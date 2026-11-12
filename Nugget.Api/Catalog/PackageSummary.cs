using System.Text.Json;
using System.Text.Json.Serialization;
using Nugget.Api.Packages;

namespace Nugget.Api.Catalog;

/// <summary>
///   What the in-memory catalog keeps per package ID: every version (for the
///   flat container and search) plus search metadata of the newest versions.
///   This is also the unit stored in the catalog snapshot.
/// </summary>
public sealed class PackageSummary {
  // Search filter combinations (prerelease x SemVer 2.0.0).
  private const int C_FILTERS_ = 4;

  private int[]? latest_;

  /// <summary>Display ID: exact casing of the newest version.</summary>
  public required string Id { get; init; }

  /// <summary><see cref="PackageRecord.Revision" /> this summary was built from.</summary>
  public long Revision { get; init; }

  /// <summary>All versions (listed and unlisted), ordered by precedence.</summary>
  public required List<SummaryVersion> Versions { get; init; }

  /// <summary>Search metadata for the newest listed versions.</summary>
  public required List<SearchDetails> Details { get; init; }

  [JsonIgnore]
  public string LowerId => field ??= Id.ToLowerInvariant();

  /// <summary>Serialized flat container <c>index.json</c>, built on first use.</summary>
  [JsonIgnore]
  public byte[]? VersionsJson { get; set; }

  public static PackageSummary FromRecord(PackageRecord record) {
    List<SummaryVersion> versions = record.Versions
                                          .Select(
                                            v => new SummaryVersion {
                                              Version =
                                                string.Intern(v.Version),
                                              Listed = v.Listed,
                                              SemVer2 = v.IsSemVer2
                                            }
                                          )
                                          .ToList();
    PackageSummary summary = new() {
      Id = record.DisplayId,
      Revision = record.Revision,
      Versions = versions,
      Details = []
    };
    HashSet<int> latest = summary.GetLatestIndexes()
                                 .Where(i => i >= 0)
                                 .ToHashSet();
    foreach (int index in latest.Order()) {
      summary.Details.Add(SearchDetails.FromRecord(record.Versions[index]));
    }

    return summary;
  }

  /// <summary>
  ///   The newest listed version matching the search filters, or <c>null</c>
  ///   when the package has none (then it is not shown in search).
  /// </summary>
  public SummaryVersion? GetLatest(bool prerelease, bool semver2) {
    int index = GetLatestIndexes()[FilterIndex(prerelease, semver2)];
    return index < 0 ? null : Versions[index];
  }

  public SearchDetails? GetDetails(SummaryVersion version) {
    foreach (SearchDetails details in Details) {
      if (details.Version == version.Version) {
        return details;
      }
    }

    return null;
  }

  public IEnumerable<SummaryVersion> GetListed(bool prerelease, bool semver2) {
    return Versions.Where(v => v.Matches(prerelease, semver2));
  }

  private int[] GetLatestIndexes() {
    if (latest_ != null) {
      return latest_;
    }

    int[] latest = new int[C_FILTERS_];
    for (int filter = 0; filter < C_FILTERS_; filter++) {
      bool prerelease = (filter & 1) != 0;
      bool semver2 = (filter & 2) != 0;
      latest[filter] =
        Versions.FindLastIndex(v => v.Matches(prerelease, semver2));
    }

    latest_ = latest;
    return latest;
  }

  private static int FilterIndex(bool prerelease, bool semver2) {
    return (prerelease ? 1 : 0) | (semver2 ? 2 : 0);
  }
}

/// <remarks>
///   Stored compactly in snapshots (millions of them): <c>"1.0.0"</c>, or
///   with flags after a space, <c>"1.0.0 u"</c> (unlisted) and
///   <c>"1.0.0 s"</c> (SemVer 2.0.0).
/// </remarks>
[JsonConverter(typeof(SummaryVersionConverter))]
public sealed class SummaryVersion {
  private int prerelease_ = -1;

  /// <summary>Normalized version including build metadata.</summary>
  public required string Version { get; init; }

  public bool Listed { get; init; } = true;
  public bool SemVer2 { get; init; }

  /// <summary>Lower case normalized version without build metadata.</summary>
  [JsonIgnore]
  public string LowerVersion => field ??= ToLowerVersion(Version);

  [JsonIgnore]
  public bool Prerelease {
    get {
      if (prerelease_ < 0) {
        // Normalized versions only use '-' to start the prerelease label.
        int plus = Version.IndexOf('+');
        string without_metadata = plus < 0 ? Version : Version[..plus];
        prerelease_ = without_metadata.Contains('-') ? 1 : 0;
      }

      return prerelease_ == 1;
    }
  }

  public bool Matches(bool prerelease, bool semver2) {
    return Listed && (prerelease || !Prerelease) && (semver2 || !SemVer2);
  }

  private static string ToLowerVersion(string version) {
    int plus = version.IndexOf('+');
    string without_metadata = plus < 0 ? version : version[..plus];
    return without_metadata.ToLowerInvariant();
  }
}

/// <summary>The metadata search results show for a package.</summary>
public sealed class SearchDetails {
  public required string Version { get; init; }
  public string Description { get; init; } = "";
  public string? Summary { get; init; }
  public string? Title { get; init; }
  public string? IconUrl { get; init; }
  public bool HasIcon { get; init; }
  public string? LicenseUrl { get; init; }
  public string? LicenseExpression { get; init; }
  public bool HasLicenseFile { get; init; }
  public string? ProjectUrl { get; init; }
  public string[] Tags { get; init; } = [];
  public string[] Authors { get; init; } = [];
  public string[] PackageTypes { get; init; } = [];
  public DateTimeOffset Published { get; init; }

  public static SearchDetails FromRecord(PackageVersionRecord record) {
    return new SearchDetails {
      Version = record.Version,
      Description = record.Description,
      Summary = record.Summary,
      Title = record.Title,
      IconUrl = record.IconUrl,
      HasIcon = record.HasIcon,
      LicenseUrl = record.LicenseUrl,
      LicenseExpression = record.LicenseExpression,
      HasLicenseFile = record.HasLicenseFile,
      ProjectUrl = record.ProjectUrl,
      Tags = record.GetTags().ToArray(),
      Authors = record.GetAuthors().ToArray(),
      PackageTypes = record.PackageTypes.Count == 0
                       ? []
                       : record.PackageTypes.Select(t => t.Name).ToArray(),
      Published = record.Published
    };
  }
}

public sealed class SummaryVersionConverter : JsonConverter<SummaryVersion> {
  public override SummaryVersion Read(ref Utf8JsonReader reader,
                                      Type type_to_convert,
                                      JsonSerializerOptions options) {
    string value = reader.GetString() ??
                   throw new JsonException("Expected a version string.");
    int space = value.IndexOf(' ');
    // The same few thousand version strings ("1.0.0", "2.1.3", ...) occur
    // millions of times across packages; share one copy of each.
    if (space < 0) {
      return new SummaryVersion {
        Version = string.Intern(value)
      };
    }

    ReadOnlySpan<char> flags = value.AsSpan(space + 1);
    return new SummaryVersion {
      Version = string.Intern(value[..space]),
      Listed = !flags.Contains('u'),
      SemVer2 = flags.Contains('s')
    };
  }

  public override void Write(Utf8JsonWriter writer, SummaryVersion value,
                             JsonSerializerOptions options) {
    if (value.Listed && !value.SemVer2) {
      writer.WriteStringValue(value.Version);
      return;
    }

    writer.WriteStringValue(
      $"{value.Version} {(value.Listed ? "" : "u")}{(value.SemVer2 ? "s" : "")}"
    );
  }
}