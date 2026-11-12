using System.Globalization;
using Nugget.Api.Storage;

namespace Nugget.Api.Catalog;

public enum ChangeKind {
  /// <summary>A package's metadata document changed; subject = lower case ID.</summary>
  PACKAGE,

  /// <summary>Download counts; the object body holds the counts.</summary>
  DOWNLOADS,

  /// <summary>Vulnerability data changed; subject = file name.</summary>
  VULNERABILITIES
}

public sealed class ChangeEntry {
  public required string Key { get; init; }
  public required long Ticks { get; init; }
  public required string Instance { get; init; }
  public required ChangeKind Kind { get; init; }
  public required string Subject { get; init; }
}

/// <summary>
///   A log of changes in S3 that lets every instance keep its in-memory
///   catalog current without a database. Each change is a tiny object named
///   <c>changes/{utc ticks}-{instance}-{kind}-{subject}</c>; S3 lists keys in
///   order, so polling is a single "list after my cursor" request.
/// </summary>
/// <remarks>
///   Keys are written with the writer's clock and may become visible slightly
///   out of order, so every poll re-lists the last <see cref="S_WINDOW" /> and
///   skips keys it has already seen.
/// </remarks>
public sealed class ChangeFeed(IObjectStore store) {
  public static readonly TimeSpan S_WINDOW = TimeSpan.FromMinutes(2);

  private readonly Lock lock_ = new();

  private readonly Dictionary<string, ChangeEntry> processed_ =
    new(StringComparer.Ordinal);

  private long cursor_ticks_;

  // Changes at or before the floor count as processed without being listed.
  private long floor_ticks_;
  private long last_written_ticks_;

  public string InstanceId { get; } = Guid.NewGuid().ToString("N");

  /// <summary>Ticks of the newest change seen (or written) by this instance.</summary>
  public long CursorTicks {
    get {
      lock (lock_) {
        return cursor_ticks_;
      }
    }
  }

  /// <summary>
  ///   Treats everything at or before <paramref name="ticks" />, and the given
  ///   keys, as processed.
  /// </summary>
  public void Reset(long ticks, IEnumerable<string> processed_keys) {
    lock (lock_) {
      cursor_ticks_ = ticks;
      floor_ticks_ = ticks;
      processed_.Clear();
      foreach (string key in processed_keys) {
        ChangeEntry? entry = Parse(key);
        if (entry != null) {
          processed_[key] = entry;
        }
      }
    }
  }

  public async Task<ChangeEntry> AppendAsync(ChangeKind kind, string subject,
                                             byte[]? body,
                                             CancellationToken ct) {
    long ticks = NextTicks();
    string key = $"{StorageKeys.C_CHANGES_PREFIX}{ticks:D19}-{InstanceId}-" +
                 $"{KindCode(kind)}-{subject}";
    await store.PutAsync(
      key,
      body ?? [],
      new PutOptions {
        ContentType = body == null ? "text/plain" : "application/gzip"
      },
      ct
    );
    ChangeEntry entry = Parse(key)!;
    MarkProcessed(entry);
    return entry;
  }

  /// <summary>Changes that appeared since the last call, in key order.</summary>
  public async Task<IReadOnlyList<ChangeEntry>> ReadNewAsync(
    CancellationToken ct) {
    long from;
    lock (lock_) {
      from = Math.Max(floor_ticks_, cursor_ticks_ - S_WINDOW.Ticks);
    }

    List<ChangeEntry> result = [];
    string start_after = $"{StorageKeys.C_CHANGES_PREFIX}{from:D19}";
    await foreach (ObjectInfo item in store.ListAsync(
                     StorageKeys.C_CHANGES_PREFIX,
                     start_after,
                     ct
                   )) {
      ChangeEntry? entry = Parse(item.Key);
      if (entry != null && MarkProcessed(entry)) {
        result.Add(entry);
      }
    }

    Prune();
    return result;
  }

  /// <summary>
  ///   All changes after <paramref name="ticks" />, whether seen or not
  ///   (does not move the cursor).
  /// </summary>
  public async Task<IReadOnlyList<ChangeEntry>> ListSinceAsync(long ticks,
    CancellationToken ct) {
    List<ChangeEntry> result = [];
    await foreach (ObjectInfo item in store.ListAsync(
                     StorageKeys.C_CHANGES_PREFIX,
                     $"{StorageKeys.C_CHANGES_PREFIX}{Math.Max(0, ticks):D19}",
                     ct
                   )) {
      ChangeEntry? entry = Parse(item.Key);
      if (entry != null) {
        result.Add(entry);
      }
    }

    return result;
  }

  /// <summary>
  ///   Position to store in a snapshot: everything at or before
  ///   <c>Watermark</c> plus <c>Recent</c> is reflected in this instance's
  ///   catalog. This instance's own package changes are left out of
  ///   <c>Recent</c> so readers of the snapshot re-apply them; that is
  ///   harmless and covers changes that are still being applied.
  /// </summary>
  public (long Watermark, List<string> Recent) GetCheckpoint() {
    lock (lock_) {
      long watermark = Math.Max(0, cursor_ticks_ - S_WINDOW.Ticks);
      List<string> recent = processed_.Values
                                      .Where(
                                        e => e.Ticks > watermark &&
                                             (e.Kind == ChangeKind.DOWNLOADS ||
                                              e.Instance != InstanceId)
                                      )
                                      .Select(e => e.Key)
                                      .Order(StringComparer.Ordinal)
                                      .ToList();
      return (watermark, recent);
    }
  }

  /// <summary>Removes changes older than <paramref name="before_ticks" />.</summary>
  public async Task<int> DeleteOlderThanAsync(long before_ticks,
                                              CancellationToken ct) {
    List<string> keys = [];
    await foreach (ObjectInfo item in store.ListAsync(
                     StorageKeys.C_CHANGES_PREFIX,
                     null,
                     ct
                   )) {
      ChangeEntry? entry = Parse(item.Key);
      if (entry == null) {
        continue;
      }

      if (entry.Ticks >= before_ticks) {
        break;
      }

      keys.Add(item.Key);
    }

    if (keys.Count > 0) {
      await store.DeleteManyAsync(keys, ct);
    }

    return keys.Count;
  }

  public static ChangeEntry? Parse(string key) {
    if (!key.StartsWith(
          StorageKeys.C_CHANGES_PREFIX,
          StringComparison.Ordinal
        )) {
      return null;
    }

    string name = key[StorageKeys.C_CHANGES_PREFIX.Length..];
    string[] parts = name.Split('-', 4);
    if (parts.Length != 4 ||
        parts[0].Length != 19 ||
        !long.TryParse(
          parts[0],
          NumberStyles.None,
          CultureInfo.InvariantCulture,
          out long ticks
        ) ||
        parts[2].Length != 1) {
      return null;
    }

    ChangeKind? kind = parts[2][0] switch {
      'p' => ChangeKind.PACKAGE,
      'd' => ChangeKind.DOWNLOADS,
      'v' => ChangeKind.VULNERABILITIES,
      _ => null
    };
    if (kind == null) {
      return null;
    }

    return new ChangeEntry {
      Key = key,
      Ticks = ticks,
      Instance = parts[1],
      Kind = kind.Value,
      Subject = parts[3]
    };
  }

  private static char KindCode(ChangeKind kind) {
    return kind switch {
      ChangeKind.PACKAGE => 'p',
      ChangeKind.DOWNLOADS => 'd',
      ChangeKind.VULNERABILITIES => 'v',
      _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
  }

  private long NextTicks() {
    lock (lock_) {
      long ticks = Math.Max(DateTime.UtcNow.Ticks, last_written_ticks_ + 1);
      last_written_ticks_ = ticks;
      return ticks;
    }
  }

  /// <returns><c>false</c> when the change was already processed.</returns>
  private bool MarkProcessed(ChangeEntry entry) {
    lock (lock_) {
      if (entry.Ticks <= floor_ticks_ || !processed_.TryAdd(entry.Key, entry)) {
        return false;
      }

      cursor_ticks_ = Math.Max(cursor_ticks_, entry.Ticks);
      return true;
    }
  }

  private void Prune() {
    lock (lock_) {
      floor_ticks_ = Math.Max(floor_ticks_, cursor_ticks_ - 2 * S_WINDOW.Ticks);
      List<string> stale = processed_.Values
                                     .Where(e => e.Ticks <= floor_ticks_)
                                     .Select(e => e.Key)
                                     .ToList();
      foreach (string key in stale) {
        processed_.Remove(key);
      }
    }
  }
}