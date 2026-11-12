using System.Runtime.CompilerServices;
using Nugget.Api.Storage;

namespace Nugget.Tests;

/// <summary>
///   An S3 stand-in with the semantics Nugget relies on: ETags, conditional
///   writes and key ordered listings. Share one between several factories to
///   simulate several instances.
/// </summary>
public sealed class InMemoryObjectStore : IObjectStore {
  private readonly SortedDictionary<string, Entry> objects_ =
    new(StringComparer.Ordinal);

  public string Description => "memory";

  public int PutCount { get; private set; }

  public IReadOnlyList<string> Keys {
    get {
      lock (objects_) {
        return objects_.Keys.ToList();
      }
    }
  }

  public Task InitializeAsync(CancellationToken ct) {
    return Task.CompletedTask;
  }

  public Task<StoredObject?> GetAsync(string key, CancellationToken ct) {
    lock (objects_) {
      return Task.FromResult(
        objects_.TryGetValue(key, out Entry? entry)
          ? new StoredObject {
            Info = entry.Info,
            Content = entry.Content
          }
          : null
      );
    }
  }

  public async Task<ObjectReadStream?> OpenReadAsync(string key,
    CancellationToken ct) {
    StoredObject? stored = await GetAsync(key, ct);
    return stored == null
             ? null
             : new ObjectReadStream(
               stored.Info,
               new MemoryStream(stored.Content, false),
               null
             );
  }

  public Task<ObjectInfo?> HeadAsync(string key, CancellationToken ct) {
    lock (objects_) {
      return Task.FromResult(
        objects_.TryGetValue(key, out Entry? entry) ? entry.Info : null
      );
    }
  }

  public Task<string?> PutAsync(string key, ReadOnlyMemory<byte> content,
                                PutOptions options, CancellationToken ct) {
    lock (objects_) {
      bool exists = objects_.TryGetValue(key, out Entry? current);
      if ((options.IfNoneMatch && exists) ||
          (options.IfMatch != null && current?.Info.ETag != options.IfMatch)) {
        throw new PreconditionFailedException(key, null);
      }

      string etag = $"\"{Guid.NewGuid():N}\"";
      objects_[key] = new Entry {
        Content = content.ToArray(),
        Info = new ObjectInfo {
          Key = key,
          Size = content.Length,
          ETag = etag,
          LastModified = DateTimeOffset.UtcNow,
          ContentType = options.ContentType,
          Metadata = new Dictionary<string, string>(
            options.Metadata ?? new Dictionary<string, string>(),
            StringComparer.OrdinalIgnoreCase
          )
        }
      };
      PutCount++;
      return Task.FromResult<string?>(etag);
    }
  }

  public async Task PutFileAsync(string key, string path, string content_type,
                                 IReadOnlyDictionary<string, string>? metadata,
                                 CancellationToken ct) {
    byte[] content = await File.ReadAllBytesAsync(path, ct);
    await PutAsync(
      key,
      content,
      new PutOptions {
        ContentType = content_type,
        Metadata = metadata
      },
      ct
    );
  }

  public Task DeleteAsync(string key, CancellationToken ct) {
    lock (objects_) {
      objects_.Remove(key);
    }

    return Task.CompletedTask;
  }

  public Task DeleteManyAsync(IReadOnlyCollection<string> keys,
                              CancellationToken ct) {
    lock (objects_) {
      foreach (string key in keys) {
        objects_.Remove(key);
      }
    }

    return Task.CompletedTask;
  }

  public async IAsyncEnumerable<ObjectInfo> ListAsync(string prefix,
    string? start_after,
    [EnumeratorCancellation] CancellationToken ct) {
    List<ObjectInfo> items;
    lock (objects_) {
      items = objects_.Where(
                        pair => pair.Key.StartsWith(
                                  prefix,
                                  StringComparison.Ordinal
                                ) &&
                                (start_after == null ||
                                 string.CompareOrdinal(pair.Key, start_after) >
                                 0)
                      )
                      .Select(pair => pair.Value.Info)
                      .ToList();
    }

    foreach (ObjectInfo item in items) {
      await Task.Yield();
      yield return item;
    }
  }

  /// <summary>Overwrites an object's content (to simulate outside changes).</summary>
  public void Replace(string key, byte[] content) {
    lock (objects_) {
      Entry entry = objects_[key];
      objects_[key] = new Entry {
        Content = content,
        Info = new ObjectInfo {
          Key = key,
          Size = content.Length,
          ETag = $"\"{Guid.NewGuid():N}\"",
          LastModified = DateTimeOffset.UtcNow,
          ContentType = entry.Info.ContentType,
          Metadata = entry.Info.Metadata
        }
      };
    }
  }

  public byte[]? Read(string key) {
    lock (objects_) {
      return objects_.TryGetValue(key, out Entry? entry) ? entry.Content : null;
    }
  }

  private sealed class Entry {
    public required byte[] Content { get; init; }
    public required ObjectInfo Info { get; init; }
  }
}