using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Options;
using Nugget.Api.Configuration;
using Nugget.Api.Json;
using Nugget.Api.Protocol;
using NuGet.Packaging;
using NuGet.Versioning;

namespace Nugget.Api.Endpoints;

public static class HttpHelpers {
  public const string C_API_KEY_HEADER = "X-NuGet-ApiKey";
  private const string C_ALT_API_KEY_HEADER_ = "X-Api-Key";

  public static NuGetUrls GetUrls(this HttpContext context) {
    return NuGetUrls.FromRequest(
      context.Request,
      context.RequestServices.GetRequiredService<IOptions<NuggetOptions>>()
             .Value
    );
  }

  public static IResult Json<T>(T value, JsonTypeInfo<T> type,
                                int status = StatusCodes.Status200OK) {
    return Results.Json(value, type, "application/json", status);
  }

  public static IResult Error(int status, string message) {
    return Json(
      new ErrorResponse {
        Error = message
      },
      NuggetJson.S_CONTEXT.ErrorResponse,
      status
    );
  }

  /// <summary>Validates a package ID from a URL and lower cases it.</summary>
  public static bool TryParseId(string? id, out string lower_id) {
    lower_id = "";
    if (string.IsNullOrEmpty(id) ||
        id.Length > PackageIdValidator.MaxPackageIdLength ||
        !PackageIdValidator.IsValidPackageId(id)) {
      return false;
    }

    lower_id = id.ToLowerInvariant();
    return true;
  }

  public static bool TryParseVersion(string? text,
                                     out NuGetVersion version) {
    version = null!;
    if (string.IsNullOrEmpty(text) || text.Length > 256) {
      return false;
    }

    if (!NuGetVersion.TryParse(text, out NuGetVersion? parsed)) {
      return false;
    }

    version = parsed;
    return true;
  }

  /// <summary>Strips a <c>.json</c> suffix and parses the version in front of it.</summary>
  public static bool TryParseJsonVersion(string? file,
                                         out NuGetVersion version) {
    version = null!;
    return file != null &&
           file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
           TryParseVersion(file[..^".json".Length], out version);
  }

  public static bool GetBool(this IQueryCollection query, string name) {
    return bool.TryParse(query[name].ToString(), out bool value) && value;
  }

  public static int GetInt(this IQueryCollection query, string name,
                           int fallback, int min, int max) {
    return int.TryParse(
             query[name].ToString(),
             NumberStyles.Integer,
             CultureInfo.InvariantCulture,
             out int value
           )
             ? Math.Clamp(value, min, max)
             : fallback;
  }

  /// <summary><c>semVerLevel=2.0.0</c> (or higher) asks for SemVer 2.0.0 packages.</summary>
  public static bool WantsSemVer2(this IQueryCollection query) {
    return NuGetVersion.TryParse(
             query["semVerLevel"].ToString(),
             out NuGetVersion? level
           ) &&
           level.Major >= 2;
  }

  /// <summary>The API key a client sent, from any of the accepted headers.</summary>
  public static string? GetApiKey(HttpRequest request) {
    foreach (string header in new[] {
               C_API_KEY_HEADER,
               C_ALT_API_KEY_HEADER_
             }) {
      string value = request.Headers[header].ToString();
      if (value.Length > 0) {
        return value;
      }
    }

    string authorization = request.Headers.Authorization.ToString();
    return authorization.StartsWith(
             "Bearer ",
             StringComparison.OrdinalIgnoreCase
           )
             ? authorization["Bearer ".Length..].Trim()
             : null;
  }

  public static bool IsValidApiKey(string? key, IReadOnlyList<string> keys) {
    if (string.IsNullOrEmpty(key)) {
      return false;
    }

    // Compare hashes in constant time so timing reveals nothing about keys.
    byte[] given = SHA256.HashData(Encoding.UTF8.GetBytes(key));
    bool valid = false;
    foreach (string expected in keys) {
      byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
      valid |= CryptographicOperations.FixedTimeEquals(given, hash);
    }

    return valid;
  }

  /// <summary>Requires a valid API key for the endpoint (uploads, deletes, ...).</summary>
  public static TBuilder RequireApiKey<TBuilder>(this TBuilder builder)
    where TBuilder : IEndpointConventionBuilder {
    return builder.AddEndpointFilter(
      async (context, next) => {
        IReadOnlyList<string> keys = context.HttpContext.RequestServices
                                            .GetRequiredService<
                                              IOptions<NuggetOptions>>()
                                            .Value
                                            .GetApiKeys();
        if (keys.Count == 0) {
          return Error(
            StatusCodes.Status403Forbidden,
            "Uploads are disabled: no API key is configured on the server."
          );
        }

        string? key = GetApiKey(context.HttpContext.Request);
        if (!IsValidApiKey(key, keys)) {
          return Error(
            StatusCodes.Status401Unauthorized,
            string.IsNullOrEmpty(key)
              ? $"An API key is required ({C_API_KEY_HEADER} header)."
              : "The API key is not valid."
          );
        }

        return await next(context);
      }
    );
  }
}