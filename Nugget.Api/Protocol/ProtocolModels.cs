using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nugget.Api.Protocol;

// JSON documents of the NuGet V3 protocol, shaped like api.nuget.org's.

public sealed class ServiceIndexDocument {
  public string Version { get; init; } = "3.0.0";
  public required List<ServiceResource> Resources { get; init; }

  [JsonPropertyName("@context")]
  public ServiceIndexContext Context { get; init; } = new();
}

public sealed class ServiceResource {
  [JsonPropertyName("@id")]
  public required string Url { get; init; }

  [JsonPropertyName("@type")]
  public required string Type { get; init; }

  public string? Comment { get; init; }
  public string? ClientVersion { get; init; }
}

public sealed class ServiceIndexContext {
  [JsonPropertyName("@vocab")]
  public string Vocab { get; init; } = "http://schema.nuget.org/services#";

  public string Comment { get; init; } =
    "http://www.w3.org/2000/01/rdf-schema#comment";
}

public sealed class RegistrationIndex {
  [JsonPropertyName("@id")]
  public required string Url { get; init; }

  [JsonPropertyName("@type")]
  public string[] Type { get; init; } = [
    "catalog:CatalogRoot",
    "PackageRegistration",
    "catalog:Permalink"
  ];

  public required string CommitId { get; init; }
  public required DateTimeOffset CommitTimeStamp { get; init; }
  public required int Count { get; init; }
  public required List<RegistrationPage> Items { get; init; }

  [JsonPropertyName("@context")]
  public JsonElement Context { get; init; } = RegistrationContext.S_INDEX;
}

public sealed class RegistrationPage {
  [JsonPropertyName("@id")]
  public required string Url { get; init; }

  [JsonPropertyName("@type")]
  public string Type { get; init; } = "catalog:CatalogPage";

  public required string CommitId { get; init; }
  public required DateTimeOffset CommitTimeStamp { get; init; }
  public required int Count { get; init; }

  /// <summary>Omitted when the page has to be fetched separately.</summary>
  public List<RegistrationLeafItem>? Items { get; init; }

  public required string Parent { get; init; }
  public required string Lower { get; init; }
  public required string Upper { get; init; }

  /// <summary>Only set on standalone page documents.</summary>
  [JsonPropertyName("@context")]
  public JsonElement? Context { get; init; }
}

public sealed class RegistrationLeafItem {
  [JsonPropertyName("@id")]
  public required string Url { get; init; }

  [JsonPropertyName("@type")]
  public string Type { get; init; } = "Package";

  public required string CommitId { get; init; }
  public required DateTimeOffset CommitTimeStamp { get; init; }
  public required CatalogEntry CatalogEntry { get; init; }
  public required string PackageContent { get; init; }
  public required string Registration { get; init; }
}

/// <summary>The standalone registration leaf (<c>{version}.json</c>).</summary>
public sealed class RegistrationLeaf {
  [JsonPropertyName("@id")]
  public required string Url { get; init; }

  [JsonPropertyName("@type")]
  public string[] Type { get; init; } = [
    "Package",
    "http://schema.nuget.org/catalog#Permalink"
  ];

  /// <summary>URL of the catalog style leaf.</summary>
  public required string CatalogEntry { get; init; }

  public required bool Listed { get; init; }
  public required string PackageContent { get; init; }
  public required DateTimeOffset Published { get; init; }
  public required string Registration { get; init; }

  [JsonPropertyName("@context")]
  public JsonElement Context { get; init; } = RegistrationContext.S_LEAF;
}

/// <summary>
///   Package details: inlined into registration pages and served on its own
///   as a catalog leaf (with the hash and size).
/// </summary>
public sealed class CatalogEntry {
  [JsonPropertyName("@id")]
  public required string Url { get; init; }

  [JsonPropertyName("@type")]
  public string Type { get; init; } = "PackageDetails";

  public required string Authors { get; init; }
  public List<DependencyGroupEntry>? DependencyGroups { get; init; }
  public required string Description { get; init; }
  public string? IconUrl { get; init; }
  public required string Id { get; init; }
  public string Language { get; init; } = "";
  public string? LicenseExpression { get; init; }
  public string LicenseUrl { get; init; } = "";
  public string? ReadmeUrl { get; init; }
  public required bool Listed { get; init; }
  public string MinClientVersion { get; init; } = "";
  public required string PackageContent { get; init; }
  public string ProjectUrl { get; init; } = "";
  public required DateTimeOffset Published { get; init; }
  public required bool RequireLicenseAcceptance { get; init; }
  public string Summary { get; init; } = "";
  public required List<string> Tags { get; init; }
  public string Title { get; init; } = "";
  public required string Version { get; init; }
  public List<VulnerabilityEntry>? Vulnerabilities { get; init; }

  // Catalog leaf only.
  public string? VerbatimVersion { get; init; }
  public bool? IsPrerelease { get; init; }
  public string? PackageHash { get; init; }
  public string? PackageHashAlgorithm { get; init; }
  public long? PackageSize { get; init; }
  public string? Copyright { get; init; }
  public string? ReleaseNotes { get; init; }
  public string? Repository { get; init; }
  public List<PackageTypeEntry>? PackageTypes { get; init; }
}

public sealed class DependencyGroupEntry {
  [JsonPropertyName("@id")]
  public required string Url { get; init; }

  [JsonPropertyName("@type")]
  public string Type { get; init; } = "PackageDependencyGroup";

  public List<DependencyEntry>? Dependencies { get; init; }
  public string? TargetFramework { get; init; }
}

public sealed class DependencyEntry {
  [JsonPropertyName("@id")]
  public required string Url { get; init; }

  [JsonPropertyName("@type")]
  public string Type { get; init; } = "PackageDependency";

  public required string Id { get; init; }
  public required string Range { get; init; }
  public required string Registration { get; init; }
}

public sealed class PackageTypeEntry {
  public required string Name { get; init; }
  public string? Version { get; init; }
}

public sealed class VulnerabilityEntry {
  [JsonPropertyName("@id")]
  public string? Url { get; init; }

  [JsonPropertyName("@type")]
  public string? Type { get; init; }

  public required string AdvisoryUrl { get; init; }

  /// <summary>0 = low, 1 = moderate, 2 = high, 3 = critical (a string, like nuget.org).</summary>
  public required string Severity { get; init; }
}

public sealed class SearchResponse {
  [JsonPropertyName("@context")]
  public required SearchContext Context { get; init; }

  public required int TotalHits { get; init; }
  public required List<SearchResultPackage> Data { get; init; }
}

public sealed class SearchContext {
  [JsonPropertyName("@vocab")]
  public string Vocab { get; init; } = "http://schema.nuget.org/schema#";

  [JsonPropertyName("@base")]
  public string? Base { get; init; }
}

public sealed class SearchResultPackage {
  [JsonPropertyName("@id")]
  public required string Url { get; init; }

  [JsonPropertyName("@type")]
  public string Type { get; init; } = "Package";

  public required string Registration { get; init; }
  public required string Id { get; init; }
  public required string Version { get; init; }
  public required string Description { get; init; }
  public string Summary { get; init; } = "";
  public required string Title { get; init; }
  public string? IconUrl { get; init; }
  public string? LicenseUrl { get; init; }
  public string? ProjectUrl { get; init; }
  public required string[] Tags { get; init; }
  public required string[] Authors { get; init; }
  public string[] Owners { get; init; } = [];
  public required long TotalDownloads { get; init; }
  public bool Verified { get; init; }
  public required List<PackageTypeEntry> PackageTypes { get; init; }
  public required List<SearchResultVersion> Versions { get; init; }
  public List<VulnerabilityEntry> Vulnerabilities { get; init; } = [];
}

public sealed class SearchResultVersion {
  public required string Version { get; init; }
  public required long Downloads { get; init; }

  [JsonPropertyName("@id")]
  public required string Url { get; init; }
}

public sealed class AutocompleteResponse {
  [JsonPropertyName("@context")]
  public SearchContext Context { get; init; } = new();

  public required int TotalHits { get; init; }
  public required List<string> Data { get; init; }
}

public sealed class FlatContainerIndex {
  public required List<string> Versions { get; init; }
}

public sealed class VulnerabilityIndexEntry {
  [JsonPropertyName("@name")]
  public required string Name { get; init; }

  [JsonPropertyName("@id")]
  public required string Url { get; init; }

  [JsonPropertyName("@updated")]
  public required DateTimeOffset Updated { get; init; }

  public string? Comment { get; init; }
}

/// <summary>Answer to uploads, unlists and deletes.</summary>
public sealed class PackageOperationResponse {
  public required string Id { get; init; }
  public required string Version { get; init; }
  public required string Status { get; init; }
  public string? Message { get; init; }
  public bool? Listed { get; init; }
  public string? PackageContent { get; init; }
  public string? Registration { get; init; }
}

public sealed class ErrorResponse {
  public required string Error { get; init; }
}

/// <summary>The JSON-LD contexts nuget.org adds to registration documents.</summary>
public static class RegistrationContext {
  public static readonly JsonElement S_INDEX = Parse(
    """
    {
      "@vocab": "http://schema.nuget.org/schema#",
      "catalog": "http://schema.nuget.org/catalog#",
      "xsd": "http://www.w3.org/2001/XMLSchema#",
      "items": { "@id": "catalog:item", "@container": "@set" },
      "commitTimeStamp": { "@id": "catalog:commitTimeStamp", "@type": "xsd:dateTime" },
      "commitId": { "@id": "catalog:commitId" },
      "count": { "@id": "catalog:count" },
      "parent": { "@id": "catalog:parent", "@type": "@id" },
      "tags": { "@id": "tag", "@container": "@set" },
      "reasons": { "@container": "@set" },
      "packageTargetFrameworks": { "@id": "packageTargetFramework", "@container": "@set" },
      "dependencyGroups": { "@id": "dependencyGroup", "@container": "@set" },
      "dependencies": { "@id": "dependency", "@container": "@set" },
      "packageContent": { "@type": "@id" },
      "published": { "@type": "xsd:dateTime" },
      "registration": { "@type": "@id" }
    }
    """
  );

  public static readonly JsonElement S_LEAF = Parse(
    """
    {
      "@vocab": "http://schema.nuget.org/schema#",
      "xsd": "http://www.w3.org/2001/XMLSchema#",
      "catalogEntry": { "@type": "@id" },
      "registration": { "@type": "@id" },
      "packageContent": { "@type": "@id" },
      "published": { "@type": "xsd:dateTime" }
    }
    """
  );

  private static JsonElement Parse(string json) {
    using JsonDocument document = JsonDocument.Parse(json);
    return document.RootElement.Clone();
  }
}