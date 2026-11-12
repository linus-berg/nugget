using Nugget.Api.Configuration;

namespace Nugget.Api.Protocol;

/// <summary>
///   The service index (<c>/v3/index.json</c>): the same resource types and
///   versions as nuget.org's, all pointing at this server.
/// </summary>
public static class ServiceIndexBuilder {
  private static readonly string[] S_SEARCH_VERSIONS_ = [
    "",
    "/3.0.0-beta",
    "/3.0.0-rc",
    "/3.5.0"
  ];

  public static ServiceIndexDocument Build(NuGetUrls urls,
                                           NuggetOptions options,
                                           bool has_vulnerabilities) {
    List<ServiceResource> resources = [];
    foreach (string version in S_SEARCH_VERSIONS_) {
      resources.Add(
        Resource(
          urls.Search,
          "SearchQueryService" + version,
          "Query endpoint of NuGet Search service"
        )
      );
    }

    foreach (string version in S_SEARCH_VERSIONS_) {
      resources.Add(
        Resource(
          urls.Autocomplete,
          "SearchAutocompleteService" + version,
          "Autocomplete endpoint of NuGet Search service"
        )
      );
    }

    string semver1 = urls.RegistrationBase(NuGetUrls.C_SEMVER1_HIVE);
    string gz_semver1 = urls.RegistrationBase(NuGetUrls.C_GZ_SEMVER1_HIVE);
    string gz_semver2 = urls.RegistrationBase(NuGetUrls.C_GZ_SEMVER2_HIVE);
    const string REGISTRATION_COMMENT =
      "Base URL of where NuGet package registration info is stored";
    resources.AddRange(
      [
        Resource(semver1, "RegistrationsBaseUrl", REGISTRATION_COMMENT),
        Resource(
          semver1,
          "RegistrationsBaseUrl/3.0.0-beta",
          REGISTRATION_COMMENT
        ),
        Resource(
          semver1,
          "RegistrationsBaseUrl/3.0.0-rc",
          REGISTRATION_COMMENT
        ),
        Resource(
          gz_semver1,
          "RegistrationsBaseUrl/3.4.0",
          REGISTRATION_COMMENT + ", gzip compressed"
        ),
        Resource(
          gz_semver2,
          "RegistrationsBaseUrl/3.6.0",
          REGISTRATION_COMMENT + ", including SemVer 2.0.0 packages"
        ),
        new ServiceResource {
          Url = gz_semver2,
          Type = "RegistrationsBaseUrl/Versioned",
          Comment = REGISTRATION_COMMENT + ", including SemVer 2.0.0 packages",
          ClientVersion = "4.3.0-alpha"
        },
        Resource(
          urls.FlatContainer,
          "PackageBaseAddress/3.0.0",
          "Base URL of where NuGet packages are stored, in the format " +
          urls.FlatContainer +
          "{id-lower}/{version-lower}/{id-lower}.{version-lower}.nupkg"
        ),
        Resource(
          urls.ReadmeTemplate,
          "ReadmeUriTemplate/6.13.0",
          "URI template used by NuGet Client to construct a URL for " +
          "downloading a package's README"
        ),
        Resource(
          semver1 + "{id-lower}/index.json",
          "PackageDisplayMetadataUriTemplate/3.0.0-rc",
          "URI template used by NuGet Client to construct display metadata " +
          "for Packages using ID"
        ),
        Resource(
          semver1 + "{id-lower}/{version-lower}.json",
          "PackageVersionDisplayMetadataUriTemplate/3.0.0-rc",
          "URI template used by NuGet Client to construct display metadata " +
          "for Packages using ID, Version"
        ),
        Resource(
          urls.Publish,
          "PackagePublish/2.0.0",
          "Package upload (requires an API key)"
        )
      ]
    );
    if (has_vulnerabilities) {
      resources.Add(
        Resource(
          urls.Vulnerabilities,
          "VulnerabilityInfo/6.7.0",
          "The endpoint for discovering information about vulnerabilities " +
          "of packages in this package source."
        )
      );
    }

    if (!string.IsNullOrWhiteSpace(options.PackageDetailsUrlTemplate)) {
      resources.Add(
        Resource(
          options.PackageDetailsUrlTemplate,
          "PackageDetailsUriTemplate/5.1.0",
          "URI template used by NuGet Client to construct details URL for packages"
        )
      );
    }

    return new ServiceIndexDocument {
      Resources = resources
    };
  }

  private static ServiceResource Resource(string url, string type,
                                          string comment) {
    return new ServiceResource {
      Url = url,
      Type = type,
      Comment = comment
    };
  }
}