using System.Collections.Concurrent;

namespace Nugget.Api.Catalog;

/// <summary>
///   Download counts of this mirror (package downloads served, like
///   nuget.org's counters). Counts are kept in memory, shared with other
///   instances through the change log and persisted in catalog snapshots.
///   They are statistics: a crash loses at most the last unflushed minute.
/// </summary>
public sealed class DownloadStatistics {
  private readonly ConcurrentDictionary<string, PackageDownloads> counts_ =
    new(StringComparer.Ordinal);

  private ConcurrentDictionary<string, long> pending_ =
    new(StringComparer.Ordinal);

  /// <summary>Counts one download served by this instance.</summary>
  public void Record(string lower_id, string lower_version) {
    Add(lower_id, lower_version, 1);
    pending_.AddOrUpdate($"{lower_id}/{lower_version}", 1, (_, n) => n + 1);
  }

  public long GetTotal(string lower_id) {
    return counts_.TryGetValue(lower_id, out PackageDownloads? downloads)
             ? downloads.total
             : 0;
  }

  public long GetVersion(string lower_id, string lower_version) {
    return counts_.TryGetValue(lower_id, out PackageDownloads? downloads) &&
           downloads.versions.TryGetValue(lower_version, out long count)
             ? count
             : 0;
  }

  /// <summary>
  ///   Takes the downloads recorded since the last call, as
  ///   <c>id -&gt; version -&gt; count</c>.
  /// </summary>
  public Dictionary<string, Dictionary<string, long>> TakePending() {
    ConcurrentDictionary<string, long> pending = Interlocked.Exchange(
      ref pending_,
      new ConcurrentDictionary<string, long>(StringComparer.Ordinal)
    );
    Dictionary<string, Dictionary<string, long>> result =
      new(StringComparer.Ordinal);
    foreach (KeyValuePair<string, long> pair in pending) {
      int slash = pair.Key.IndexOf('/');
      string id = pair.Key[..slash];
      if (!result.TryGetValue(id, out Dictionary<string, long>? versions)) {
        versions = new Dictionary<string, long>(StringComparer.Ordinal);
        result[id] = versions;
      }

      versions[pair.Key[(slash + 1)..]] = pair.Value;
    }

    return result;
  }

  /// <summary>Puts counts back after a failed flush.</summary>
  public void RestorePending(
    Dictionary<string, Dictionary<string, long>> pending) {
    foreach (KeyValuePair<string, Dictionary<string, long>> package in
             pending) {
      foreach (KeyValuePair<string, long> version in package.Value) {
        pending_.AddOrUpdate(
          $"{package.Key}/{version.Key}",
          version.Value,
          (_, n) => n + version.Value
        );
      }
    }
  }

  /// <summary>Adds counts recorded by another instance.</summary>
  public void Apply(Dictionary<string, Dictionary<string, long>> delta) {
    foreach (KeyValuePair<string, Dictionary<string, long>> package in delta) {
      foreach (KeyValuePair<string, long> version in package.Value) {
        Add(package.Key, version.Key, version.Value);
      }
    }
  }

  public void Load(Dictionary<string, Dictionary<string, long>> counts) {
    counts_.Clear();
    Apply(counts);
  }

  public Dictionary<string, Dictionary<string, long>> Export() {
    Dictionary<string, Dictionary<string, long>> result =
      new(StringComparer.Ordinal);
    foreach (KeyValuePair<string, PackageDownloads> package in counts_) {
      result[package.Key] = new Dictionary<string, long>(
        package.Value.versions,
        StringComparer.Ordinal
      );
    }

    return result;
  }

  private void Add(string lower_id, string lower_version, long count) {
    PackageDownloads downloads = counts_.GetOrAdd(
      lower_id,
      _ => new PackageDownloads()
    );
    downloads.versions.AddOrUpdate(lower_version, count, (_, n) => n + count);
    Interlocked.Add(ref downloads.total, count);
  }

  private sealed class PackageDownloads {
    public readonly ConcurrentDictionary<string, long> versions =
      new(StringComparer.Ordinal);

    public long total;
  }
}