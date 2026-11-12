using System.Text.Json.Serialization;
using NuGet.Versioning;

namespace Nugget.Api.Packages;

/// <summary>
///   Metadata document of one package ID with all of its versions
///   (<c>metadata/{id}.json.gz</c>). Registration documents and the search
///   catalog are built from it.
/// </summary>
public sealed record PackageRecord {
  /// <summary>Lower case package ID (the storage key).</summary>
  public required string LowerId { get; init; }

  /// <summary>All versions, ordered by SemVer precedence.</summary>
  public required List<PackageVersionRecord> Versions { get; init; }

  /// <summary>
  ///   Incremented by every write. Each write is conditional on the previous
  ///   document, so a higher revision is always newer, whichever instance or
  ///   clock wrote it.
  /// </summary>
  public long Revision { get; init; }

  public string CommitId { get; init; } = Guid.NewGuid().ToString();
  public DateTimeOffset CommitTimeStamp { get; init; } = DateTimeOffset.UtcNow;

  /// <summary>
  ///   The package ID as displayed: the exact casing of the newest version,
  ///   like nuget.org.
  /// </summary>
  [JsonIgnore]
  public string DisplayId => Versions.Count == 0 ? LowerId : Versions[^1].Id;

  public PackageVersionRecord? Find(NuGetVersion version) {
    string lower_version = version.ToNormalizedString().ToLowerInvariant();
    return Versions.FirstOrDefault(v => v.LowerVersion == lower_version);
  }

  /// <summary>A copy with <paramref name="version" /> added or replaced.</summary>
  public PackageRecord WithVersion(PackageVersionRecord version) {
    List<PackageVersionRecord> versions = Versions
                                          .Where(
                                            v => v.LowerVersion !=
                                                 version.LowerVersion
                                          )
                                          .Append(version)
                                          .OrderBy(
                                            v => v.ParsedVersion,
                                            VersionComparer.Default
                                          )
                                          .ToList();
    return this with {
      Versions = versions,
      Revision = Revision + 1,
      CommitId = Guid.NewGuid().ToString(),
      CommitTimeStamp = DateTimeOffset.UtcNow
    };
  }

  /// <summary>A copy without <paramref name="lower_version" />.</summary>
  public PackageRecord WithoutVersion(string lower_version) {
    return this with {
      Versions = Versions.Where(v => v.LowerVersion != lower_version).ToList(),
      Revision = Revision + 1,
      CommitId = Guid.NewGuid().ToString(),
      CommitTimeStamp = DateTimeOffset.UtcNow
    };
  }
}

/// <summary>Everything known about one package version, taken from its nuspec.</summary>
public sealed record PackageVersionRecord {
  /// <summary>Package ID exactly as written in the nuspec.</summary>
  public required string Id { get; init; }

  /// <summary>Normalized version including build metadata (<c>1.0.0+abc</c>).</summary>
  public required string Version {
    get;
    init {
      field = value;
      // Parsed here so copies made with "with" never keep a stale version.
      ParsedVersion = NuGetVersion.Parse(value);
    }
  }

  /// <summary>Version exactly as written in the nuspec (<c>1.0</c>).</summary>
  public string? OriginalVersion { get; init; }

  public bool Listed { get; init; } = true;
  public bool IsSemVer2 { get; init; }
  public DateTimeOffset Published { get; init; }

  public string Authors { get; init; } = "";
  public string Description { get; init; } = "";
  public string? Summary { get; init; }
  public string? Title { get; init; }
  public string? Tags { get; init; }
  public string? Copyright { get; init; }
  public string? ReleaseNotes { get; init; }
  public string? Language { get; init; }
  public string? ProjectUrl { get; init; }
  public string? IconUrl { get; init; }
  public string? LicenseUrl { get; init; }
  public string? LicenseExpression { get; init; }
  public string? MinClientVersion { get; init; }
  public bool RequireLicenseAcceptance { get; init; }
  public bool DevelopmentDependency { get; init; }

  /// <summary>Content type of the embedded icon, when there is one.</summary>
  public string? IconContentType { get; init; }

  public bool HasReadme { get; init; }
  public bool HasLicenseFile { get; init; }

  public RepositoryRecord? Repository { get; init; }
  public List<DependencyGroupRecord> DependencyGroups { get; init; } = new();
  public List<PackageTypeRecord> PackageTypes { get; init; } = new();

  public long PackageSize { get; init; }

  /// <summary>Base64 SHA-512 of the <c>.nupkg</c>, like nuget.org's catalog.</summary>
  public string PackageHash { get; init; } = "";

  [JsonIgnore]
  public NuGetVersion ParsedVersion { get; private init; } = null!;

  /// <summary>Lower case normalized version without metadata (the storage key).</summary>
  [JsonIgnore]
  public string LowerVersion =>
    ParsedVersion.ToNormalizedString().ToLowerInvariant();

  [JsonIgnore]
  public string LowerId => Id.ToLowerInvariant();

  [JsonIgnore]
  public bool IsPrerelease => ParsedVersion.IsPrerelease;

  [JsonIgnore]
  public bool HasIcon => IconContentType != null;

  public IEnumerable<string> GetTags() {
    return (Tags ?? "").Split(
      [
        ' ',
        ',',
        ';',
        '\t'
      ],
      StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
    );
  }

  public IEnumerable<string> GetAuthors() {
    return Authors.Split(
      ',',
      StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
    );
  }
}

public sealed record DependencyGroupRecord {
  /// <summary>Short folder name (<c>net8.0</c>), or <c>null</c> for any framework.</summary>
  public string? TargetFramework { get; init; }

  public List<DependencyRecord> Dependencies { get; init; } = new();
}

public sealed record DependencyRecord {
  public required string Id { get; init; }

  /// <summary>Normalized version range, e.g. <c>[1.0.0, )</c>.</summary>
  public required string Range { get; init; }
}

public sealed record PackageTypeRecord {
  public required string Name { get; init; }
  public string? Version { get; init; }
}

public sealed record RepositoryRecord {
  public string? Type { get; init; }
  public string? Url { get; init; }
  public string? Branch { get; init; }
  public string? Commit { get; init; }
}