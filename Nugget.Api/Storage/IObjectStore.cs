namespace Nugget.Api.Storage;

/// <summary>
///   The object storage everything lives in. Keys are relative to the
///   configured prefix and always use <c>/</c> as separator.
/// </summary>
public interface IObjectStore {
  string Description { get; }

  Task InitializeAsync(CancellationToken ct);

  /// <summary>Reads a whole (small) object, or <c>null</c> when it does not exist.</summary>
  Task<StoredObject?> GetAsync(string key, CancellationToken ct);

  /// <summary>Opens an object for streaming, or <c>null</c> when it does not exist.</summary>
  Task<ObjectReadStream?> OpenReadAsync(string key, CancellationToken ct);

  Task<ObjectInfo?> HeadAsync(string key, CancellationToken ct);

  /// <summary>Writes an object and returns its new ETag.</summary>
  /// <exception cref="PreconditionFailedException">
  ///   <see cref="PutOptions.IfMatch" /> or <see cref="PutOptions.IfNoneMatch" />
  ///   did not hold.
  /// </exception>
  Task<string?> PutAsync(string key, ReadOnlyMemory<byte> content,
                         PutOptions options, CancellationToken ct);

  /// <summary>Uploads a large file (multipart where useful).</summary>
  Task PutFileAsync(string key, string path, string content_type,
                    IReadOnlyDictionary<string, string>? metadata,
                    CancellationToken ct);

  Task DeleteAsync(string key, CancellationToken ct);

  Task DeleteManyAsync(IReadOnlyCollection<string> keys, CancellationToken ct);

  /// <summary>Lists objects in key order, starting after <paramref name="start_after" />.</summary>
  IAsyncEnumerable<ObjectInfo> ListAsync(string prefix, string? start_after,
                                         CancellationToken ct);
}

public sealed class PutOptions {
  public string ContentType { get; init; } = "application/octet-stream";

  /// <summary>Only write when the current object has this ETag.</summary>
  public string? IfMatch { get; init; }

  /// <summary>Only write when the object does not exist yet.</summary>
  public bool IfNoneMatch { get; init; }

  /// <summary>User metadata (<c>x-amz-meta-*</c>).</summary>
  public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

public sealed class ObjectInfo {
  public required string Key { get; init; }
  public long Size { get; init; }
  public string? ETag { get; init; }
  public DateTimeOffset LastModified { get; init; }
  public string? ContentType { get; init; }

  public IReadOnlyDictionary<string, string> Metadata { get; init; } =
    new Dictionary<string, string>();
}

public sealed class StoredObject {
  public required ObjectInfo Info { get; init; }
  public required byte[] Content { get; init; }
}

/// <summary>An open object body; dispose it to release the connection.</summary>
public sealed class ObjectReadStream : IAsyncDisposable {
  private readonly IDisposable? owner_;

  public ObjectReadStream(ObjectInfo info, Stream stream, IDisposable? owner) {
    Info = info;
    Stream = stream;
    owner_ = owner;
  }

  public ObjectInfo Info { get; }
  public Stream Stream { get; }

  public async ValueTask DisposeAsync() {
    await Stream.DisposeAsync();
    owner_?.Dispose();
  }
}

/// <summary>A conditional write lost against a concurrent writer.</summary>
public sealed class PreconditionFailedException(string key, Exception? inner)
  : Exception($"Conditional write of '{key}' failed", inner);