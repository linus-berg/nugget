using Nugget.Api.Catalog;
using Nugget.Api.Json;
using Nugget.Api.Packages;
using Nugget.Api.Protocol;
using Nugget.Api.Vulnerabilities;
using NuGet.Versioning;

namespace Nugget.Api.Endpoints;

/// <summary>
///   The package metadata resource (registration hives such as
///   <c>/v3/registration5-gz-semver2/</c>) used by Visual Studio, the
///   <c>dotnet</c> CLI and many tools, plus catalog style version leaves.
/// </summary>
public static class RegistrationEndpoints {
  public static void MapRegistrationEndpoints(this IEndpointRouteBuilder app) {
    app.MapGet("/v3/{hive}/{id}/index.json", GetIndexAsync);
    app.MapGet("/v3/{hive}/{id}/page/{lower}/{upper}", GetPageAsync);
    app.MapGet("/v3/{hive}/{id}/{leaf}", GetLeafAsync);
    app.MapGet("/v3/catalog0/data/{id}/{leaf}", GetCatalogLeafAsync);
  }

  /// <summary>
  ///   Any <c>registration*</c> hive is served (e.g. older
  ///   <c>registration3</c> URLs hard coded in tools).
  /// </summary>
  private static bool IsHive(string hive) {
    return hive.StartsWith(
             "registration",
             StringComparison.OrdinalIgnoreCase
           ) &&
           hive.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
  }

  private static async Task<IResult> GetIndexAsync(HttpContext context,
                                                   string hive, string id,
                                                   PackageCatalog catalog,
                                                   PackageRepository repository,
                                                   VulnerabilityStore
                                                     vulnerabilities,
                                                   CancellationToken ct) {
    PackageRecord? record = IsHive(hive)
                              ? await FindAsync(id, catalog, repository, ct)
                              : null;
    if (record == null) {
      return Results.NotFound();
    }

    RegistrationBuilder builder = new(context.GetUrls(), hive, vulnerabilities);
    List<PackageVersionRecord> versions = builder.GetVersions(record);
    return versions.Count == 0
             ? Results.NotFound()
             : HttpHelpers.Json(
               builder.BuildIndex(record, versions),
               NuggetJson.S_CONTEXT.RegistrationIndex
             );
  }

  private static async Task<IResult> GetPageAsync(HttpContext context,
                                                  string hive, string id,
                                                  string lower, string upper,
                                                  PackageCatalog catalog,
                                                  PackageRepository repository,
                                                  VulnerabilityStore
                                                    vulnerabilities,
                                                  CancellationToken ct) {
    if (!IsHive(hive) ||
        !HttpHelpers.TryParseVersion(lower, out NuGetVersion from) ||
        !HttpHelpers.TryParseJsonVersion(upper, out NuGetVersion to)) {
      return Results.NotFound();
    }

    PackageRecord? record = await FindAsync(id, catalog, repository, ct);
    if (record == null) {
      return Results.NotFound();
    }

    RegistrationBuilder builder = new(context.GetUrls(), hive, vulnerabilities);
    List<PackageVersionRecord> versions = RegistrationBuilder.SelectPage(
      builder.GetVersions(record),
      from,
      to
    );
    return versions.Count == 0
             ? Results.NotFound()
             : HttpHelpers.Json(
               builder.BuildPage(record, versions, true, true),
               NuggetJson.S_CONTEXT.RegistrationPage
             );
  }

  private static async Task<IResult> GetLeafAsync(HttpContext context,
                                                  string hive, string id,
                                                  string leaf,
                                                  PackageCatalog catalog,
                                                  PackageRepository repository,
                                                  VulnerabilityStore
                                                    vulnerabilities,
                                                  CancellationToken ct) {
    if (!IsHive(hive) ||
        !HttpHelpers.TryParseJsonVersion(leaf, out NuGetVersion version)) {
      return Results.NotFound();
    }

    PackageRecord? record = await FindAsync(id, catalog, repository, ct);
    RegistrationBuilder builder = new(context.GetUrls(), hive, vulnerabilities);
    PackageVersionRecord? found = record?.Find(version);
    if (record == null ||
        found == null ||
        !builder.IsVisible(found)) {
      return Results.NotFound();
    }

    return HttpHelpers.Json(
      builder.BuildLeaf(record, found),
      NuggetJson.S_CONTEXT.RegistrationLeaf
    );
  }

  private static async Task<IResult> GetCatalogLeafAsync(HttpContext context,
    string id, string leaf, PackageCatalog catalog,
    PackageRepository repository, VulnerabilityStore vulnerabilities,
    CancellationToken ct) {
    if (!HttpHelpers.TryParseJsonVersion(leaf, out NuGetVersion version)) {
      return Results.NotFound();
    }

    PackageRecord? record = await FindAsync(id, catalog, repository, ct);
    PackageVersionRecord? found = record?.Find(version);
    if (found == null) {
      return Results.NotFound();
    }

    RegistrationBuilder builder = new(
      context.GetUrls(),
      NuGetUrls.C_GZ_SEMVER2_HIVE,
      vulnerabilities
    );
    return HttpHelpers.Json(
      builder.BuildCatalogEntry(found, true),
      NuggetJson.S_CONTEXT.CatalogEntry
    );
  }

  private static async Task<PackageRecord?> FindAsync(string id,
    PackageCatalog catalog, PackageRepository repository,
    CancellationToken ct) {
    if (!HttpHelpers.TryParseId(id, out string lower_id) ||
        !catalog.MayExist(lower_id)) {
      return null;
    }

    PackageRecord? record = await repository.GetAsync(lower_id, ct);
    return record?.Versions.Count > 0 ? record : null;
  }
}