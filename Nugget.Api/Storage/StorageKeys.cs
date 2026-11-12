namespace Nugget.Api.Storage;

/// <summary>
///   Bucket layout. Package files mirror the nuget.org flat container (lower
///   case ID and normalized version); the exact ID and version are kept in
///   the package's metadata document.
/// </summary>
/// <remarks>
///   <code>
///   packages/{id}/{version}/{id}.{version}.nupkg   the package, byte for byte
///   packages/{id}/{version}/{id}.nuspec            extracted manifest
///   packages/{id}/{version}/icon|readme|license    extracted embedded files
///   metadata/{id}.json.gz                          all versions of one package
///   catalog/snapshot.json.gz                       search catalog snapshot
///   changes/{ticks}-{instance}-{kind}-{subject}    change log between instances
///   vulnerabilities/{name}.json.gz                 mirrored advisory data
///   </code>
/// </remarks>
public static class StorageKeys {
  public const string C_METADATA_PREFIX = "metadata/";
  public const string C_CHANGES_PREFIX = "changes/";
  public const string C_VULNERABILITIES_PREFIX = "vulnerabilities/";
  public const string C_SNAPSHOT = "catalog/snapshot.json.gz";

  private const string C_METADATA_SUFFIX_ = ".json.gz";

  public static string PackageFolder(string lower_id, string lower_version) {
    return $"packages/{lower_id}/{lower_version}/";
  }

  public static string Nupkg(string lower_id, string lower_version) {
    return PackageFolder(lower_id, lower_version) +
           $"{lower_id}.{lower_version}.nupkg";
  }

  public static string Nuspec(string lower_id, string lower_version) {
    return PackageFolder(lower_id, lower_version) + $"{lower_id}.nuspec";
  }

  public static string Asset(string lower_id, string lower_version,
                             PackageAsset asset) {
    return PackageFolder(lower_id, lower_version) +
           asset.ToString().ToLowerInvariant();
  }

  public static string Metadata(string lower_id) {
    return C_METADATA_PREFIX + lower_id + C_METADATA_SUFFIX_;
  }

  /// <summary>The lower case package ID of a metadata key, or <c>null</c>.</summary>
  public static string? IdFromMetadataKey(string key) {
    if (!key.StartsWith(C_METADATA_PREFIX, StringComparison.Ordinal) ||
        !key.EndsWith(C_METADATA_SUFFIX_, StringComparison.Ordinal)) {
      return null;
    }

    return key[C_METADATA_PREFIX.Length..^C_METADATA_SUFFIX_.Length];
  }

  public static string Vulnerabilities(string name) {
    return C_VULNERABILITIES_PREFIX + name + ".json.gz";
  }
}

/// <summary>Files extracted from a package so they can be served directly.</summary>
public enum PackageAsset {
  ICON,
  README,
  LICENSE
}