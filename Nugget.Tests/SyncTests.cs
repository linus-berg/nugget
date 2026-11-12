using System.Net;
using System.Text.Json;
using Nugget.Api.Json;
using Nugget.Api.Packages;
using Nugget.Api.Storage;

namespace Nugget.Tests;

/// <summary>Several instances sharing one bucket, snapshots and recovery.</summary>
public sealed class SyncTests {
  [Fact]
  public async Task UploadsThroughOneInstanceReachTheOthers() {
    InMemoryObjectStore store = new();
    await using NuggetFactory a = await new NuggetFactory(store).StartAsync();
    await using NuggetFactory b = await new NuggetFactory(store).StartAsync();

    await a.UploadOkAsync(TestPackages.Create("Shared.Package", "1.0.0"));
    // Not in b's catalog before it polls, so b answers 404 without S3.
    Assert.Equal(
      HttpStatusCode.NotFound,
      (await b.GetAsync("/v3-flatcontainer/shared.package/index.json"))
      .StatusCode
    );

    await b.Synchronizer.PollAsync(CancellationToken.None);
    JsonElement versions =
      await b.GetJsonAsync("/v3-flatcontainer/shared.package/index.json");
    Assert.Equal("1.0.0", versions.GetProperty("versions")[0].GetString());

    // b adds a version to the same package; a learns about it.
    await b.UploadOkAsync(TestPackages.Create("Shared.Package", "2.0.0"));
    await a.Synchronizer.PollAsync(CancellationToken.None);
    JsonElement search =
      await a.GetJsonAsync("/query?q=packageid:shared.package");
    Assert.Equal(
      "2.0.0",
      search.GetProperty("data")[0].GetProperty("version").GetString()
    );
    JsonElement registration = await a.GetJsonAsync(
                                 "/v3/registration5-gz-semver2/shared.package/index.json"
                               );
    Assert.Equal(
      2,
      registration.GetProperty("items")[0].GetProperty("count").GetInt32()
    );
  }

  [Fact]
  public async Task ConcurrentUploadsThroughSeveralInstancesKeepEveryVersion() {
    InMemoryObjectStore store = new();
    await using NuggetFactory a = await new NuggetFactory(store).StartAsync();
    await using NuggetFactory b = await new NuggetFactory(store).StartAsync();
    await Task.WhenAll(
      Enumerable.Range(0, 30)
                .Select(
                  i => (i % 2 == 0 ? a : b).UploadOkAsync(
                    TestPackages.Create("Contended.Package", $"1.0.{i}")
                  )
                )
    );
    await a.Synchronizer.PollAsync(CancellationToken.None);
    await b.Synchronizer.PollAsync(CancellationToken.None);
    foreach (NuggetFactory instance in new[] {
               a,
               b
             }) {
      JsonElement versions = await instance.GetJsonAsync(
                               "/v3-flatcontainer/contended.package/index.json"
                             );
      Assert.Equal(30, versions.GetProperty("versions").GetArrayLength());
    }
  }

  [Fact]
  public async Task NewInstancesLoadTheSnapshotAndReplayLaterChanges() {
    InMemoryObjectStore store = new();
    await using (NuggetFactory first =
                 await new NuggetFactory(store).StartAsync()) {
      await first.UploadOkAsync(TestPackages.Create("Snap.One", "1.0.0"));
      using HttpResponseMessage download = await first.GetAsync(
                                             "/v3-flatcontainer/snap.one/1.0.0/snap.one.1.0.0.nupkg"
                                           );
      await first.Synchronizer.WriteSnapshotAsync(CancellationToken.None);
      // After the snapshot: must come from the change log.
      await first.UploadOkAsync(TestPackages.Create("Snap.Two", "1.0.0"));
      using HttpResponseMessage second_download = await first.GetAsync(
                                                    "/v3-flatcontainer/snap.one/1.0.0/snap.one.1.0.0.nupkg"
                                                  );
      await first.Synchronizer.FlushDownloadsAsync(CancellationToken.None);
    }

    Assert.Contains(StorageKeys.C_SNAPSHOT, store.Keys);
    await using NuggetFactory second =
      await new NuggetFactory(store).StartAsync();
    Assert.Equal(2, second.Catalog.Count);
    JsonElement hit = (await second.GetJsonAsync("/query?q=packageid:snap.one"))
      .GetProperty("data")[0];
    // One download in the snapshot, one in the change log; neither twice.
    Assert.Equal(2, hit.GetProperty("totalDownloads").GetInt64());
    Assert.NotNull(second.Catalog.Find("snap.two"));
  }

  [Fact]
  public async Task TheCatalogIsRebuiltWhenTheSnapshotIsMissingOrBroken() {
    InMemoryObjectStore store = new();
    await using (NuggetFactory first =
                 await new NuggetFactory(store).StartAsync()) {
      await first.UploadOkAsync(TestPackages.Create("Rebuild.One", "1.0.0"));
      await first.UploadOkAsync(TestPackages.Create("Rebuild.Two", "1.0.0"));
      await first.Synchronizer.WriteSnapshotAsync(CancellationToken.None);
    }

    store.Replace(StorageKeys.C_SNAPSHOT, "garbage"u8.ToArray());
    await using NuggetFactory second =
      await new NuggetFactory(store).StartAsync();
    Assert.Equal(2, second.Catalog.Count);
    // ... and a fresh snapshot replaces the broken one (written right after
    // the catalog is ready).
    for (int i = 0;
         i < 100 && store.Read(StorageKeys.C_SNAPSHOT)!.Length == 7;
         i++) {
      await Task.Delay(50);
    }

    Assert.NotEqual("garbage"u8.ToArray(), store.Read(StorageKeys.C_SNAPSHOT));
  }

  [Fact]
  public async Task AdminRebuildRecoversPackagesAddedOutsideTheChangeLog() {
    InMemoryObjectStore store = new();
    await using NuggetFactory instance =
      await new NuggetFactory(store).StartAsync();
    await instance.UploadOkAsync(
      TestPackages.Create("Visible.Package", "1.0.0")
    );
    // Simulate a lost change: a metadata document nobody announced.
    PackageRecord visible = NuggetJson.DeserializeGzip(
      store.Read(StorageKeys.Metadata("visible.package"))!,
      NuggetJson.S_CONTEXT.PackageRecord
    )!;
    PackageRecord restored = visible with {
      LowerId = "restored.package",
      Versions = visible.Versions
                        .Select(
                          v => v with {
                            Id = "Restored.Package"
                          }
                        )
                        .ToList()
    };
    await store.PutAsync(
      StorageKeys.Metadata("restored.package"),
      NuggetJson.SerializeGzip(restored, NuggetJson.S_CONTEXT.PackageRecord),
      new PutOptions(),
      CancellationToken.None
    );
    Assert.Equal(1, instance.Catalog.Count);

    using HttpResponseMessage response = await instance.SendWithKeyAsync(
                                           HttpMethod.Post,
                                           "/api/v2/admin/rebuild"
                                         );
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    Assert.Equal(2, instance.Catalog.Count);
  }

  [Fact]
  public async Task SnapshotsRemoveChangesOlderThanTheRetentionPeriod() {
    InMemoryObjectStore store = new();
    await using NuggetFactory instance = await new NuggetFactory(store)
                                               .WithSetting(
                                                 "Nugget:Sync:ChangeRetentionHours",
                                                 "1"
                                               )
                                               .StartAsync();
    long old_ticks = DateTime.UtcNow.AddHours(-3).Ticks;
    string old_key = $"{StorageKeys.C_CHANGES_PREFIX}{old_ticks:D19}-" +
                     $"{Guid.NewGuid():N}-p-old.package";
    await store.PutAsync(
      old_key,
      Array.Empty<byte>(),
      new PutOptions(),
      CancellationToken.None
    );
    await instance.UploadOkAsync(
      TestPackages.Create("Recent.Package", "1.0.0")
    );

    await instance.Synchronizer.WriteSnapshotAsync(CancellationToken.None);
    List<string> changes = store.Keys
                                .Where(
                                  k => k.StartsWith(
                                    StorageKeys.C_CHANGES_PREFIX
                                  )
                                )
                                .ToList();
    Assert.DoesNotContain(old_key, changes);
    Assert.Contains(changes, k => k.EndsWith("-p-recent.package"));
  }

  [Fact]
  public async Task AdminRebuildKeepsDownloadCounts() {
    InMemoryObjectStore store = new();
    await using NuggetFactory instance =
      await new NuggetFactory(store).StartAsync();
    await instance.UploadOkAsync(TestPackages.Create("Kept.Counts", "1.0.0"));
    using HttpResponseMessage download = await instance.GetAsync(
                                           "/v3-flatcontainer/kept.counts/1.0.0/kept.counts.1.0.0.nupkg"
                                         );
    await instance.Synchronizer.WriteSnapshotAsync(CancellationToken.None);
    // The counts now live only in the snapshot, as after log retention.
    await store.DeleteManyAsync(
      store.Keys.Where(k => k.StartsWith(StorageKeys.C_CHANGES_PREFIX))
           .ToList(),
      CancellationToken.None
    );

    using HttpResponseMessage response = await instance.SendWithKeyAsync(
                                           HttpMethod.Post,
                                           "/api/v2/admin/rebuild"
                                         );
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    JsonElement hit =
      (await instance.GetJsonAsync("/query?q=packageid:kept.counts"))
      .GetProperty("data")[0];
    Assert.Equal(1, hit.GetProperty("totalDownloads").GetInt64());
  }
}