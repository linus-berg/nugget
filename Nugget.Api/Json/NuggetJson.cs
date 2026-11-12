using System.IO.Compression;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Nugget.Api.Catalog;
using Nugget.Api.Packages;
using Nugget.Api.Protocol;
using Nugget.Api.Vulnerabilities;

namespace Nugget.Api.Json;

/// <summary>Source generated serializers for stored documents and protocol responses.</summary>
[JsonSourceGenerationOptions(
  PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
  DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
)]
[JsonSerializable(typeof(PackageRecord))]
[JsonSerializable(typeof(CatalogSnapshot))]
[JsonSerializable(typeof(Dictionary<string, Dictionary<string, long>>))]
[JsonSerializable(typeof(Dictionary<string, List<VulnerabilityRecord>>))]
[JsonSerializable(typeof(ServiceIndexDocument))]
[JsonSerializable(typeof(RegistrationIndex))]
[JsonSerializable(typeof(RegistrationPage))]
[JsonSerializable(typeof(RegistrationLeaf))]
[JsonSerializable(typeof(CatalogEntry))]
[JsonSerializable(typeof(SearchResponse))]
[JsonSerializable(typeof(AutocompleteResponse))]
[JsonSerializable(typeof(FlatContainerIndex))]
[JsonSerializable(typeof(List<VulnerabilityIndexEntry>))]
[JsonSerializable(typeof(PackageOperationResponse))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(HealthResponse))]
public sealed partial class NuggetJsonContext : JsonSerializerContext;

public sealed class HealthResponse {
  public required string Status { get; init; }
  public int? Packages { get; init; }
}

public static class NuggetJson {
  /// <summary>
  ///   Like nuget.org, non-ASCII text and characters such as <c>+</c> are not
  ///   escaped (responses are never embedded in HTML).
  /// </summary>
  public static readonly NuggetJsonContext S_CONTEXT = new(
    // Options given here replace the attribute's, so repeat them.
    new JsonSerializerOptions {
      PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
      DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
      Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    }
  );

  public static byte[] Serialize<T>(T value, JsonTypeInfo<T> type) {
    return JsonSerializer.SerializeToUtf8Bytes(value, type);
  }

  public static byte[] SerializeGzip<T>(T value, JsonTypeInfo<T> type) {
    using MemoryStream buffer = new();
    using (GZipStream gzip = new(buffer, CompressionLevel.Optimal, true)) {
      JsonSerializer.Serialize(gzip, value, type);
    }

    return buffer.ToArray();
  }

  public static T? DeserializeGzip<T>(byte[] data, JsonTypeInfo<T> type) {
    using GZipStream gzip = new(
      new MemoryStream(data, false),
      CompressionMode.Decompress
    );
    return JsonSerializer.Deserialize(gzip, type);
  }
}