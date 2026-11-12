using Nugget.Api.Catalog;
using Nugget.Api.Json;
using Nugget.Api.Protocol;
using Nugget.Api.Vulnerabilities;

namespace Nugget.Api.Endpoints;

/// <summary>Search (<c>/query</c>) and autocomplete (<c>/autocomplete</c>).</summary>
public static class SearchEndpoints {
  // nuget.org's limits.
  private const int C_MAX_TAKE_ = 1000;
  private const int C_MAX_SKIP_ = 100_000;

  public static void MapSearchEndpoints(this IEndpointRouteBuilder app) {
    app.MapGet("/query", Search);
    app.MapGet("/autocomplete", Autocomplete);
  }

  private static IResult Search(HttpContext context, PackageCatalog catalog,
                                DownloadStatistics downloads,
                                VulnerabilityStore vulnerabilities) {
    if (!catalog.IsReady) {
      return NotReady(context);
    }

    IQueryCollection query = context.Request.Query;
    SearchRequest request = new() {
      Query = query["q"].ToString(),
      Skip = query.GetInt("skip", 0, 0, C_MAX_SKIP_),
      Take = query.GetInt("take", 20, 0, C_MAX_TAKE_),
      Prerelease = query.GetBool("prerelease"),
      SemVer2 = query.WantsSemVer2(),
      PackageType = query["packageType"].ToString()
    };
    SearchResults results = catalog.Search(request);
    return HttpHelpers.Json(
      new SearchResultBuilder(context.GetUrls(), downloads, vulnerabilities)
        .Build(results, request),
      NuggetJson.S_CONTEXT.SearchResponse
    );
  }

  private static IResult Autocomplete(HttpContext context,
                                      PackageCatalog catalog) {
    if (!catalog.IsReady) {
      return NotReady(context);
    }

    IQueryCollection query = context.Request.Query;
    bool prerelease = query.GetBool("prerelease");
    bool semver2 = query.WantsSemVer2();
    string id = query["id"].ToString();
    if (id.Length > 0) {
      // Versions of one package (used for version completion).
      List<string> versions = HttpHelpers.TryParseId(id, out string lower_id)
                                ? catalog.AutocompleteVersions(
                                  lower_id,
                                  prerelease,
                                  semver2
                                )
                                : [];
      return HttpHelpers.Json(
        new AutocompleteResponse {
          TotalHits = versions.Count,
          Data = versions
        },
        NuggetJson.S_CONTEXT.AutocompleteResponse
      );
    }

    (int total, List<string> ids) = catalog.AutocompleteIds(
      query["q"].ToString(),
      query.GetInt("skip", 0, 0, C_MAX_SKIP_),
      query.GetInt("take", 20, 0, C_MAX_TAKE_),
      prerelease,
      semver2,
      query["packageType"].ToString()
    );
    return HttpHelpers.Json(
      new AutocompleteResponse {
        TotalHits = total,
        Data = ids
      },
      NuggetJson.S_CONTEXT.AutocompleteResponse
    );
  }

  private static IResult NotReady(HttpContext context) {
    context.Response.Headers.RetryAfter = "10";
    return HttpHelpers.Error(
      StatusCodes.Status503ServiceUnavailable,
      "The package catalog is still loading."
    );
  }
}