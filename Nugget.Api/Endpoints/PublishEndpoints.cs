using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Nugget.Api.Catalog;
using Nugget.Api.Configuration;
using Nugget.Api.Json;
using Nugget.Api.Packages;
using Nugget.Api.Protocol;
using Nugget.Api.Vulnerabilities;
using NuGet.Versioning;

namespace Nugget.Api.Endpoints;

/// <summary>
///   Write operations, all requiring an API key. Uploads accept the raw
///   <c>.nupkg</c> as the request body (<c>curl --data-binary</c>) or a
///   <c>multipart/form-data</c> upload as sent by <c>dotnet nuget push</c>.
/// </summary>
public static class PublishEndpoints {
  private const int C_MAX_VULNERABILITY_BYTES_ = 512 * 1024 * 1024;

  public static void MapPublishEndpoints(this IEndpointRouteBuilder app) {
    app.MapMethods(
         "/api/v2/package",
         [
           HttpMethods.Put,
           HttpMethods.Post
         ],
         UploadAsync
       )
       .RequireApiKey()
       .DisableAntiforgery();
    app.MapDelete("/api/v2/package/{id}/{version}", DeleteAsync)
       .RequireApiKey();
    app.MapPost("/api/v2/package/{id}/{version}", RelistAsync).RequireApiKey();
    app.MapPut("/api/v2/vulnerabilities/{name}", UploadVulnerabilitiesAsync)
       .RequireApiKey();
    app.MapDelete("/api/v2/vulnerabilities/{name}", DeleteVulnerabilitiesAsync)
       .RequireApiKey();
    app.MapPost("/api/v2/admin/rebuild", RebuildAsync).RequireApiKey();

    // nuget.org's V2 download URLs, for scripts that use them.
    app.MapGet("/api/v2/package/{id}/{version?}", RedirectDownload);
  }

  private static async Task<IResult> UploadAsync(HttpContext context,
                                                 PackagePublisher publisher,
                                                 IOptions<NuggetOptions>
                                                   options,
                                                 ILoggerFactory logger_factory,
                                                 CancellationToken ct) {
    long max_bytes = options.Value.MaxPackageBytes;
    IHttpMaxRequestBodySizeFeature? limit =
      context.Features.Get<IHttpMaxRequestBodySizeFeature>();
    if (limit is { IsReadOnly: false }) {
      limit.MaxRequestBodySize = max_bytes;
    }

    string path = Path.Combine(
      Path.GetTempPath(),
      $"nugget-upload-{Guid.NewGuid():N}.nupkg"
    );
    try {
      Received? received = await ReceiveAsync(
                             context.Request,
                             path,
                             max_bytes,
                             ct
                           );
      if (received == null) {
        return HttpHelpers.Error(
          StatusCodes.Status400BadRequest,
          "The request contains no package."
        );
      }

      InspectedPackage package;
      await using (FileStream file = File.OpenRead(path)) {
        package = PackageInspector.Inspect(
          file,
          received.Size,
          received.Sha512
        );
      }

      PublishResult result = await publisher.PublishAsync(package, path, ct);
      return ToResponse(context, result);
    } catch (InvalidPackageException ex) {
      logger_factory.CreateLogger(typeof(PublishEndpoints))
                    .LogWarning("Rejected upload: {Reason}", ex.Message);
      return HttpHelpers.Error(StatusCodes.Status400BadRequest, ex.Message);
    } catch (PackageTooLargeException) {
      return TooLarge(max_bytes);
    } catch (BadHttpRequestException ex) when (
      ex.StatusCode == StatusCodes.Status413PayloadTooLarge) {
      return TooLarge(max_bytes);
    } finally {
      File.Delete(path);
    }
  }

  private static IResult ToResponse(HttpContext context, PublishResult result) {
    PackageVersionRecord version = result.Version;
    NuGetUrls urls = context.GetUrls();
    PackageOperationResponse body = new() {
      Id = version.Id,
      Version = version.Version,
      Listed = version.Listed,
      Status = result.Status switch {
        PublishStatus.CREATED => "created",
        PublishStatus.EXISTS => "exists",
        _ => "conflict"
      },
      Message = result.Status switch {
        PublishStatus.CREATED => null,
        PublishStatus.EXISTS => "This package is already present.",
        _ => "A different package with this ID and version already exists."
      },
      PackageContent = urls.PackageContent(
        version.LowerId,
        version.LowerVersion
      ),
      Registration = urls.RegistrationIndex(
        NuGetUrls.C_GZ_SEMVER2_HIVE,
        version.LowerId
      )
    };
    return HttpHelpers.Json(
      body,
      NuggetJson.S_CONTEXT.PackageOperationResponse,
      result.Status switch {
        PublishStatus.CREATED => StatusCodes.Status201Created,
        PublishStatus.EXISTS => StatusCodes.Status200OK,
        _ => StatusCodes.Status409Conflict
      }
    );
  }

  /// <summary>Unlists a version, or with <c>?purge=true</c> deletes it.</summary>
  private static async Task<IResult> DeleteAsync(string id, string version,
                                                 bool? purge,
                                                 PackagePublisher publisher,
                                                 CancellationToken ct) {
    if (!HttpHelpers.TryParseId(id, out string lower_id) ||
        !HttpHelpers.TryParseVersion(version, out NuGetVersion parsed)) {
      return Results.NotFound();
    }

    PackageVersionRecord? result = purge == true
                                     ? await publisher.DeleteAsync(
                                         lower_id,
                                         parsed,
                                         ct
                                       )
                                     : await publisher.SetListedAsync(
                                         lower_id,
                                         parsed,
                                         false,
                                         ct
                                       );
    return result == null
             ? HttpHelpers.Error(
               StatusCodes.Status404NotFound,
               "No such package version."
             )
             : Operation(
               result,
               purge == true ? "deleted" : "unlisted",
               purge == true ? null : false
             );
  }

  private static async Task<IResult> RelistAsync(string id, string version,
                                                 PackagePublisher publisher,
                                                 CancellationToken ct) {
    if (!HttpHelpers.TryParseId(id, out string lower_id) ||
        !HttpHelpers.TryParseVersion(version, out NuGetVersion parsed)) {
      return Results.NotFound();
    }

    PackageVersionRecord? result = await publisher.SetListedAsync(
                                     lower_id,
                                     parsed,
                                     true,
                                     ct
                                   );
    return result == null
             ? HttpHelpers.Error(
               StatusCodes.Status404NotFound,
               "No such package version."
             )
             : Operation(result, "listed", true);
  }

  private static async Task<IResult> UploadVulnerabilitiesAsync(
    HttpContext context, string name, VulnerabilityStore vulnerabilities,
    ChangeFeed feed, CancellationToken ct) {
    if (!VulnerabilityStore.IsValidName(name)) {
      return HttpHelpers.Error(
        StatusCodes.Status400BadRequest,
        "Names are lower case letters, digits and dashes (e.g. 'base', 'update')."
      );
    }

    IHttpMaxRequestBodySizeFeature? limit =
      context.Features.Get<IHttpMaxRequestBodySizeFeature>();
    if (limit is { IsReadOnly: false }) {
      limit.MaxRequestBodySize = C_MAX_VULNERABILITY_BYTES_;
    }

    using MemoryStream buffer = new();
    await context.Request.Body.CopyToAsync(buffer, ct);
    try {
      await vulnerabilities.SaveAsync(name, buffer.ToArray(), ct);
    } catch (JsonException ex) {
      return HttpHelpers.Error(
        StatusCodes.Status400BadRequest,
        $"Not a NuGet vulnerability file: {ex.Message}"
      );
    }

    await feed.AppendAsync(ChangeKind.VULNERABILITIES, name, null, ct);
    return Results.Ok();
  }

  private static async Task<IResult> DeleteVulnerabilitiesAsync(string name,
    VulnerabilityStore vulnerabilities, ChangeFeed feed,
    CancellationToken ct) {
    if (!VulnerabilityStore.IsValidName(name) ||
        !await vulnerabilities.DeleteAsync(name, ct)) {
      return Results.NotFound();
    }

    await feed.AppendAsync(ChangeKind.VULNERABILITIES, name, null, ct);
    return Results.Ok();
  }

  /// <summary>Rebuilds the catalog from the metadata documents and writes a snapshot.</summary>
  private static async Task<IResult> RebuildAsync(
    CatalogSynchronizer synchronizer, PackageCatalog catalog,
    CancellationToken ct) {
    await synchronizer.RebuildAsync(ct);
    return HttpHelpers.Json(
      new HealthResponse {
        Status = "rebuilt",
        Packages = catalog.Count
      },
      NuggetJson.S_CONTEXT.HealthResponse
    );
  }

  private static IResult RedirectDownload(HttpContext context, string id,
                                          string? version,
                                          PackageCatalog catalog) {
    if (!HttpHelpers.TryParseId(id, out string lower_id)) {
      return Results.NotFound();
    }

    string lower_version;
    if (version == null) {
      SummaryVersion? latest = catalog.Find(lower_id)?.GetLatest(false, true);
      if (latest == null) {
        return Results.NotFound();
      }

      lower_version = latest.LowerVersion;
    } else if (HttpHelpers.TryParseVersion(version, out NuGetVersion parsed)) {
      lower_version = parsed.ToNormalizedString().ToLowerInvariant();
    } else {
      return Results.NotFound();
    }

    return Results.Redirect(
      context.GetUrls().PackageContent(lower_id, lower_version)
    );
  }

  private static IResult Operation(PackageVersionRecord version, string status,
                                   bool? listed) {
    return HttpHelpers.Json(
      new PackageOperationResponse {
        Id = version.Id,
        Version = version.Version,
        Status = status,
        Listed = listed
      },
      NuggetJson.S_CONTEXT.PackageOperationResponse
    );
  }

  private static IResult TooLarge(long max_bytes) {
    return HttpHelpers.Error(
      StatusCodes.Status413PayloadTooLarge,
      $"Packages may be at most {max_bytes:N0} bytes (Nugget:MaxPackageBytes)."
    );
  }

  /// <summary>Streams the package to a file, hashing it on the way.</summary>
  private static async Task<Received?> ReceiveAsync(HttpRequest request,
                                                    string path,
                                                    long max_bytes,
                                                    CancellationToken ct) {
    if (request.HasFormContentType &&
        MediaTypeHeaderValue.TryParse(
          request.ContentType,
          out MediaTypeHeaderValue? media_type
        ) &&
        media_type.MediaType.Equals(
          "multipart/form-data",
          StringComparison.OrdinalIgnoreCase
        )) {
      string boundary =
        HeaderUtilities.RemoveQuotes(media_type.Boundary).Value ?? "";
      MultipartReader reader = new(boundary, request.Body);
      while (await reader.ReadNextSectionAsync(ct) is { } section) {
        // The first file part is the package (nuget push sends exactly one).
        if (ContentDispositionHeaderValue.TryParse(
              section.ContentDisposition,
              out ContentDispositionHeaderValue? disposition
            ) &&
            disposition.IsFileDisposition()) {
          return await SaveAsync(section.Body, path, max_bytes, ct);
        }
      }

      return null;
    }

    Received received = await SaveAsync(request.Body, path, max_bytes, ct);
    return received.Size == 0 ? null : received;
  }

  private static async Task<Received> SaveAsync(Stream body, string path,
                                                long max_bytes,
                                                CancellationToken ct) {
    using IncrementalHash hash =
      IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
    byte[] buffer = new byte[128 * 1024];
    long size = 0;
    await using FileStream file = new(
      path,
      FileMode.CreateNew,
      FileAccess.Write,
      FileShare.None,
      buffer.Length,
      FileOptions.Asynchronous
    );
    int read;
    while ((read = await body.ReadAsync(buffer, ct)) > 0) {
      size += read;
      if (size > max_bytes) {
        throw new PackageTooLargeException();
      }

      hash.AppendData(buffer, 0, read);
      await file.WriteAsync(buffer.AsMemory(0, read), ct);
    }

    return new Received {
      Size = size,
      Sha512 = Convert.ToBase64String(hash.GetHashAndReset())
    };
  }

  private sealed class Received {
    public required long Size { get; init; }
    public required string Sha512 { get; init; }
  }

  private sealed class PackageTooLargeException : Exception;
}