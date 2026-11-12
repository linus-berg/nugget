using Microsoft.Net.Http.Headers;
using Nugget.Api.Catalog;
using Nugget.Api.Json;
using Nugget.Api.Packages;
using Nugget.Api.Protocol;
using Nugget.Api.Storage;
using NuGet.Versioning;

namespace Nugget.Api.Endpoints;

/// <summary>
///   The package content resource (<c>/v3-flatcontainer/</c>) that restore
///   uses: version lists, <c>.nupkg</c> and <c>.nuspec</c> downloads, plus
///   icons, readmes and license files.
/// </summary>
public static class FlatContainerEndpoints {
  public static void MapFlatContainerEndpoints(this IEndpointRouteBuilder app) {
    app.MapGet("/v3-flatcontainer/{id}/index.json", GetVersionsAsync);
    app.MapMethods(
      "/v3-flatcontainer/{id}/{version}/{file}",
      [
        HttpMethods.Get,
        HttpMethods.Head
      ],
      GetFileAsync
    );
  }

  private static async Task<IResult> GetVersionsAsync(string id,
    PackageCatalog catalog, PackageRepository repository,
    CancellationToken ct) {
    if (!HttpHelpers.TryParseId(id, out string lower_id) ||
        !catalog.MayExist(lower_id)) {
      return Results.NotFound();
    }

    PackageSummary? summary = catalog.Find(lower_id);
    if (summary != null) {
      // Restore asks for this for every package in the graph: answer from
      // memory with the bytes serialized on first use.
      summary.VersionsJson ??= NuggetJson.Serialize(
        new FlatContainerIndex {
          Versions = summary.Versions.Select(v => v.LowerVersion).ToList()
        },
        NuggetJson.S_CONTEXT.FlatContainerIndex
      );
      return Results.Bytes(summary.VersionsJson, "application/json");
    }

    // The catalog is still loading.
    PackageRecord? record = await repository.GetAsync(lower_id, ct);
    if (record == null || record.Versions.Count == 0) {
      return Results.NotFound();
    }

    return HttpHelpers.Json(
      new FlatContainerIndex {
        Versions = record.Versions.Select(v => v.LowerVersion).ToList()
      },
      NuggetJson.S_CONTEXT.FlatContainerIndex
    );
  }

  private static async Task<IResult> GetFileAsync(HttpContext context,
                                                  string id, string version,
                                                  string file,
                                                  PackageCatalog catalog,
                                                  IObjectStore store,
                                                  DownloadStatistics downloads,
                                                  CancellationToken ct) {
    if (!HttpHelpers.TryParseId(id, out string lower_id) ||
        !HttpHelpers.TryParseVersion(version, out NuGetVersion parsed)) {
      return Results.NotFound();
    }

    string lower_version = parsed.ToNormalizedString().ToLowerInvariant();
    if (!IsKnown(catalog, lower_id, lower_version)) {
      return Results.NotFound();
    }

    string lower_file = file.ToLowerInvariant();
    bool is_package = lower_file == $"{lower_id}.{lower_version}.nupkg";
    (string key, string content_type)? target = lower_file switch {
      _ when is_package => (StorageKeys.Nupkg(lower_id, lower_version),
                            "application/octet-stream"),
      _ when lower_file == $"{lower_id}.nuspec" => (
                                                     StorageKeys.Nuspec(
                                                       lower_id,
                                                       lower_version
                                                     ), "text/xml"),
      "icon" => (
                  StorageKeys.Asset(lower_id, lower_version, PackageAsset.ICON),
                  ""),
      "readme" => (
                    StorageKeys.Asset(
                      lower_id,
                      lower_version,
                      PackageAsset.README
                    ),
                    "text/markdown; charset=utf-8"),
      "license" => (
                     StorageKeys.Asset(
                       lower_id,
                       lower_version,
                       PackageAsset.LICENSE
                     ),
                     "text/plain; charset=utf-8"),
      _ => null
    };
    if (target == null) {
      return Results.NotFound();
    }

    HttpResponse response = context.Response;
    response.Headers.XContentTypeOptions = "nosniff";
    if (!is_package) {
      // Icons and readmes come from package authors; never let a browser
      // treat them as active content on this origin.
      response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
    }

    if (HttpMethods.IsHead(context.Request.Method)) {
      ObjectInfo? info = await store.HeadAsync(target.Value.key, ct);
      if (info == null) {
        return Results.NotFound();
      }

      SetHeaders(response, info, target.Value.content_type);
      return Results.Empty;
    }

    await using ObjectReadStream? stream = await store.OpenReadAsync(
                                             target.Value.key,
                                             ct
                                           );
    if (stream == null) {
      return Results.NotFound();
    }

    if (stream.Info.ETag != null &&
        context.Request.Headers.IfNoneMatch.ToString() == stream.Info.ETag) {
      return Results.StatusCode(StatusCodes.Status304NotModified);
    }

    SetHeaders(response, stream.Info, target.Value.content_type);
    if (is_package) {
      downloads.Record(lower_id, lower_version);
    }

    await stream.Stream.CopyToAsync(response.Body, ct);
    return Results.Empty;
  }

  /// <summary>Unknown versions are answered without a round trip to S3.</summary>
  private static bool IsKnown(PackageCatalog catalog, string lower_id,
                              string lower_version) {
    if (!catalog.IsReady) {
      return true;
    }

    PackageSummary? summary = catalog.Find(lower_id);
    return summary != null &&
           summary.Versions.Any(v => v.LowerVersion == lower_version);
  }

  private static void SetHeaders(HttpResponse response, ObjectInfo info,
                                 string content_type) {
    response.ContentType = content_type.Length > 0
                             ? content_type
                             : info.ContentType ?? "application/octet-stream";
    response.ContentLength = info.Size;
    if (info.ETag != null) {
      response.Headers.ETag = info.ETag;
    }

    response.Headers.LastModified = info.LastModified.ToString("R");
    response.Headers.CacheControl =
      CacheControlHeaderValue.PublicString + ", max-age=86400";
  }
}