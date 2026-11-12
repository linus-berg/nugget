using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Nugget.Api.Configuration;
using Nugget.Api.Json;
using Nugget.Api.Packages;
using Nugget.Api.Storage;
using Nugget.Api.Vulnerabilities;

namespace Nugget.Api.Catalog;

/// <summary>The search catalog as stored in <c>catalog/snapshot.json.gz</c>.</summary>
public sealed class CatalogSnapshot {
  public int Format { get; init; } = 1;
  public DateTimeOffset Created { get; init; }

  /// <summary>Changes up to here (and <see cref="RecentChanges" />) are included.</summary>
  public long WatermarkTicks { get; init; }

  public List<string> RecentChanges { get; init; } = [];
  public List<PackageSummary> Packages { get; init; } = [];

  public Dictionary<string, Dictionary<string, long>> Downloads { get; init; } =
    new();
}

/// <summary>
///   Keeps this instance's catalog in step with S3: loads the snapshot (or
///   rebuilds it from the metadata documents), applies the change log,
///   publishes download counts and periodically writes a new snapshot.
/// </summary>
public sealed class CatalogSynchronizer(
  IObjectStore store,
  PackageRepository repository,
  PackageCatalog catalog,
  DownloadStatistics downloads,
  VulnerabilityStore vulnerabilities,
  ChangeFeed feed,
  IOptions<NuggetOptions> options,
  ILogger<CatalogSynchronizer> logger
) {
  private const string C_WATERMARK_METADATA_ = "watermark";
  private const int C_POLL_PARALLELISM_ = 16;

  private readonly SemaphoreSlim gate_ = new(1, 1);
  private DateTimeOffset last_snapshot_ = DateTimeOffset.MinValue;

  // After a rebuild the metadata documents already contain every change
  // older than this, so only download counts need replaying.
  private long rebuilt_at_ticks_;
  private bool snapshot_due_;

  private SyncOptions Sync => options.Value.Sync;

  public async Task InitializeAsync(CancellationToken ct) {
    await gate_.WaitAsync(ct);
    try {
      logger.LogInformation("Storage: {Storage}", store.Description);
      await store.InitializeAsync(ct);
      await vulnerabilities.LoadAsync(ct);
      await LoadCoreAsync(ct);
      catalog.MarkReady();
      logger.LogInformation(
        "Catalog ready with {Count} package(s)",
        catalog.Count
      );
    } finally {
      gate_.Release();
    }

    if (snapshot_due_) {
      await WriteSnapshotAsync(ct);
    }
  }

  /// <summary>Applies changes made by other instances.</summary>
  public async Task PollAsync(CancellationToken ct) {
    await gate_.WaitAsync(ct);
    try {
      await ApplyChangesAsync(ct);
    } finally {
      gate_.Release();
    }
  }

  /// <summary>Publishes download counts recorded since the last flush.</summary>
  public async Task FlushDownloadsAsync(CancellationToken ct) {
    Dictionary<string, Dictionary<string, long>> pending =
      downloads.TakePending();
    if (pending.Count == 0) {
      return;
    }

    try {
      await feed.AppendAsync(
        ChangeKind.DOWNLOADS,
        Guid.NewGuid().ToString("N"),
        NuggetJson.SerializeGzip(
          pending,
          NuggetJson.S_CONTEXT.DictionaryStringDictionaryStringInt64
        ),
        ct
      );
    } catch {
      downloads.RestorePending(pending);
      throw;
    }
  }

  /// <summary>
  ///   Writes a snapshot when one is due and no other instance wrote one
  ///   recently. Also notices when this instance fell so far behind that
  ///   the change log no longer covers the gap, and reloads.
  /// </summary>
  public async Task<bool> MaybeWriteSnapshotAsync(CancellationToken ct) {
    TimeSpan interval = TimeSpan.FromMinutes(Sync.SnapshotIntervalMinutes);
    DateTimeOffset now = DateTimeOffset.UtcNow;
    if (!snapshot_due_ && now - last_snapshot_ < interval) {
      return false;
    }

    ObjectInfo? current = await store.HeadAsync(StorageKeys.C_SNAPSHOT, ct);
    if (current != null && await ReloadIfBehindAsync(current, ct)) {
      return false;
    }

    if (!snapshot_due_ &&
        current != null &&
        now - current.LastModified < interval) {
      last_snapshot_ = current.LastModified;
      return false;
    }

    await WriteSnapshotAsync(ct);
    return true;
  }

  public async Task WriteSnapshotAsync(CancellationToken ct) {
    await FlushDownloadsAsync(ct);
    await gate_.WaitAsync(ct);
    string path = Path.Combine(
      Path.GetTempPath(),
      $"nugget-snapshot-{Guid.NewGuid():N}.json.gz"
    );
    try {
      (long watermark, List<string> recent) = feed.GetCheckpoint();
      CatalogSnapshot snapshot = new() {
        Created = DateTimeOffset.UtcNow,
        WatermarkTicks = watermark,
        RecentChanges = recent,
        Packages = catalog.GetAll().ToList(),
        Downloads = downloads.Export()
      };
      await using (FileStream file = File.Create(path)) {
        await using GZipStream gzip = new(file, CompressionLevel.Optimal);
        await JsonSerializer.SerializeAsync(
          gzip,
          snapshot,
          NuggetJson.S_CONTEXT.CatalogSnapshot,
          ct
        );
      }

      long size = new FileInfo(path).Length;
      await store.PutFileAsync(
        StorageKeys.C_SNAPSHOT,
        path,
        "application/gzip",
        new Dictionary<string, string> {
          [C_WATERMARK_METADATA_] =
            watermark.ToString(CultureInfo.InvariantCulture)
        },
        ct
      );
      last_snapshot_ = snapshot.Created;
      snapshot_due_ = false;

      long cutoff = watermark -
                    TimeSpan.FromHours(Sync.ChangeRetentionHours).Ticks;
      int removed = await feed.DeleteOlderThanAsync(cutoff, ct);
      logger.LogInformation(
        "Wrote catalog snapshot: {Packages} package(s), {Size:N0} bytes; " +
        "removed {Removed} old change(s)",
        snapshot.Packages.Count,
        size,
        removed
      );
    } finally {
      gate_.Release();
      File.Delete(path);
    }
  }

  /// <summary>
  ///   Rebuilds the catalog from every metadata document in S3 and writes a
  ///   new snapshot (recovery after a lost or damaged snapshot).
  /// </summary>
  public async Task RebuildAsync(CancellationToken ct) {
    await gate_.WaitAsync(ct);
    try {
      long started = DateTime.UtcNow.Ticks;
      // Download counts and the change log position stay as they are.
      await RebuildCoreAsync(false, ct);
      await ApplyChangesAsync(ct);
      await ReapplyPackageChangesAsync(started, ct);
    } finally {
      gate_.Release();
    }

    await WriteSnapshotAsync(ct);
  }

  /// <summary>
  ///   Replaces the catalog with the snapshot (or a rebuild) and applies the
  ///   changes made since.
  /// </summary>
  private async Task LoadCoreAsync(CancellationToken ct) {
    long started = DateTime.UtcNow.Ticks;
    if (!await TryLoadSnapshotAsync(ct)) {
      await RebuildCoreAsync(true, ct);
    }

    await ApplyChangesAsync(ct);
    await ReapplyPackageChangesAsync(started, ct);
  }

  /// <param name="fresh">
  ///   There is no snapshot: start the change log from the beginning to
  ///   recover the download counts recorded there.
  /// </param>
  /// <param name="ct">Cancellation.</param>
  private async Task RebuildCoreAsync(bool fresh, CancellationToken ct) {
    logger.LogInformation("Rebuilding the catalog from package metadata");
    DateTime started = DateTime.UtcNow;
    if (fresh) {
      rebuilt_at_ticks_ = started.Ticks - ChangeFeed.S_WINDOW.Ticks;
      feed.Reset(0, []);
      downloads.Load(new Dictionary<string, Dictionary<string, long>>());
    }

    List<string> ids = [];
    await foreach (string lower_id in repository.ListIdsAsync(ct)) {
      ids.Add(lower_id);
    }

    ConcurrentBag<PackageSummary> summaries = [];
    int loaded = 0;
    await Parallel.ForEachAsync(
      ids,
      new ParallelOptions {
        MaxDegreeOfParallelism = Math.Max(1, Sync.RebuildParallelism),
        CancellationToken = ct
      },
      async (lower_id, token) => {
        PackageRecord? record = await repository.GetFreshAsync(lower_id, token);
        if (record is { Versions.Count: > 0 }) {
          summaries.Add(PackageSummary.FromRecord(record));
        }

        int count = Interlocked.Increment(ref loaded);
        if (count % 10_000 == 0) {
          logger.LogInformation(
            "Loaded {Count:N0} of {Total:N0} packages",
            count,
            ids.Count
          );
        }
      }
    );
    catalog.Load(summaries);
    snapshot_due_ = true;
    logger.LogInformation(
      "Rebuilt the catalog with {Count:N0} package(s) in {Elapsed:0.0} s",
      summaries.Count,
      (DateTime.UtcNow - started).TotalSeconds
    );
  }

  private async Task<bool> TryLoadSnapshotAsync(CancellationToken ct) {
    await using ObjectReadStream? stream = await store.OpenReadAsync(
                                             StorageKeys.C_SNAPSHOT,
                                             ct
                                           );
    if (stream == null) {
      return false;
    }

    CatalogSnapshot? snapshot;
    try {
      await using GZipStream gzip = new(
        stream.Stream,
        CompressionMode.Decompress
      );
      snapshot = await JsonSerializer.DeserializeAsync(
                   gzip,
                   NuggetJson.S_CONTEXT.CatalogSnapshot,
                   ct
                 );
    } catch (Exception ex) when (ex is JsonException or InvalidDataException) {
      logger.LogWarning(ex, "The catalog snapshot is unreadable; rebuilding");
      return false;
    }

    if (snapshot == null) {
      return false;
    }

    catalog.Load(snapshot.Packages);
    downloads.Load(snapshot.Downloads);
    feed.Reset(snapshot.WatermarkTicks, snapshot.RecentChanges);
    rebuilt_at_ticks_ = 0;
    last_snapshot_ = stream.Info.LastModified;
    logger.LogInformation(
      "Loaded catalog snapshot from {Created:u} with {Count:N0} package(s)",
      snapshot.Created,
      snapshot.Packages.Count
    );
    return true;
  }

  private async Task<bool> ReloadIfBehindAsync(ObjectInfo snapshot,
                                               CancellationToken ct) {
    if (!snapshot.Metadata.TryGetValue(
          C_WATERMARK_METADATA_,
          out string? value
        ) ||
        !long.TryParse(
          value,
          NumberStyles.None,
          CultureInfo.InvariantCulture,
          out long watermark
        )) {
      return false;
    }

    long retention = TimeSpan.FromHours(Sync.ChangeRetentionHours).Ticks;
    if (feed.CursorTicks >= watermark - retention / 2) {
      return false;
    }

    logger.LogWarning(
      "This instance is too far behind the change log; reloading the snapshot"
    );
    await gate_.WaitAsync(ct);
    try {
      await LoadCoreAsync(ct);
    } finally {
      gate_.Release();
    }

    return true;
  }

  /// <summary>
  ///   Loading replaces the whole catalog, which can undo packages this
  ///   instance published while it was loading (their changes are already
  ///   marked as seen). Re-reading every package changed since the load
  ///   started puts them back; applying a change twice is harmless.
  /// </summary>
  private async Task ReapplyPackageChangesAsync(long since_ticks,
                                                CancellationToken ct) {
    IReadOnlyList<ChangeEntry> changes = await feed.ListSinceAsync(
                                           since_ticks -
                                           ChangeFeed.S_WINDOW.Ticks,
                                           ct
                                         );
    await RefreshPackagesAsync(
      changes.Where(c => c.Kind == ChangeKind.PACKAGE)
             .Select(c => c.Subject)
             .ToHashSet(StringComparer.Ordinal),
      ct
    );
  }

  private async Task RefreshPackagesAsync(IReadOnlyCollection<string> ids,
                                          CancellationToken ct) {
    await Parallel.ForEachAsync(
      ids,
      new ParallelOptions {
        MaxDegreeOfParallelism = C_POLL_PARALLELISM_,
        CancellationToken = ct
      },
      async (lower_id, token) => {
        PackageRecord? record = await repository.GetFreshAsync(lower_id, token);
        catalog.Apply(lower_id, record);
      }
    );
  }

  private async Task ApplyChangesAsync(CancellationToken ct) {
    IReadOnlyList<ChangeEntry> changes = await feed.ReadNewAsync(ct);
    if (changes.Count == 0) {
      return;
    }

    HashSet<string> packages = new(StringComparer.Ordinal);
    bool vulnerabilities_changed = false;
    foreach (ChangeEntry change in changes) {
      switch (change.Kind) {
        case ChangeKind.PACKAGE:
          if (change.Ticks >= rebuilt_at_ticks_) {
            packages.Add(change.Subject);
          }

          break;
        case ChangeKind.DOWNLOADS:
          await ApplyDownloadsAsync(change, ct);
          break;
        case ChangeKind.VULNERABILITIES:
          vulnerabilities_changed = true;
          break;
      }
    }

    if (vulnerabilities_changed) {
      await vulnerabilities.LoadAsync(ct);
    }

    await RefreshPackagesAsync(packages, ct);
    if (packages.Count > 0) {
      logger.LogDebug("Applied {Count} package change(s)", packages.Count);
    }
  }

  private async Task ApplyDownloadsAsync(ChangeEntry change,
                                         CancellationToken ct) {
    StoredObject? stored = await store.GetAsync(change.Key, ct);
    if (stored == null) {
      return;
    }

    try {
      Dictionary<string, Dictionary<string, long>>? delta =
        NuggetJson.DeserializeGzip(
          stored.Content,
          NuggetJson.S_CONTEXT.DictionaryStringDictionaryStringInt64
        );
      if (delta != null) {
        downloads.Apply(delta);
      }
    } catch (Exception ex) when (ex is JsonException or InvalidDataException) {
      logger.LogWarning(
        ex,
        "Ignoring unreadable download counts {Key}",
        change.Key
      );
    }
  }
}