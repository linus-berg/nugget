using Nugget.Api.Configuration;

namespace Nugget.Api.Protocol;

/// <summary>
///   Absolute URLs of every resource, using the same paths as api.nuget.org
///   so that clients (and tools with hard coded nuget.org URLs) work when
///   api.nuget.org resolves to this server.
/// </summary>
public sealed class NuGetUrls {
  /// <summary>Registration hive without SemVer 2.0.0 packages (uncompressed on nuget.org).</summary>
  public const string C_SEMVER1_HIVE = "registration5-semver1";

  /// <summary>Registration hive without SemVer 2.0.0 packages.</summary>
  public const string C_GZ_SEMVER1_HIVE = "registration5-gz-semver1";

  /// <summary>Registration hive including SemVer 2.0.0 packages.</summary>
  public const string C_GZ_SEMVER2_HIVE = "registration5-gz-semver2";

  public NuGetUrls(string base_url) {
    Base = base_url.TrimEnd('/');
  }

  /// <summary>Scheme, host and path base the client used (or the configured URL).</summary>
  public string Base { get; }

  public string ServiceIndex => $"{Base}/v3/index.json";
  public string Search => $"{Base}/query";
  public string Autocomplete => $"{Base}/autocomplete";
  public string FlatContainer => $"{Base}/v3-flatcontainer/";
  public string Publish => $"{Base}/api/v2/package";
  public string Vulnerabilities => $"{Base}/v3/vulnerabilities/index.json";

  public string ReadmeTemplate =>
    $"{FlatContainer}{{lower_id}}/{{lower_version}}/readme";

  public static NuGetUrls FromRequest(HttpRequest request,
                                      NuggetOptions options) {
    if (!string.IsNullOrWhiteSpace(options.PublicBaseUrl)) {
      return new NuGetUrls(options.PublicBaseUrl);
    }

    return new NuGetUrls(
      $"{request.Scheme}://{request.Host.Value}{request.PathBase.Value}"
    );
  }

  /// <summary>Whether a registration hive includes SemVer 2.0.0 packages.</summary>
  public static bool IncludesSemVer2(string hive) {
    return hive.Contains("semver2", StringComparison.OrdinalIgnoreCase);
  }

  public string RegistrationBase(string hive) {
    return $"{Base}/v3/{hive}/";
  }

  public string RegistrationIndex(string hive, string lower_id) {
    return $"{RegistrationBase(hive)}{lower_id}/index.json";
  }

  public string RegistrationPage(string hive, string lower_id, string lower,
                                 string upper) {
    return $"{RegistrationBase(hive)}{lower_id}/page/{lower}/{upper}.json";
  }

  public string RegistrationLeaf(string hive, string lower_id,
                                 string lower_version) {
    return $"{RegistrationBase(hive)}{lower_id}/{lower_version}.json";
  }

  /// <summary>A catalog style leaf with all metadata (there is no catalog index).</summary>
  public string CatalogLeaf(string lower_id, string lower_version) {
    return $"{Base}/v3/catalog0/data/{lower_id}/{lower_version}.json";
  }

  public string PackageContent(string lower_id, string lower_version) {
    return $"{FlatContainer}{lower_id}/{lower_version}/" +
           $"{lower_id}.{lower_version}.nupkg";
  }

  public string Icon(string lower_id, string lower_version) {
    return $"{FlatContainer}{lower_id}/{lower_version}/icon";
  }

  public string Readme(string lower_id, string lower_version) {
    return $"{FlatContainer}{lower_id}/{lower_version}/readme";
  }

  public string License(string lower_id, string lower_version) {
    return $"{FlatContainer}{lower_id}/{lower_version}/license";
  }

  public string VulnerabilityFile(string name, string stamp) {
    return $"{Base}/v3/vulnerabilities/{name}/{stamp}.json";
  }
}