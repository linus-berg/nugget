using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Nugget.Api.Configuration;
using Nugget.Api.Json;
using Nugget.Api.Storage;
using Polly;
using Polly.Retry;

namespace Nugget.Api.Packages;

/// <summary>
///   Reads and writes package metadata documents
///   (<c>metadata/{id}.json.gz</c>) with an in-memory cache. Writes use
///   optimistic concurrency (S3 conditional writes), so several instances
///   can add versions of the same package at once.
/// </summary>
public sealed class PackageRepository : IDisposable {
  private const int C_MAX_RETRIES_ = 9;
  private static readonly TimeSpan S_CACHE_LIFETIME_ = TimeSpan.FromMinutes(10);

  private readonly MemoryCache cache_;

  private readonly ConcurrentDictionary<string, Task<CachedRecord?>> loading_ =
    new(StringComparer.Ordinal);

  // Another writer changed the document between our read and our write.
  private readonly ResiliencePipeline conflict_retry_;
  private readonly IObjectStore store_;

  public PackageRepository(IObjectStore store, IOptions<NuggetOptions> options,
                           ILogger<PackageRepository> logger) {
    store_ = store;
    cache_ = new MemoryCache(
      new MemoryCacheOptions {
        SizeLimit = Math.Max(1, options.Value.MetadataCacheVersions)
      }
    );
    conflict_retry_ = new ResiliencePipelineBuilder()
                      .AddRetry(
                        new RetryStrategyOptions {
                          ShouldHandle = new PredicateBuilder()
                            .Handle<PreconditionFailedException>(),
                          MaxRetryAttempts = C_MAX_RETRIES_,
                          BackoffType = DelayBackoffType.Exponential,
                          UseJitter = true,
                          Delay = TimeSpan.FromMilliseconds(20),
                          MaxDelay = TimeSpan.FromSeconds(1),
                          OnRetry = args => {
                            logger.LogDebug(
                              "Concurrent update ({Reason}), retry {Attempt}",
                              args.Outcome.Exception?.Message,
                              args.AttemptNumber + 1
                            );
                            return ValueTask.CompletedTask;
                          }
                        }
                      )
                      .Build();
  }

  public void Dispose() {
    cache_.Dispose();
  }

  public async Task<PackageRecord?> GetAsync(string lower_id,
                                             CancellationToken ct) {
    if (cache_.TryGetValue(lower_id, out CachedRecord? cached) &&
        cached != null) {
      return cached.Record;
    }

    return (await LoadSharedAsync(lower_id, ct))?.Record;
  }

  /// <summary>Reads the document from S3, bypassing the cache.</summary>
  public async Task<PackageRecord?> GetFreshAsync(string lower_id,
                                                  CancellationToken ct) {
    Invalidate(lower_id);
    // Not shared with loads already in flight: those may predate the change
    // that made the caller ask for a fresh copy.
    CachedRecord? loaded = await LoadAsync(lower_id, ct);
    Remember(lower_id, loaded);
    return loaded?.Record;
  }

  public void Invalidate(string lower_id) {
    cache_.Remove(lower_id);
  }

  /// <summary>
  ///   Applies <paramref name="update" /> to the current document and stores
  ///   the result, retrying when another writer got there first. The update
  ///   must be side effect free (it can run more than once); it returns the
  ///   same instance for "no change" and <c>null</c> (or a record without
  ///   versions) to delete the document.
  /// </summary>
  public async Task<PackageRecord?> UpdateAsync(string lower_id,
                                                Func<PackageRecord?,
                                                  PackageRecord?> update,
                                                CancellationToken ct) {
    return await conflict_retry_.ExecuteAsync(
             async token => await TryUpdateAsync(lower_id, update, token),
             ct
           );
  }

  /// <exception cref="PreconditionFailedException">
  ///   Lost against another writer.
  /// </exception>
  private async Task<PackageRecord?> TryUpdateAsync(string lower_id,
                                                    Func<PackageRecord?,
                                                      PackageRecord?> update,
                                                    CancellationToken ct) {
    // Always start from S3: a cached copy may predate another instance's
    // write, and not every store enforces conditional writes.
    string key = StorageKeys.Metadata(lower_id);
    CachedRecord? current = await LoadAsync(lower_id, ct);
    PackageRecord? updated = update(current?.Record);
    if (ReferenceEquals(updated, current?.Record)) {
      Remember(lower_id, current);
      return updated;
    }

    if (updated == null || updated.Versions.Count == 0) {
      if (current != null) {
        await store_.DeleteAsync(key, ct);
      }

      Invalidate(lower_id);
      return null;
    }

    try {
      string? etag = await store_.PutAsync(
                       key,
                       NuggetJson.SerializeGzip(
                         updated,
                         NuggetJson.S_CONTEXT.PackageRecord
                       ),
                       new PutOptions {
                         ContentType = "application/gzip",
                         IfMatch = current?.ETag,
                         IfNoneMatch = current == null
                       },
                       ct
                     );
      Remember(
        lower_id,
        new CachedRecord {
          Record = updated,
          ETag = etag
        }
      );
      return updated;
    } catch (PreconditionFailedException) {
      Invalidate(lower_id);
      throw;
    }
  }

  /// <summary>Lower case IDs of all packages with a metadata document.</summary>
  public async IAsyncEnumerable<string> ListIdsAsync(
    [EnumeratorCancellation] CancellationToken ct) {
    await foreach (ObjectInfo item in store_.ListAsync(
                     StorageKeys.C_METADATA_PREFIX,
                     null,
                     ct
                   )) {
      string? lower_id = StorageKeys.IdFromMetadataKey(item.Key);
      if (lower_id != null) {
        yield return lower_id;
      }
    }
  }

  /// <summary>Loads once even when many requests ask for the same package.</summary>
  private async Task<CachedRecord?> LoadSharedAsync(string lower_id,
                                                    CancellationToken ct) {
    TaskCompletionSource<CachedRecord?> source = new(
      TaskCreationOptions.RunContinuationsAsynchronously
    );
    Task<CachedRecord?> task = loading_.GetOrAdd(lower_id, source.Task);
    if (task != source.Task) {
      return await task.WaitAsync(ct);
    }

    try {
      CachedRecord? loaded = await LoadAsync(lower_id, CancellationToken.None);
      Remember(lower_id, loaded);
      source.SetResult(loaded);
      return loaded;
    } catch (Exception ex) {
      source.SetException(ex);
      throw;
    } finally {
      loading_.TryRemove(
        new KeyValuePair<string, Task<CachedRecord?>>(lower_id, source.Task)
      );
    }
  }

  private async Task<CachedRecord?> LoadAsync(string lower_id,
                                              CancellationToken ct) {
    StoredObject? stored = await store_.GetAsync(
                             StorageKeys.Metadata(lower_id),
                             ct
                           );
    if (stored == null) {
      return null;
    }

    PackageRecord record = NuggetJson.DeserializeGzip(
                             stored.Content,
                             NuggetJson.S_CONTEXT.PackageRecord
                           ) ??
                           throw new InvalidDataException(
                             $"Empty metadata document for '{lower_id}'"
                           );
    return new CachedRecord {
      Record = record,
      ETag = stored.Info.ETag
    };
  }

  /// <summary>Caches a document unless a newer revision is cached already.</summary>
  private void Remember(string lower_id, CachedRecord? cached) {
    if (cached == null) {
      return;
    }

    lock (cache_) {
      if (cache_.TryGetValue(lower_id, out CachedRecord? existing) &&
          existing != null &&
          existing.Record.Revision > cached.Record.Revision) {
        return;
      }

      Store(lower_id, cached);
    }
  }

  private void Store(string lower_id, CachedRecord cached) {
    cache_.Set(
      lower_id,
      cached,
      new MemoryCacheEntryOptions {
        Size = Math.Max(1, cached.Record.Versions.Count),
        AbsoluteExpirationRelativeToNow = S_CACHE_LIFETIME_
      }
    );
  }

  private sealed class CachedRecord {
    public required PackageRecord Record { get; init; }
    public string? ETag { get; init; }
  }
}