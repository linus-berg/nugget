using Nugget.Api.Packages;
using Nugget.Api.Vulnerabilities;
using NuGet.Versioning;

namespace Nugget.Api.Protocol;

/// <summary>Builds registration documents (package metadata resource) like nuget.org's.</summary>
public sealed class RegistrationBuilder(
  NuGetUrls urls,
  string hive,
  VulnerabilityStore vulnerabilities
) {
  /// <summary>Versions per registration page.</summary>
  public const int C_PAGE_SIZE = 64;

  /// <summary>Above this many versions, pages are not inlined in the index.</summary>
  public const int C_INLINE_LIMIT = 128;

  // nuget.org reports unlisted versions as published on this date.
  private static readonly DateTimeOffset S_UNLISTED_PUBLISHED_ =
    new(1900, 1, 1, 0, 0, 0, TimeSpan.Zero);

  /// <summary>Versions shown in this hive (SemVer 1 hives hide SemVer 2.0.0 packages).</summary>
  public List<PackageVersionRecord> GetVersions(PackageRecord record) {
    return record.Versions.Where(IsVisible).ToList();
  }

  public bool IsVisible(PackageVersionRecord version) {
    return !version.IsSemVer2 || NuGetUrls.IncludesSemVer2(hive);
  }

  public RegistrationIndex BuildIndex(PackageRecord record,
                                      List<PackageVersionRecord> versions) {
    bool inline = versions.Count <= C_INLINE_LIMIT;
    List<RegistrationPage> pages = versions
                                   .Chunk(C_PAGE_SIZE)
                                   .Select(
                                     chunk => BuildPage(
                                       record,
                                       chunk,
                                       inline,
                                       false
                                     )
                                   )
                                   .ToList();
    return new RegistrationIndex {
      Url = urls.RegistrationIndex(hive, record.LowerId),
      CommitId = record.CommitId,
      CommitTimeStamp = record.CommitTimeStamp,
      Count = pages.Count,
      Items = pages
    };
  }

  public RegistrationPage BuildPage(PackageRecord record,
                                    IReadOnlyList<PackageVersionRecord>
                                      versions,
                                    bool include_items, bool standalone) {
    string lower = versions[0].ParsedVersion.ToNormalizedString();
    string upper = versions[^1].ParsedVersion.ToNormalizedString();
    string index_url = urls.RegistrationIndex(hive, record.LowerId);
    return new RegistrationPage {
      Url = include_items && !standalone
              ? $"{index_url}#page/{lower}/{upper}"
              : urls.RegistrationPage(
                hive,
                record.LowerId,
                lower.ToLowerInvariant(),
                upper.ToLowerInvariant()
              ),
      CommitId = record.CommitId,
      CommitTimeStamp = record.CommitTimeStamp,
      Count = versions.Count,
      Items = include_items
                ? versions.Select(v => BuildLeafItem(record, v)).ToList()
                : null,
      Parent = index_url,
      Lower = lower,
      Upper = upper,
      Context = standalone ? RegistrationContext.S_INDEX : null
    };
  }

  /// <summary>Versions of a page requested as <c>page/{lower}/{upper}.json</c>.</summary>
  public static List<PackageVersionRecord> SelectPage(
    List<PackageVersionRecord> versions, NuGetVersion lower,
    NuGetVersion upper) {
    return versions.Where(
                     v => VersionComparer.Default.Compare(
                            v.ParsedVersion,
                            lower
                          ) >=
                          0 &&
                          VersionComparer.Default.Compare(
                            v.ParsedVersion,
                            upper
                          ) <=
                          0
                   )
                   .ToList();
  }

  public RegistrationLeafItem BuildLeafItem(PackageRecord record,
                                            PackageVersionRecord version) {
    return new RegistrationLeafItem {
      Url = urls.RegistrationLeaf(hive, record.LowerId, version.LowerVersion),
      CommitId = record.CommitId,
      CommitTimeStamp = record.CommitTimeStamp,
      CatalogEntry = BuildCatalogEntry(version, false),
      PackageContent =
        urls.PackageContent(record.LowerId, version.LowerVersion),
      Registration = urls.RegistrationIndex(hive, record.LowerId)
    };
  }

  public RegistrationLeaf BuildLeaf(PackageRecord record,
                                    PackageVersionRecord version) {
    return new RegistrationLeaf {
      Url = urls.RegistrationLeaf(hive, record.LowerId, version.LowerVersion),
      CatalogEntry = urls.CatalogLeaf(record.LowerId, version.LowerVersion),
      Listed = version.Listed,
      PackageContent =
        urls.PackageContent(record.LowerId, version.LowerVersion),
      Published = Published(version),
      Registration = urls.RegistrationIndex(hive, record.LowerId)
    };
  }

  /// <param name="version">The version.</param>
  /// <param name="full">Adds the catalog leaf only fields (hash, size, ...).</param>
  public CatalogEntry BuildCatalogEntry(PackageVersionRecord version,
                                        bool full) {
    string lower_id = version.LowerId;
    string lower_version = version.LowerVersion;
    string url = urls.CatalogLeaf(lower_id, lower_version);
    List<DependencyGroupEntry> groups = version.DependencyGroups
                                               .Select(g => BuildGroup(url, g))
                                               .ToList();
    return new CatalogEntry {
      Url = url,
      Authors = version.Authors,
      DependencyGroups = groups.Count == 0 ? null : groups,
      Description = version.Description,
      IconUrl = version.HasIcon
                  ? urls.Icon(lower_id, lower_version)
                  : version.IconUrl,
      Id = version.Id,
      Language = version.Language ?? "",
      LicenseExpression = version.LicenseExpression,
      LicenseUrl = LicenseUrl(
                     urls,
                     lower_id,
                     lower_version,
                     version.LicenseExpression,
                     version.HasLicenseFile,
                     version.LicenseUrl
                   ) ??
                   "",
      ReadmeUrl = version.HasReadme
                    ? urls.Readme(lower_id, lower_version)
                    : null,
      Listed = version.Listed,
      MinClientVersion = version.MinClientVersion ?? "",
      PackageContent = urls.PackageContent(lower_id, lower_version),
      ProjectUrl = version.ProjectUrl ?? "",
      Published = Published(version),
      RequireLicenseAcceptance = version.RequireLicenseAcceptance,
      Summary = version.Summary ?? "",
      Tags = version.GetTags().ToList(),
      Title = version.Title ?? "",
      Version = version.Version,
      Vulnerabilities = vulnerabilities.Find(lower_id, version.ParsedVersion),
      VerbatimVersion = full ? version.OriginalVersion : null,
      IsPrerelease = full ? version.IsPrerelease : null,
      PackageHash = full ? version.PackageHash : null,
      PackageHashAlgorithm = full ? "SHA512" : null,
      PackageSize = full ? version.PackageSize : null,
      Copyright = full ? version.Copyright : null,
      ReleaseNotes = full ? version.ReleaseNotes : null,
      Repository = full ? version.Repository?.Url : null,
      PackageTypes = full && version.PackageTypes.Count > 0
                       ? version.PackageTypes
                                .Select(
                                  t => new PackageTypeEntry {
                                    Name = t.Name,
                                    Version = t.Version
                                  }
                                )
                                .ToList()
                       : null
    };
  }

  /// <summary>
  ///   License link like nuget.org's: expressions point at
  ///   licenses.nuget.org, embedded license files are served from here.
  /// </summary>
  public static string? LicenseUrl(NuGetUrls urls, string lower_id,
                                   string lower_version, string? expression,
                                   bool has_file, string? license_url) {
    if (!string.IsNullOrEmpty(expression)) {
      return $"https://licenses.nuget.org/{Uri.EscapeDataString(expression)}";
    }

    return has_file ? urls.License(lower_id, lower_version) : license_url;
  }

  private DependencyGroupEntry BuildGroup(string catalog_url,
                                          DependencyGroupRecord group) {
    string group_url = $"{catalog_url}#dependencygroup" +
                       (group.TargetFramework == null
                          ? ""
                          : "/" + group.TargetFramework.ToLowerInvariant());
    List<DependencyEntry> dependencies = group.Dependencies
                                              .Select(
                                                d => new DependencyEntry {
                                                  Url =
                                                    $"{group_url}/{d.Id.ToLowerInvariant()}",
                                                  Id = d.Id,
                                                  Range = d.Range,
                                                  Registration =
                                                    urls.RegistrationIndex(
                                                      hive,
                                                      d.Id.ToLowerInvariant()
                                                    )
                                                }
                                              )
                                              .ToList();
    return new DependencyGroupEntry {
      Url = group_url,
      Dependencies = dependencies.Count == 0 ? null : dependencies,
      TargetFramework = group.TargetFramework
    };
  }

  private static DateTimeOffset Published(PackageVersionRecord version) {
    return version.Listed ? version.Published : S_UNLISTED_PUBLISHED_;
  }
}