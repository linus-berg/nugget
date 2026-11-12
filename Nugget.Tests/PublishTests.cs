using System.Net;
using System.Text;
using System.Text.Json;
using Nugget.Api.Storage;

namespace Nugget.Tests;

public sealed class PublishTests : IAsyncLifetime {
  private readonly NuggetFactory factory_ = new();

  public async Task InitializeAsync() {
    await factory_.StartAsync();
  }

  public async Task DisposeAsync() {
    await factory_.DisposeAsync();
  }

  [Fact]
  public async Task UploadsRequireAValidApiKey() {
    byte[] package = TestPackages.Create("Auth.Test", "1.0.0");
    Assert.Equal(
      HttpStatusCode.Unauthorized,
      (await factory_.UploadAsync(package, null)).StatusCode
    );
    Assert.Equal(
      HttpStatusCode.Unauthorized,
      (await factory_.UploadAsync(package, "wrong")).StatusCode
    );
    Assert.Equal(
      HttpStatusCode.Created,
      (await factory_.UploadAsync(package)).StatusCode
    );
  }

  [Fact]
  public async Task UploadsAreDisabledWithoutConfiguredKeys() {
    await using NuggetFactory factory =
      new NuggetFactory().WithSetting("Nugget:ApiKey", "");
    await factory.StartAsync();
    using HttpResponseMessage response = await factory.UploadAsync(
                                           TestPackages.Create(
                                             "No.Keys",
                                             "1.0.0"
                                           ),
                                           ""
                                         );
    Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
  }

  [Fact]
  public async Task UploadStoresThePackageByteForByte() {
    byte[] package = TestPackages.Create("Newtonsoft.Json", "13.0.1");
    using HttpResponseMessage response = await factory_.UploadAsync(package);
    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    JsonElement body = JsonDocument
                       .Parse(await response.Content.ReadAsStringAsync())
                       .RootElement;
    Assert.Equal("Newtonsoft.Json", body.GetProperty("id").GetString());
    Assert.Equal("13.0.1", body.GetProperty("version").GetString());
    Assert.Equal("created", body.GetProperty("status").GetString());

    Assert.Equal(
      package,
      factory_.Store.Read(
        "packages/newtonsoft.json/13.0.1/newtonsoft.json.13.0.1.nupkg"
      )
    );
    Assert.NotNull(
      factory_.Store.Read(
        "packages/newtonsoft.json/13.0.1/newtonsoft.json.nuspec"
      )
    );
    Assert.NotNull(factory_.Store.Read("metadata/newtonsoft.json.json.gz"));
  }

  [Fact]
  public async Task IdenticalReuploadsSucceedAndDifferentContentConflicts() {
    byte[] package = TestPackages.Create("Dup.Test", "1.0.0");
    Assert.Equal(
      HttpStatusCode.Created,
      (await factory_.UploadAsync(package)).StatusCode
    );
    Assert.Equal(
      HttpStatusCode.OK,
      (await factory_.UploadAsync(package)).StatusCode
    );

    byte[] different = TestPackages.Create(
      new TestPackage {
        Id = "Dup.Test",
        Version = "1.0.0",
        Salt = "other"
      }
    );
    Assert.Equal(
      HttpStatusCode.Conflict,
      (await factory_.UploadAsync(different)).StatusCode
    );
    Assert.Equal(
      package,
      factory_.Store.Read("packages/dup.test/1.0.0/dup.test.1.0.0.nupkg")
    );
  }

  [Fact]
  public async Task VersionsAreNormalizedLikeNuGetOrg() {
    // 1.0 and 1.0.0 are the same version; the first upload wins.
    Assert.Equal(
      HttpStatusCode.Created,
      (await factory_.UploadAsync(TestPackages.Create("Norm.Test", "1.0")))
      .StatusCode
    );
    Assert.Equal(
      HttpStatusCode.Conflict,
      (await factory_.UploadAsync(
         TestPackages.Create(
           new TestPackage {
             Id = "Norm.Test",
             Version = "1.0.0",
             Salt = "x"
           }
         )
       )).StatusCode
    );
    JsonElement versions =
      await factory_.GetJsonAsync("/v3-flatcontainer/norm.test/index.json");
    Assert.Equal(
      "1.0.0",
      Assert.Single(versions.GetProperty("versions").EnumerateArray())
            .GetString()
    );
  }

  [Fact]
  public async Task MultipartUploadsLikeNuGetPushWork() {
    using HttpResponseMessage response = await factory_.UploadAsync(
                                           TestPackages.Create(
                                             "Push.Test",
                                             "2.0.0"
                                           ),
                                           multipart: true
                                         );
    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    Assert.NotNull(
      factory_.Store.Read("packages/push.test/2.0.0/push.test.2.0.0.nupkg")
    );
  }

  [Fact]
  public async Task InvalidUploadsAreRejected() {
    Assert.Equal(
      HttpStatusCode.BadRequest,
      (await factory_.UploadAsync(Encoding.UTF8.GetBytes("not a zip")))
      .StatusCode
    );
    Assert.Equal(
      HttpStatusCode.BadRequest,
      (await factory_.UploadAsync([])).StatusCode
    );
    Assert.Equal(
      HttpStatusCode.BadRequest,
      (await factory_.UploadAsync(
         TestPackages.Create(
           new TestPackage {
             Id = "Symbols.Test",
             Version = "1.0.0",
             PackageType = "SymbolsPackage"
           }
         )
       )).StatusCode
    );
    Assert.DoesNotContain(factory_.Store.Keys, k => k.StartsWith("packages/"));
  }

  [Fact]
  public async Task OversizedUploadsAreRejected() {
    await using NuggetFactory factory =
      new NuggetFactory().WithSetting("Nugget:MaxPackageBytes", "100");
    await factory.StartAsync();
    using HttpResponseMessage response = await factory.UploadAsync(
                                           TestPackages.Create(
                                             "Big.Test",
                                             "1.0.0"
                                           )
                                         );
    Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
  }

  [Fact]
  public async Task UnlistHidesFromSearchButKeepsRestoreWorking() {
    await factory_.UploadOkAsync(TestPackages.Create("Unlist.Test", "1.0.0"));
    await factory_.UploadOkAsync(TestPackages.Create("Unlist.Test", "2.0.0"));
    using HttpResponseMessage unlist = await factory_.SendWithKeyAsync(
                                         HttpMethod.Delete,
                                         "/api/v2/package/Unlist.Test/2.0.0"
                                       );
    Assert.Equal(HttpStatusCode.OK, unlist.StatusCode);

    JsonElement search =
      await factory_.GetJsonAsync("/query?q=packageid:Unlist.Test");
    JsonElement hit =
      Assert.Single(search.GetProperty("data").EnumerateArray());
    Assert.Equal("1.0.0", hit.GetProperty("version").GetString());

    JsonElement versions =
      await factory_.GetJsonAsync("/v3-flatcontainer/unlist.test/index.json");
    Assert.Equal(2, versions.GetProperty("versions").GetArrayLength());
    using HttpResponseMessage download = await factory_.GetAsync(
                                           "/v3-flatcontainer/unlist.test/2.0.0/unlist.test.2.0.0.nupkg"
                                         );
    Assert.Equal(HttpStatusCode.OK, download.StatusCode);

    JsonElement leaf = await factory_.GetJsonAsync(
                         "/v3/registration5-gz-semver2/unlist.test/2.0.0.json"
                       );
    Assert.False(leaf.GetProperty("listed").GetBoolean());
    Assert.StartsWith("1900-01-01", leaf.GetProperty("published").GetString());

    using HttpResponseMessage relist = await factory_.SendWithKeyAsync(
                                         HttpMethod.Post,
                                         "/api/v2/package/unlist.test/2.0.0"
                                       );
    Assert.Equal(HttpStatusCode.OK, relist.StatusCode);
    search = await factory_.GetJsonAsync("/query?q=packageid:Unlist.Test");
    Assert.Equal(
      "2.0.0",
      Assert.Single(search.GetProperty("data").EnumerateArray())
            .GetProperty("version")
            .GetString()
    );
  }

  [Fact]
  public async Task PurgeDeletesTheVersionAndItsFiles() {
    await factory_.UploadOkAsync(
      TestPackages.Create(
        new TestPackage {
          Id = "Purge.Test",
          Version = "1.0.0",
          Readme = "# Hi"
        }
      )
    );
    await factory_.UploadOkAsync(TestPackages.Create("Purge.Test", "2.0.0"));
    using HttpResponseMessage response = await factory_.SendWithKeyAsync(
                                           HttpMethod.Delete,
                                           "/api/v2/package/Purge.Test/1.0.0?purge=true"
                                         );
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    Assert.DoesNotContain(
      factory_.Store.Keys,
      k => k.StartsWith("packages/purge.test/1.0.0/")
    );
    JsonElement versions =
      await factory_.GetJsonAsync("/v3-flatcontainer/purge.test/index.json");
    Assert.Equal(
      "2.0.0",
      Assert.Single(versions.GetProperty("versions").EnumerateArray())
            .GetString()
    );

    using HttpResponseMessage last = await factory_.SendWithKeyAsync(
                                       HttpMethod.Delete,
                                       "/api/v2/package/Purge.Test/2.0.0?purge=true"
                                     );
    Assert.Equal(HttpStatusCode.OK, last.StatusCode);
    Assert.DoesNotContain(
      factory_.Store.Keys,
      k => k == StorageKeys.Metadata("purge.test")
    );
    Assert.Equal(
      HttpStatusCode.NotFound,
      (await factory_.GetAsync("/v3-flatcontainer/purge.test/index.json"))
      .StatusCode
    );
  }

  [Fact]
  public async Task WriteOperationsOnMissingPackagesReturnNotFound() {
    using HttpResponseMessage response = await factory_.SendWithKeyAsync(
                                           HttpMethod.Delete,
                                           "/api/v2/package/Missing.Test/1.0.0"
                                         );
    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
  }

  [Fact]
  public async Task ConcurrentUploadsOfOnePackageKeepEveryVersion() {
    await Task.WhenAll(
      Enumerable.Range(1, 20)
                .Select(
                  i => factory_.UploadOkAsync(
                    TestPackages.Create("Parallel.Test", $"1.0.{i}")
                  )
                )
    );
    JsonElement versions =
      await factory_.GetJsonAsync("/v3-flatcontainer/parallel.test/index.json");
    Assert.Equal(20, versions.GetProperty("versions").GetArrayLength());
  }

  [Fact]
  public async Task V2DownloadUrlsRedirectToTheFlatContainer() {
    await factory_.UploadOkAsync(TestPackages.Create("Redirect.Test", "1.2.3"));
    using HttpClient client = factory_.CreateClient(
      new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions {
        AllowAutoRedirect = false
      }
    );
    using HttpResponseMessage response =
      await client.GetAsync("/api/v2/package/Redirect.Test/1.2.3");
    Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    Assert.EndsWith(
      "/v3-flatcontainer/redirect.test/1.2.3/redirect.test.1.2.3.nupkg",
      response.Headers.Location!.ToString()
    );
  }
}