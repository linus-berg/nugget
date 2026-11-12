namespace Nugget.Api.Packages;

/// <summary>Async mutual exclusion per key (package ID) within this process.</summary>
public sealed class KeyedLock {
  private readonly Dictionary<string, Entry> entries_ =
    new(StringComparer.Ordinal);

  public async Task<IDisposable>
    AcquireAsync(string key, CancellationToken ct) {
    Entry entry;
    lock (entries_) {
      if (!entries_.TryGetValue(key, out Entry? existing)) {
        existing = new Entry();
        entries_[key] = existing;
      }

      existing.users++;
      entry = existing;
    }

    try {
      await entry.semaphore.WaitAsync(ct);
    } catch {
      Release(key, entry, false);
      throw;
    }

    return new Releaser(this, key, entry);
  }

  private void Release(string key, Entry entry, bool held) {
    if (held) {
      entry.semaphore.Release();
    }

    lock (entries_) {
      entry.users--;
      if (entry.users == 0) {
        entries_.Remove(key);
        entry.semaphore.Dispose();
      }
    }
  }

  private sealed class Entry {
    public readonly SemaphoreSlim semaphore = new(1, 1);
    public int users;
  }

  private sealed class Releaser(KeyedLock owner, string key, Entry entry)
    : IDisposable {
    private int disposed_;

    public void Dispose() {
      if (Interlocked.Exchange(ref disposed_, 1) == 0) {
        owner.Release(key, entry, true);
      }
    }
  }
}