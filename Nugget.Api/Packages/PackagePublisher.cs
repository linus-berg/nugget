using Nugget.Api.Catalog;
using Nugget.Api.Storage;
using NuGet.Versioning;

namespace Nugget.Api.Packages;

public enum PublishStatus {
  /// <summary>The version was added.</summary>
  CREATED,

  /// <summary>The identical package was already there (re-uploads are fine).</summary>
  EXISTS,

  /// <summary>A different package with the same ID and version exists.</summary>
  CONFLICT
}

public sealed class PublishResult {
  public required PublishStatus Status { get; init; }
  public required PackageVersionRecord Version { get; init; }
}

/// <summary>
///   Adds, unlists, relists and deletes package versions. The metadata
///   document is the commit point: package files are written before it and
///   deleted after it, so clients never see a version whose files are missing.
/// </summary>
public sealed class PackagePublisher(
  IObjectStore store,
  PackageRepository repository,
  PackageCatalog catalog,
  ChangeFeed feed,
  ILogger<PackagePublisher> logger
) {
  private readonly KeyedLock locks_ = new();

  public async Task<PublishResult> PublishAsync(InspectedPackage package,
                                                string package_path,
                                                CancellationToken ct) {
    PackageVersionRecord version = package.Record;
    string lower_id = version.LowerId;
    using IDisposable held = await locks_.AcquireAsync(lower_id, ct);

    // The cached copy is good enough to spot re-uploads; the update below
    // checks again against the stored document.
    PackageRecord? existing = await repository.GetAsync(lower_id, ct);
    PackageVersionRecord? current = existing?.Find(version.ParsedVersion);
    if (current != null) {
      return await DuplicateAsync(existing!, current, version, ct);
    }

    await UploadFilesAsync(package, package_path, ct);

    PublishResult? result = null;
    PackageRecord? updated = await repository.UpdateAsync(
                               lower_id,
                               record => {
                                 PackageVersionRecord? raced =
                                   record?.Find(version.ParsedVersion);
                                 if (raced != null) {
                                   // Another instance added it meanwhile.
                                   result = new PublishResult {
                                     Status = raced.PackageHash ==
                                              version.PackageHash
                                                ? PublishStatus.EXISTS
                                                : PublishStatus.CONFLICT,
                                     Version = raced
                                   };
                                   return record;
                                 }

                                 result = new PublishResult {
                                   Status = PublishStatus.CREATED,
                                   Version = version
                                 };
                                 return (record ??
                                         new PackageRecord {
                                           LowerId = lower_id,
                                           Versions = []
                                         }).WithVersion(version);
                               },
                               ct
                             );
    if (result!.Status == PublishStatus.CREATED) {
      await NotifyAsync(lower_id, updated, ct);
      logger.LogInformation(
        "Added {Id} {Version} ({Size:N0} bytes)",
        version.Id,
        version.Version,
        version.PackageSize
      );
    } else if (result.Status == PublishStatus.CONFLICT) {
      logger.LogWarning(
        "Concurrent upload of a different {Id} {Version}; the other one won",
        version.Id,
        version.Version
      );
    }

    return result;
  }

  /// <summary>Unlists (hides from search) or relists a version.</summary>
  /// <returns>The updated version, or <c>null</c> when it does not exist.</returns>
  public async Task<PackageVersionRecord?> SetListedAsync(string lower_id,
    NuGetVersion version, bool listed, CancellationToken ct) {
    using IDisposable held = await locks_.AcquireAsync(lower_id, ct);
    PackageVersionRecord? result = null;
    bool changed = false;
    PackageRecord? updated = await repository.UpdateAsync(
                               lower_id,
                               record => {
                                 changed = false;
                                 result = record?.Find(version);
                                 if (result == null ||
                                     result.Listed == listed) {
                                   return record;
                                 }

                                 changed = true;
                                 result = result with {
                                   Listed = listed
                                 };
                                 return record!.WithVersion(result);
                               },
                               ct
                             );
    if (changed) {
      await NotifyAsync(lower_id, updated, ct);
      logger.LogInformation(
        "{Action} {Id} {Version}",
        listed ? "Relisted" : "Unlisted",
        result!.Id,
        result.Version
      );
    }

    return result;
  }

  /// <summary>Removes a version and its files for good.</summary>
  /// <returns>The deleted version, or <c>null</c> when it does not exist.</returns>
  public async Task<PackageVersionRecord?> DeleteAsync(string lower_id,
    NuGetVersion version,
    CancellationToken ct) {
    using IDisposable held = await locks_.AcquireAsync(lower_id, ct);
    PackageVersionRecord? deleted = null;
    PackageRecord? updated = await repository.UpdateAsync(
                               lower_id,
                               record => {
                                 deleted = record?.Find(version);
                                 return deleted == null
                                          ? record
                                          : record!.WithoutVersion(
                                            deleted.LowerVersion
                                          );
                               },
                               ct
                             );
    if (deleted == null) {
      return null;
    }

    await NotifyAsync(lower_id, updated, ct);
    string lower_version = deleted.LowerVersion;
    await store.DeleteManyAsync(
      [
        StorageKeys.Nupkg(lower_id, lower_version),
        StorageKeys.Nuspec(lower_id, lower_version),
        StorageKeys.Asset(lower_id, lower_version, PackageAsset.ICON),
        StorageKeys.Asset(lower_id, lower_version, PackageAsset.README),
        StorageKeys.Asset(lower_id, lower_version, PackageAsset.LICENSE)
      ],
      ct
    );
    logger.LogInformation(
      "Deleted {Id} {Version}",
      deleted.Id,
      deleted.Version
    );
    return deleted;
  }

  private async Task<PublishResult> DuplicateAsync(PackageRecord record,
                                                   PackageVersionRecord current,
                                                   PackageVersionRecord
                                                     uploaded,
                                                   CancellationToken ct) {
    if (current.PackageHash != uploaded.PackageHash) {
      return new PublishResult {
        Status = PublishStatus.CONFLICT,
        Version = current
      };
    }

    // Re-uploading an identical package also repairs an earlier upload that
    // failed between committing the metadata and announcing the change.
    PackageSummary? known = catalog.Find(record.LowerId);
    if (catalog.IsReady &&
        known?.Versions.Any(v => v.LowerVersion == current.LowerVersion) !=
        true) {
      await NotifyAsync(record.LowerId, record, ct);
    }

    return new PublishResult {
      Status = PublishStatus.EXISTS,
      Version = current
    };
  }

  private async Task UploadFilesAsync(InspectedPackage package,
                                      string package_path,
                                      CancellationToken ct) {
    PackageVersionRecord version = package.Record;
    string lower_id = version.LowerId;
    string lower_version = version.LowerVersion;
    List<Task> uploads = [
      store.PutFileAsync(
        StorageKeys.Nupkg(lower_id, lower_version),
        package_path,
        "application/octet-stream",
        new Dictionary<string, string> {
          ["sha512"] = version.PackageHash
        },
        ct
      ),
      store.PutAsync(
        StorageKeys.Nuspec(lower_id, lower_version),
        package.Nuspec,
        new PutOptions {
          ContentType = "text/xml"
        },
        ct
      )
    ];
    if (package.Icon != null) {
      uploads.Add(
        store.PutAsync(
          StorageKeys.Asset(lower_id, lower_version, PackageAsset.ICON),
          package.Icon,
          new PutOptions {
            ContentType = version.IconContentType!
          },
          ct
        )
      );
    }

    if (package.Readme != null) {
      uploads.Add(
        store.PutAsync(
          StorageKeys.Asset(lower_id, lower_version, PackageAsset.README),
          package.Readme,
          new PutOptions {
            ContentType = "text/markdown; charset=utf-8"
          },
          ct
        )
      );
    }

    if (package.License != null) {
      uploads.Add(
        store.PutAsync(
          StorageKeys.Asset(lower_id, lower_version, PackageAsset.LICENSE),
          package.License,
          new PutOptions {
            ContentType = "text/plain; charset=utf-8"
          },
          ct
        )
      );
    }

    await Task.WhenAll(uploads);
  }

  /// <summary>Announces a change to other instances, then applies it here.</summary>
  private async Task NotifyAsync(string lower_id, PackageRecord? record,
                                 CancellationToken ct) {
    await feed.AppendAsync(ChangeKind.PACKAGE, lower_id, null, ct);
    catalog.Apply(lower_id, record);
  }
}