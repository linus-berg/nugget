using Microsoft.Extensions.Options;
using Nugget.Api.Catalog;
using Nugget.Api.Configuration;
using Nugget.Api.Json;
using Nugget.Api.Protocol;
using Nugget.Api.Vulnerabilities;

namespace Nugget.Api.Endpoints;

public static class ServiceIndexEndpoints {
  public static void MapServiceIndexEndpoints(this IEndpointRouteBuilder app) {
    app.MapGet("/v3/index.json", GetServiceIndex);
    app.MapGet(
      "/",
      (HttpContext context) => Results.Redirect(context.GetUrls().ServiceIndex)
    );
    app.MapGet(
      "/health",
      (PackageCatalog catalog) => HttpHelpers.Json(
        new HealthResponse {
          Status = "ok",
          Packages = catalog.IsReady ? catalog.Count : null
        },
        NuggetJson.S_CONTEXT.HealthResponse
      )
    );
    app.MapGet(
      "/health/ready",
      (PackageCatalog catalog) => HttpHelpers.Json(
        new HealthResponse {
          Status = catalog.IsReady ? "ok" : "loading",
          Packages = catalog.IsReady ? catalog.Count : null
        },
        NuggetJson.S_CONTEXT.HealthResponse,
        catalog.IsReady
          ? StatusCodes.Status200OK
          : StatusCodes.Status503ServiceUnavailable
      )
    );
  }

  private static IResult GetServiceIndex(HttpContext context,
                                         IOptions<NuggetOptions> options,
                                         VulnerabilityStore vulnerabilities) {
    return HttpHelpers.Json(
      ServiceIndexBuilder.Build(
        context.GetUrls(),
        options.Value,
        vulnerabilities.HasData
      ),
      NuggetJson.S_CONTEXT.ServiceIndexDocument
    );
  }
}