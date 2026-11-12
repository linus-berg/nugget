using System.Net;
using System.Text.Json;

namespace Nugget.Tests;

public sealed class ProtocolTests : IAsyncLifetime {
  private readonly NuggetFactory factory_ = new();

  public async Task InitializeAsync() {
    await factory_.StartAsync();
  }

  public async Task DisposeAsync() {
    await factory_.DisposeAsync();
  }

  [Fact]
  public async Task ServiceIndexPointsEverythingAtTheRequestHost() {
    using HttpClient client = factory_.CreateClient();
    client.DefaultRequestHeaders.Host = "api.nuget.org";
    string json = await client.GetStringAsync("/v3/index.json");
    JsonElement index = JsonDocument.Parse(json).RootElement;
    Assert.Equal("3.0.0", index.GetProperty("version").GetString());
    Dictionary<string, string> resources = index.GetProperty("resources")
                                                .EnumerateArray()
                                                .ToDictionary(
                                                  r => r.GetProperty("@type")
                                                    .GetString()!,
                                                  r => r.GetProperty("@id")
                                                    .GetString()!
                                                );
    Assert.Equal(
      "http://api.nuget.org/v3-flatcontainer/",
      resources["PackageBaseAddress/3.0.0"]
    );
    Assert.Equal(
      "http://api.nuget.org/query",
      resources["SearchQueryService/3.5.0"]
    );
    Assert.Equal(
      "http://api.nuget.org/autocomplete",
      resources["SearchAutocompleteService"]
    );
    Assert.Equal(
      "http://api.nuget.org/v3/registration5-gz-semver2/",
      resources["RegistrationsBaseUrl/3.6.0"]
    );
    Assert.Equal(
      "http://api.nuget.org/v3/registration5-semver1/",
      resources["RegistrationsBaseUrl"]
    );
    Assert.Equal(
      "http://api.nuget.org/api/v2/package",
      resources["PackagePublish/2.0.0"]
    );
    Assert.Equal(
      "http://api.nuget.org/v3-flatcontainer/{lower_id}/{lower_version}/readme",
      resources["ReadmeUriTemplate/6.13.0"]
    );
    Assert.All(
      resources.Values,
      url => Assert.StartsWith("http://api.nuget.org/", url)
    );
    // No vulnerability data uploaded yet.
    Assert.False(resources.ContainsKey("VulnerabilityInfo/6.7.0"));
  }

  [Fact]
  public async Task PublicBaseUrlOverridesTheRequestHost() {
    await using NuggetFactory factory = new NuggetFactory().WithSetting(
      "Nugget:PublicBaseUrl",
      "https://api.nuget.org/"
    );
    await factory.StartAsync();
    JsonElement index = await factory.GetJsonAsync("/v3/index.json");
    Assert.All(
      index.GetProperty("resources").EnumerateArray(),
      r => Assert.StartsWith(
        "https://api.nuget.org/",
        r.GetProperty("@id").GetString()
      )
    );
  }

  [Fact]
  public async Task PackagesKeepTheirExactIdAndVersion() {
    await factory_.UploadOkAsync(
      TestPackages.Create("Serilog.Sinks.Console", "6.1.1-Beta.2")
    );

    JsonElement versions = await factory_.GetJsonAsync(
                             "/v3-flatcontainer/serilog.sinks.console/index.json"
                           );
    Assert.Equal(
      "6.1.1-beta.2",
      versions.GetProperty("versions")[0].GetString()
    );

    JsonElement registration = await factory_.GetJsonAsync(
                                 "/v3/registration5-gz-semver2/serilog.sinks.console/index.json"
                               );
    JsonElement entry = registration.GetProperty("items")[0]
                                    .GetProperty("items")[0]
                                    .GetProperty("catalogEntry");
    Assert.Equal("Serilog.Sinks.Console", entry.GetProperty("id").GetString());
    Assert.Equal("6.1.1-Beta.2", entry.GetProperty("version").GetString());

    JsonElement search = await factory_.GetJsonAsync(
                           "/query?q=serilog&prerelease=true&semVerLevel=2.0.0"
                         );
    JsonElement hit =
      Assert.Single(search.GetProperty("data").EnumerateArray());
    Assert.Equal("Serilog.Sinks.Console", hit.GetProperty("id").GetString());
    Assert.Equal("6.1.1-Beta.2", hit.GetProperty("version").GetString());

    // Mixed case URLs work too.
    using HttpResponseMessage download = await factory_.GetAsync(
                                           "/v3-flatcontainer/Serilog.Sinks.Console/6.1.1-Beta.2/serilog.sinks.console.6.1.1-beta.2.nupkg"
                                         );
    Assert.Equal(HttpStatusCode.OK, download.StatusCode);
  }

  [Fact]
  public async Task FlatContainerServesPackagesAndManifests() {
    byte[] package = TestPackages.Create("Flat.Test", "1.0.0");
    await factory_.UploadOkAsync(package);
    using HttpResponseMessage nupkg = await factory_.GetAsync(
                                        "/v3-flatcontainer/flat.test/1.0.0/flat.test.1.0.0.nupkg"
                                      );
    Assert.Equal(HttpStatusCode.OK, nupkg.StatusCode);
    Assert.Equal(
      "application/octet-stream",
      nupkg.Content.Headers.ContentType?.MediaType
    );
    Assert.Equal(package, await nupkg.Content.ReadAsByteArrayAsync());

    using HttpResponseMessage nuspec = await factory_.GetAsync(
                                         "/v3-flatcontainer/flat.test/1.0.0/flat.test.nuspec"
                                       );
    Assert.Equal(HttpStatusCode.OK, nuspec.StatusCode);
    Assert.Contains(
      "<id>Flat.Test</id>",
      await nuspec.Content.ReadAsStringAsync()
    );

    Assert.Equal(
      HttpStatusCode.NotFound,
      (await factory_.GetAsync(
         "/v3-flatcontainer/flat.test/9.9.9/flat.test.9.9.9.nupkg"
       )).StatusCode
    );
    Assert.Equal(
      HttpStatusCode.NotFound,
      (await factory_.GetAsync("/v3-flatcontainer/missing.test/index.json"))
      .StatusCode
    );
    Assert.Equal(
      HttpStatusCode.NotFound,
      (await factory_.GetAsync(
         "/v3-flatcontainer/flat.test/1.0.0/other.1.0.0.nupkg"
       )).StatusCode
    );

    using HttpClient client = factory_.CreateClient();
    using HttpRequestMessage head = new(
      HttpMethod.Head,
      "/v3-flatcontainer/flat.test/1.0.0/flat.test.1.0.0.nupkg"
    );
    using HttpResponseMessage head_response = await client.SendAsync(head);
    Assert.Equal(HttpStatusCode.OK, head_response.StatusCode);
    Assert.Equal(package.Length, head_response.Content.Headers.ContentLength);
  }

  [Fact]
  public async Task FlatContainerVersionsAreOrderedBySemVer() {
    foreach (string version in new[] {
               "10.0.0",
               "2.0.0",
               "2.0.0-beta",
               "2.0.0-alpha.10",
               "2.0.0-alpha.9"
             }) {
      await factory_.UploadOkAsync(TestPackages.Create("Order.Test", version));
    }

    JsonElement versions =
      await factory_.GetJsonAsync("/v3-flatcontainer/order.test/index.json");
    Assert.Equal(
      [
        "2.0.0-alpha.9",
        "2.0.0-alpha.10",
        "2.0.0-beta",
        "2.0.0",
        "10.0.0"
      ],
      versions.GetProperty("versions")
              .EnumerateArray()
              .Select(v => v.GetString()!)
              .ToArray()
    );
  }

  [Fact]
  public async Task EmbeddedReadmeIconAndLicenseAreServed() {
    await factory_.UploadOkAsync(
      TestPackages.Create(
        new TestPackage {
          Id = "Assets.Test",
          Version = "1.0.0",
          Readme = "# Assets",
          Icon = TestPackages.S_PNG,
          LicenseFile = "Do what you want."
        }
      )
    );
    using HttpResponseMessage readme = await factory_.GetAsync(
                                         "/v3-flatcontainer/assets.test/1.0.0/readme"
                                       );
    Assert.Equal("# Assets", await readme.Content.ReadAsStringAsync());
    using HttpResponseMessage icon = await factory_.GetAsync(
                                       "/v3-flatcontainer/assets.test/1.0.0/icon"
                                     );
    Assert.Equal("image/png", icon.Content.Headers.ContentType?.MediaType);
    Assert.Equal(TestPackages.S_PNG, await icon.Content.ReadAsByteArrayAsync());
    using HttpResponseMessage license = await factory_.GetAsync(
                                          "/v3-flatcontainer/assets.test/1.0.0/license"
                                        );
    Assert.Equal(
      "Do what you want.",
      await license.Content.ReadAsStringAsync()
    );

    JsonElement entry = (await factory_.GetJsonAsync(
                           "/v3/registration5-gz-semver2/assets.test/index.json"
                         )).GetProperty("items")[0]
                           .GetProperty("items")[0]
                           .GetProperty("catalogEntry");
    Assert.EndsWith(
      "/v3-flatcontainer/assets.test/1.0.0/icon",
      entry.GetProperty("iconUrl").GetString()
    );
    Assert.EndsWith(
      "/v3-flatcontainer/assets.test/1.0.0/license",
      entry.GetProperty("licenseUrl").GetString()
    );
    Assert.EndsWith(
      "/v3-flatcontainer/assets.test/1.0.0/readme",
      entry.GetProperty("readmeUrl").GetString()
    );
  }

  [Fact]
  public async Task RegistrationHasDependenciesAndHidesSemVer2FromOldHives() {
    await factory_.UploadOkAsync(
      TestPackages.Create(
        new TestPackage {
          Id = "Reg.Test",
          Version = "1.0.0",
          LicenseExpression = "MIT",
          Dependencies = [
            ("net8.0", "Newtonsoft.Json", "13.0.1"),
            ("netstandard2.0", "System.Text.Json", "[8.0.0, 9.0.0)")
          ]
        }
      )
    );
    await factory_.UploadOkAsync(
      TestPackages.Create("Reg.Test", "2.0.0-beta.1")
    );
    await factory_.UploadOkAsync(
      TestPackages.Create("Reg.Test", "2.0.0+build.5")
    );

    JsonElement semver2 = await factory_.GetJsonAsync(
                            "/v3/registration5-gz-semver2/reg.test/index.json"
                          );
    Assert.Equal(1, semver2.GetProperty("count").GetInt32());
    JsonElement page = semver2.GetProperty("items")[0];
    Assert.Equal(3, page.GetProperty("count").GetInt32());
    Assert.Equal("1.0.0", page.GetProperty("lower").GetString());
    Assert.Equal("2.0.0", page.GetProperty("upper").GetString());

    JsonElement entry =
      page.GetProperty("items")[0].GetProperty("catalogEntry");
    Assert.Equal(
      "https://licenses.nuget.org/MIT",
      entry.GetProperty("licenseUrl").GetString()
    );
    JsonElement groups = entry.GetProperty("dependencyGroups");
    Assert.Equal(2, groups.GetArrayLength());
    JsonElement net8 = groups.EnumerateArray()
                             .Single(
                               g => g.GetProperty("targetFramework")
                                     .GetString() ==
                                    "net8.0"
                             );
    JsonElement dependency = net8.GetProperty("dependencies")[0];
    Assert.Equal("Newtonsoft.Json", dependency.GetProperty("id").GetString());
    Assert.Equal("[13.0.1, )", dependency.GetProperty("range").GetString());
    Assert.EndsWith(
      "/v3/registration5-gz-semver2/newtonsoft.json/index.json",
      dependency.GetProperty("registration").GetString()
    );
    Assert.EndsWith(
      "/v3-flatcontainer/reg.test/1.0.0/reg.test.1.0.0.nupkg",
      page.GetProperty("items")[0].GetProperty("packageContent").GetString()
    );

    // The old hives only know the SemVer 1 version.
    JsonElement semver1 = await factory_.GetJsonAsync(
                            "/v3/registration5-semver1/reg.test/index.json"
                          );
    JsonElement only = Assert.Single(
      semver1.GetProperty("items")[0].GetProperty("items").EnumerateArray()
    );
    Assert.Equal(
      "1.0.0",
      only.GetProperty("catalogEntry").GetProperty("version").GetString()
    );
    Assert.Equal(
      HttpStatusCode.NotFound,
      (await factory_.GetAsync(
         "/v3/registration5-gz-semver1/reg.test/2.0.0.json"
       )).StatusCode
    );

    JsonElement leaf = await factory_.GetJsonAsync(
                         "/v3/registration5-gz-semver2/reg.test/2.0.0.json"
                       );
    Assert.EndsWith(
      "/v3/catalog0/data/reg.test/2.0.0.json",
      leaf.GetProperty("catalogEntry").GetString()
    );
    JsonElement details =
      await factory_.GetJsonAsync(
        leaf.GetProperty("catalogEntry").GetString()!
      );
    Assert.Equal("2.0.0+build.5", details.GetProperty("version").GetString());
    Assert.Equal(
      "SHA512",
      details.GetProperty("packageHashAlgorithm").GetString()
    );
    Assert.True(details.GetProperty("packageSize").GetInt64() > 0);
  }

  [Fact]
  public async Task LargeRegistrationsArePaged() {
    for (int i = 0; i < 150; i++) {
      await factory_.UploadOkAsync(
        TestPackages.Create("Paged.Test", $"1.0.{i}")
      );
    }

    JsonElement index = await factory_.GetJsonAsync(
                          "/v3/registration5-gz-semver2/paged.test/index.json"
                        );
    Assert.Equal(3, index.GetProperty("count").GetInt32());
    JsonElement[] pages = index.GetProperty("items").EnumerateArray().ToArray();
    Assert.All(pages, p => Assert.False(p.TryGetProperty("items", out _)));
    Assert.Equal(
      [64, 64, 22],
      pages.Select(p => p.GetProperty("count").GetInt32()).ToArray()
    );

    JsonElement second =
      await factory_.GetJsonAsync(pages[1].GetProperty("@id").GetString()!);
    Assert.Equal(64, second.GetProperty("items").GetArrayLength());
    Assert.Equal("1.0.64", second.GetProperty("lower").GetString());
    Assert.Equal(
      "1.0.64",
      second.GetProperty("items")[0]
            .GetProperty("catalogEntry")
            .GetProperty("version")
            .GetString()
    );
  }

  [Fact]
  public async Task UnknownRegistrationsReturnNotFound() {
    Assert.Equal(
      HttpStatusCode.NotFound,
      (await factory_.GetAsync("/v3/registration5-gz-semver2/nope/index.json"))
      .StatusCode
    );
    Assert.Equal(
      HttpStatusCode.NotFound,
      (await factory_.GetAsync(
         "/v3/registration5-gz-semver2/..%2F..%2Fmetadata/index.json"
       )).StatusCode
    );
  }

  [Fact]
  public async Task SearchRanksExactIdMatchesFirst() {
    await factory_.UploadOkAsync(
      TestPackages.Create(
        new TestPackage {
          Id = "Json.Helpers",
          Version = "1.0.0",
          Description = "Helps with json"
        }
      )
    );
    await factory_.UploadOkAsync(
      TestPackages.Create(
        new TestPackage {
          Id = "Newtonsoft.Json",
          Version = "13.0.3",
          Title = "Json.NET",
          Tags = "json serialization",
          Description =
            "Json.NET is a popular high-performance JSON framework for .NET"
        }
      )
    );
    await factory_.UploadOkAsync(
      TestPackages.Create(
        new TestPackage {
          Id = "Unrelated",
          Version = "1.0.0"
        }
      )
    );

    JsonElement search =
      await factory_.GetJsonAsync("/query?q=newtonsoft.json");
    Assert.Equal(
      "Newtonsoft.Json",
      search.GetProperty("data")[0].GetProperty("id").GetString()
    );

    search = await factory_.GetJsonAsync("/query?q=json");
    Assert.Equal(2, search.GetProperty("totalHits").GetInt32());

    search = await factory_.GetJsonAsync("/query?q=tags:serialization");
    Assert.Equal(
      "Newtonsoft.Json",
      Assert.Single(search.GetProperty("data").EnumerateArray())
            .GetProperty("id")
            .GetString()
    );

    search =
      await factory_.GetJsonAsync("/query?q=json%20nothing-matches-this");
    Assert.Equal(0, search.GetProperty("totalHits").GetInt32());

    JsonElement result =
      (await factory_.GetJsonAsync("/query?q=packageid:newtonsoft.json"))
      .GetProperty("data")[0];
    Assert.Equal("Json.NET", result.GetProperty("title").GetString());
    Assert.Equal("json", result.GetProperty("tags")[0].GetString());
    Assert.Equal("Test Author", result.GetProperty("authors")[0].GetString());
    Assert.EndsWith(
      "/v3/registration5-semver1/newtonsoft.json/index.json",
      result.GetProperty("registration").GetString()
    );
    Assert.Equal(
      "13.0.3",
      result.GetProperty("versions")[0].GetProperty("version").GetString()
    );
  }

  [Fact]
  public async Task SearchFiltersPrereleaseSemVer2AndPackageTypes() {
    await factory_.UploadOkAsync(TestPackages.Create("Filter.Test", "1.0.0"));
    await factory_.UploadOkAsync(
      TestPackages.Create("Filter.Test", "2.0.0-beta")
    );
    await factory_.UploadOkAsync(
      TestPackages.Create("Filter.Test", "3.0.0-beta.1")
    );
    await factory_.UploadOkAsync(
      TestPackages.Create("Filter.Only.Prerelease", "1.0.0-alpha")
    );
    await factory_.UploadOkAsync(
      TestPackages.Create(
        new TestPackage {
          Id = "Filter.Tool",
          Version = "1.0.0",
          PackageType = "DotnetTool"
        }
      )
    );

    JsonElement stable = await factory_.GetJsonAsync("/query?q=filter");
    Assert.Equal(2, stable.GetProperty("totalHits").GetInt32());
    JsonElement test = stable.GetProperty("data")
                             .EnumerateArray()
                             .Single(
                               d => d.GetProperty("id").GetString() ==
                                    "Filter.Test"
                             );
    Assert.Equal("1.0.0", test.GetProperty("version").GetString());
    Assert.Equal(1, test.GetProperty("versions").GetArrayLength());

    JsonElement prerelease =
      await factory_.GetJsonAsync("/query?q=filter.test&prerelease=true");
    Assert.Equal(
      "2.0.0-beta",
      prerelease.GetProperty("data")[0].GetProperty("version").GetString()
    );

    JsonElement semver2 = await factory_.GetJsonAsync(
                            "/query?q=filter.test&prerelease=true&semVerLevel=2.0.0"
                          );
    JsonElement latest = semver2.GetProperty("data")[0];
    Assert.Equal("3.0.0-beta.1", latest.GetProperty("version").GetString());
    Assert.Equal(3, latest.GetProperty("versions").GetArrayLength());

    JsonElement tools =
      await factory_.GetJsonAsync("/query?q=&packageType=DotnetTool");
    Assert.Equal(
      "Filter.Tool",
      Assert.Single(tools.GetProperty("data").EnumerateArray())
            .GetProperty("id")
            .GetString()
    );
    Assert.Equal(
      "DotnetTool",
      tools.GetProperty("data")[0].GetProperty("packageTypes")[0]
           .GetProperty("name")
           .GetString()
    );
  }

  [Fact]
  public async Task SearchPagesWithSkipAndTake() {
    for (int i = 0; i < 25; i++) {
      await factory_.UploadOkAsync(
        TestPackages.Create($"Paging.Package{i:D2}", "1.0.0")
      );
    }

    JsonElement first = await factory_.GetJsonAsync("/query?q=paging&take=10");
    Assert.Equal(25, first.GetProperty("totalHits").GetInt32());
    Assert.Equal(10, first.GetProperty("data").GetArrayLength());
    JsonElement last =
      await factory_.GetJsonAsync("/query?q=paging&skip=20&take=10");
    Assert.Equal(5, last.GetProperty("data").GetArrayLength());
    JsonElement browse = await factory_.GetJsonAsync("/query?take=0");
    Assert.Equal(25, browse.GetProperty("totalHits").GetInt32());
    Assert.Equal(0, browse.GetProperty("data").GetArrayLength());
  }

  [Fact]
  public async Task AutocompleteCompletesIdsAndVersions() {
    await factory_.UploadOkAsync(
      TestPackages.Create("Auto.Complete.One", "1.0.0")
    );
    await factory_.UploadOkAsync(
      TestPackages.Create("Auto.Complete.One", "1.1.0-pre")
    );
    await factory_.UploadOkAsync(
      TestPackages.Create("Other.Complete", "1.0.0")
    );

    JsonElement ids = await factory_.GetJsonAsync("/autocomplete?q=auto");
    Assert.Equal(
      "Auto.Complete.One",
      Assert.Single(ids.GetProperty("data").EnumerateArray()).GetString()
    );
    ids = await factory_.GetJsonAsync("/autocomplete?q=complete");
    Assert.Equal(2, ids.GetProperty("totalHits").GetInt32());

    JsonElement versions =
      await factory_.GetJsonAsync("/autocomplete?id=auto.complete.one");
    Assert.Equal(
      ["1.0.0"],
      versions.GetProperty("data")
              .EnumerateArray()
              .Select(v => v.GetString()!)
              .ToArray()
    );
    versions = await factory_.GetJsonAsync(
                 "/autocomplete?id=Auto.Complete.One&prerelease=true"
               );
    Assert.Equal(2, versions.GetProperty("data").GetArrayLength());
  }

  [Fact]
  public async Task DownloadsAreCountedAndShownInSearch() {
    await factory_.UploadOkAsync(TestPackages.Create("Counted.Test", "1.0.0"));
    for (int i = 0; i < 3; i++) {
      using HttpResponseMessage response = await factory_.GetAsync(
                                             "/v3-flatcontainer/counted.test/1.0.0/counted.test.1.0.0.nupkg"
                                           );
      Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    JsonElement hit =
      (await factory_.GetJsonAsync("/query?q=packageid:counted.test"))
      .GetProperty("data")[0];
    Assert.Equal(3, hit.GetProperty("totalDownloads").GetInt64());
    Assert.Equal(
      3,
      hit.GetProperty("versions")[0].GetProperty("downloads").GetInt64()
    );
  }

  [Fact]
  public async Task HealthReportsReadiness() {
    JsonElement health = await factory_.GetJsonAsync("/health/ready");
    Assert.Equal("ok", health.GetProperty("status").GetString());
  }
}