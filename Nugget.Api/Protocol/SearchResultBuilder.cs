using Nugget.Api.Catalog;
using Nugget.Api.Vulnerabilities;
using NuGet.Versioning;

namespace Nugget.Api.Protocol;

/// <summary>Turns catalog search hits into nuget.org shaped search results.</summary>
public sealed class SearchResultBuilder(
  NuGetUrls urls,
  DownloadStatistics downloads,
  VulnerabilityStore vulnerabilities
) {
  public SearchResponse Build(SearchResults results, SearchRequest request) {
    string hive = Hive(request.SemVer2);
    return new SearchResponse {
      Context = new SearchContext {
        Base = urls.RegistrationBase(hive)
      },
      TotalHits = results.TotalHits,
      Data = results.Hits.Select(h => Build(h, request, hive)).ToList()
    };
  }

  /// <summary>
  ///   SemVer 2.0.0 aware clients get the hive with SemVer 2.0.0 packages,
  ///   like on nuget.org.
  /// </summary>
  public static string Hive(bool semver2) {
    return semver2 ? NuGetUrls.C_GZ_SEMVER2_HIVE : NuGetUrls.C_SEMVER1_HIVE;
  }

  private SearchResultPackage Build(SearchHit hit, SearchRequest request,
                                    string hive) {
    PackageSummary package = hit.Package;
    SearchDetails details = hit.Details;
    string lower_id = package.LowerId;
    string latest = hit.Latest.LowerVersion;
    string registration = urls.RegistrationIndex(hive, lower_id);
    return new SearchResultPackage {
      Url = registration,
      Registration = registration,
      Id = package.Id,
      Version = hit.Latest.Version,
      Description = details.Description,
      Summary = details.Summary ?? "",
      // nuget.org shows the ID when a package has no title.
      Title = string.IsNullOrWhiteSpace(details.Title)
                ? package.Id
                : details.Title,
      IconUrl = details.HasIcon ? urls.Icon(lower_id, latest) : details.IconUrl,
      LicenseUrl = RegistrationBuilder.LicenseUrl(
        urls,
        lower_id,
        latest,
        details.LicenseExpression,
        details.HasLicenseFile,
        details.LicenseUrl
      ),
      ProjectUrl = details.ProjectUrl,
      Tags = details.Tags,
      Authors = details.Authors,
      TotalDownloads = downloads.GetTotal(lower_id),
      PackageTypes = details.PackageTypes.Length == 0
                       ? [
                         new PackageTypeEntry {
                           Name = "Dependency"
                         }
                       ]
                       : details.PackageTypes
                                .Select(
                                  t => new PackageTypeEntry {
                                    Name = t
                                  }
                                )
                                .ToList(),
      Versions = package.GetListed(request.Prerelease, request.SemVer2)
                        .Select(
                          v => new SearchResultVersion {
                            Version = v.Version,
                            Downloads = downloads.GetVersion(
                              lower_id,
                              v.LowerVersion
                            ),
                            Url = urls.RegistrationLeaf(
                              hive,
                              lower_id,
                              v.LowerVersion
                            )
                          }
                        )
                        .ToList(),
      Vulnerabilities = vulnerabilities.Find(
                          lower_id,
                          NuGetVersion.Parse(hit.Latest.Version)
                        ) ??
                        []
    };
  }
}