using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nugget.Api.Catalog;
using Nugget.Api.Configuration;
using Nugget.Api.Packages;
using Nugget.Api.Storage;

namespace Nugget.Tests;

public sealed class SearchQueryTests {
  [Fact]
  public void ParsesWordsPhrasesAndFields() {
    SearchQuery query = SearchQuery.Parse(
      "json  \"high performance\" packageid:Newtonsoft.Json tags:\"a b\" owner:james http://x"
    );
    Assert.Collection(
      query.Terms,
      t => Assert.Equal((SearchField.ANY, "json"), (t.Field, t.Value)),
      t => Assert.Equal(
        (SearchField.ANY, "high performance"),
        (t.Field, t.Value)
      ),
      t => Assert.Equal(
        (SearchField.PACKAGE_ID, "Newtonsoft.Json"),
        (t.Field, t.Value)
      ),
      t => Assert.Equal((SearchField.TAGS, "a b"), (t.Field, t.Value)),
      t => Assert.Equal((SearchField.AUTHORS, "james"), (t.Field, t.Value)),
      // Unknown "fields" are plain words.
      t => Assert.Equal((SearchField.ANY, "http://x"), (t.Field, t.Value))
    );
  }

  [Fact]
  public void EmptyQueriesHaveNoTerms() {
    Assert.True(SearchQuery.Parse(null).IsEmpty);
    Assert.True(SearchQuery.Parse("   ").IsEmpty);
  }
}

public sealed class ChangeFeedTests {
  [Fact]
  public void ParsesChangeKeys() {
    ChangeEntry? entry = ChangeFeed.Parse(
      "changes/0638600000000000000-0123456789abcdef0123456789abcdef-p-my-package.id"
    );
    Assert.NotNull(entry);
    Assert.Equal(638600000000000000, entry.Ticks);
    Assert.Equal("0123456789abcdef0123456789abcdef", entry.Instance);
    Assert.Equal(ChangeKind.PACKAGE, entry.Kind);
    // Package IDs may contain dashes.
    Assert.Equal("my-package.id", entry.Subject);
  }

  [Theory]
  [InlineData("changes/123-abc-p-x")]
  [InlineData("changes/0638600000000000000-abc-z-x")]
  [InlineData("metadata/x.json.gz")]
  public void IgnoresOtherKeys(string key) {
    Assert.Null(ChangeFeed.Parse(key));
  }

  [Fact]
  public async Task SkipsChangesItHasSeen() {
    InMemoryObjectStore store = new();
    ChangeFeed writer = new(store);
    ChangeFeed reader = new(store);
    reader.Reset(0, []);
    await writer.AppendAsync(
      ChangeKind.PACKAGE,
      "a",
      null,
      CancellationToken.None
    );
    await writer.AppendAsync(
      ChangeKind.PACKAGE,
      "b",
      null,
      CancellationToken.None
    );
    Assert.Equal(2, (await reader.ReadNewAsync(CancellationToken.None)).Count);
    Assert.Empty(await reader.ReadNewAsync(CancellationToken.None));
    // A writer has already applied its own changes.
    Assert.Empty(await writer.ReadNewAsync(CancellationToken.None));
  }
}

public sealed class PackageInspectorTests {
  [Fact]
  public void ReadsMetadataFromTheNuspec() {
    byte[] package = TestPackages.Create(
      new TestPackage {
        Id = "Inspect.Me",
        Version = "1.0.0-RC.1+sha.abc",
        Tags = "one two,three",
        Authors = "A, B",
        Dependencies = [
          (null, "Dep.One", "[1.0.0-beta.1, )")
        ]
      }
    );
    InspectedPackage inspected = PackageInspector.Inspect(
      new MemoryStream(package),
      package.Length,
      "hash"
    );
    PackageVersionRecord record = inspected.Record;
    Assert.Equal("Inspect.Me", record.Id);
    Assert.Equal("1.0.0-RC.1+sha.abc", record.Version);
    Assert.Equal("1.0.0-rc.1", record.LowerVersion);
    Assert.True(record.IsSemVer2);
    Assert.True(record.IsPrerelease);
    Assert.Equal(["one", "two", "three"], record.GetTags().ToArray());
    Assert.Equal(["A", "B"], record.GetAuthors().ToArray());
    DependencyGroupRecord group = Assert.Single(record.DependencyGroups);
    Assert.Null(group.TargetFramework);
    Assert.Equal("[1.0.0-beta.1, )", Assert.Single(group.Dependencies).Range);
  }

  [Fact]
  public void SemVer2DependencyRangesMakeThePackageSemVer2() {
    byte[] package = TestPackages.Create(
      new TestPackage {
        Id = "Depends.On.SemVer2",
        Version = "1.0.0",
        Dependencies = [
          ("net8.0", "Other", "2.0.0-beta.1")
        ]
      }
    );
    Assert.True(
      PackageInspector.Inspect(new MemoryStream(package), package.Length, "h")
                      .Record.IsSemVer2
    );
  }

  [Fact]
  public void DetectsRasterImagesOnly() {
    Assert.Equal(
      "image/png",
      PackageInspector.DetectImageType(TestPackages.S_PNG)
    );
    Assert.Equal(
      "image/jpeg",
      PackageInspector.DetectImageType([0xFF, 0xD8, 0xFF, 0xE0])
    );
    Assert.Equal(
      "application/octet-stream",
      PackageInspector.DetectImageType("<svg onload=alert(1)>"u8)
    );
  }
}

public sealed class PackageRepositoryTests {
  [Fact]
  public async Task WritesThatLoseARaceAreRetried() {
    ConflictingStore store = new(new InMemoryObjectStore(), 2);
    using PackageRepository repository = new(
      store,
      Options.Create(new NuggetOptions()),
      NullLogger<PackageRepository>.Instance
    );
    int calls = 0;
    PackageRecord? written = await repository.UpdateAsync(
                               "race.me",
                               _ => {
                                 calls++;
                                 return new PackageRecord {
                                   LowerId = "race.me",
                                   Versions = [
                                     new PackageVersionRecord {
                                       Id = "Race.Me",
                                       Version = "1.0.0"
                                     }
                                   ]
                                 };
                               },
                               CancellationToken.None
                             );
    // Two lost races, then the third attempt goes through.
    Assert.Equal(3, calls);
    Assert.NotNull(written);
    Assert.NotNull(
      await repository.GetFreshAsync("race.me", CancellationToken.None)
    );
  }

  /// <summary>Rejects the first conditional metadata writes, like a busy S3.</summary>
  private sealed class ConflictingStore(IObjectStore inner, int conflicts)
    : IObjectStore {
    private int remaining_ = conflicts;

    public string Description => inner.Description;

    public Task InitializeAsync(CancellationToken ct) {
      return inner.InitializeAsync(ct);
    }

    public Task<StoredObject?> GetAsync(string key, CancellationToken ct) {
      return inner.GetAsync(key, ct);
    }

    public Task<ObjectReadStream?> OpenReadAsync(
      string key, CancellationToken ct) {
      return inner.OpenReadAsync(key, ct);
    }

    public Task<ObjectInfo?> HeadAsync(string key, CancellationToken ct) {
      return inner.HeadAsync(key, ct);
    }

    public Task<string?> PutAsync(string key, ReadOnlyMemory<byte> content,
                                  PutOptions options, CancellationToken ct) {
      if (key.StartsWith(StorageKeys.C_METADATA_PREFIX) &&
          Interlocked.Decrement(ref remaining_) >= 0) {
        throw new PreconditionFailedException(key, null);
      }

      return inner.PutAsync(key, content, options, ct);
    }

    public Task PutFileAsync(string key, string path, string content_type,
                             IReadOnlyDictionary<string, string>? metadata,
                             CancellationToken ct) {
      return inner.PutFileAsync(key, path, content_type, metadata, ct);
    }

    public Task DeleteAsync(string key, CancellationToken ct) {
      return inner.DeleteAsync(key, ct);
    }

    public Task DeleteManyAsync(IReadOnlyCollection<string> keys,
                                CancellationToken ct) {
      return inner.DeleteManyAsync(keys, ct);
    }

    public IAsyncEnumerable<ObjectInfo> ListAsync(string prefix,
                                                  string? start_after,
                                                  CancellationToken ct) {
      return inner.ListAsync(prefix, start_after, ct);
    }
  }
}