namespace Nugget.Api.Configuration;

/// <summary>General server settings (configuration section <c>Nugget</c>).</summary>
public sealed class NuggetOptions {
  public const string C_SECTION = "Nugget";

  /// <summary>
  ///   Single key accepted (via <c>X-NuGet-ApiKey</c>, <c>X-Api-Key</c> or
  ///   <c>Authorization: Bearer</c>) for uploads and other write operations.
  /// </summary>
  public string? ApiKey { get; set; }

  /// <summary>Additional accepted API keys, e.g. one per uploader.</summary>
  public List<string> ApiKeys { get; set; } = new();

  /// <summary>
  ///   Externally visible base URL (e.g. <c>https://api.nuget.org</c>). When
  ///   empty, URLs are built from the scheme and host of each request, which
  ///   is what you want when several names point at the same server.
  /// </summary>
  public string? PublicBaseUrl { get; set; }

  /// <summary>Maximum size of a single uploaded package.</summary>
  public long MaxPackageBytes { get; set; } = 2L * 1024 * 1024 * 1024;

  /// <summary>
  ///   Optional link shown by Visual Studio as "package details", e.g.
  ///   <c>https://nuget.corp/packages/{id}/{version}</c>. Omitted from the
  ///   service index when empty.
  /// </summary>
  public string? PackageDetailsUrlTemplate { get; set; }

  /// <summary>Logs every request (method, path, status, client).</summary>
  public bool LogRequests { get; set; } = true;

  /// <summary>
  ///   Maximum number of package versions whose metadata is kept in memory
  ///   (registration documents are built from it).
  /// </summary>
  public int MetadataCacheVersions { get; set; } = 250_000;

  public SyncOptions Sync { get; set; } = new();

  public IReadOnlyList<string> GetApiKeys() {
    List<string> keys = ApiKeys.Where(k => !string.IsNullOrWhiteSpace(k))
                               .Select(k => k.Trim())
                               .ToList();
    if (!string.IsNullOrWhiteSpace(ApiKey)) {
      keys.Add(ApiKey.Trim());
    }

    return keys;
  }
}

/// <summary>
///   How instances share the in-memory catalog through S3 (section
///   <c>Nugget:Sync</c>).
/// </summary>
public sealed class SyncOptions {
  /// <summary>
  ///   How often (seconds) the change log is polled for packages uploaded
  ///   through other instances. <c>0</c> disables polling (tests only).
  /// </summary>
  public double PollIntervalSeconds { get; set; } = 5;

  /// <summary>How often (minutes) a catalog snapshot is written.</summary>
  public double SnapshotIntervalMinutes { get; set; } = 15;

  /// <summary>How often (seconds) download counts are persisted.</summary>
  public double DownloadFlushSeconds { get; set; } = 60;

  /// <summary>
  ///   How long change log entries are kept after they are included in a
  ///   snapshot. Instances that fall further behind reload the snapshot.
  /// </summary>
  public double ChangeRetentionHours { get; set; } = 24;

  /// <summary>Parallel S3 reads when the catalog is rebuilt from scratch.</summary>
  public int RebuildParallelism { get; set; } = 32;
}