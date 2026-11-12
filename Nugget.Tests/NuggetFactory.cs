using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Nugget.Api;
using Nugget.Api.Catalog;
using Nugget.Api.Storage;

namespace Nugget.Tests;

/// <summary>
///   Runs Nugget in-process on an <see cref="InMemoryObjectStore" />. Several
///   factories sharing one store behave like several instances.
/// </summary>
public sealed class NuggetFactory : WebApplicationFactory<Program> {
  public const string C_API_KEY = "test-api-key";

  private readonly Dictionary<string, string?> settings_ = new();

  public NuggetFactory(InMemoryObjectStore? store = null) {
    Store = store ?? new InMemoryObjectStore();
  }

  public InMemoryObjectStore Store { get; }

  public PackageCatalog Catalog =>
    Services.GetRequiredService<PackageCatalog>();

  public CatalogSynchronizer Synchronizer =>
    Services.GetRequiredService<CatalogSynchronizer>();

  public NuggetFactory WithSetting(string key, string? value) {
    settings_[key] = value;
    return this;
  }

  protected override void ConfigureWebHost(IWebHostBuilder builder) {
    builder.UseEnvironment("Testing");
    builder.UseSetting("Nugget:ApiKey", C_API_KEY);
    builder.UseSetting("Nugget:LogRequests", "false");
    // Tests drive synchronization explicitly.
    builder.UseSetting("Nugget:Sync:PollIntervalSeconds", "0");
    foreach (KeyValuePair<string, string?> setting in settings_) {
      builder.UseSetting(setting.Key, setting.Value);
    }

    builder.ConfigureTestServices(
      services => { services.AddSingleton<IObjectStore>(Store); }
    );
  }

  /// <summary>Starts the server and waits until the catalog is loaded.</summary>
  public async Task<NuggetFactory> StartAsync() {
    _ = Server;
    await Catalog.WhenReady.WaitAsync(TimeSpan.FromSeconds(30));
    return this;
  }

  public async Task<HttpResponseMessage> UploadAsync(byte[] package,
    string? api_key = C_API_KEY,
    bool multipart = false) {
    using HttpClient client = CreateClient();
    HttpContent content;
    if (multipart) {
      MultipartFormDataContent form = new();
      ByteArrayContent file = new(package);
      file.Headers.ContentType =
        new MediaTypeHeaderValue("application/octet-stream");
      form.Add(file, "package", "package.nupkg");
      content = form;
    } else {
      content = new ByteArrayContent(package);
      content.Headers.ContentType =
        new MediaTypeHeaderValue("application/octet-stream");
    }

    using HttpRequestMessage request = new(HttpMethod.Put, "/api/v2/package") {
      Content = content
    };
    if (api_key != null) {
      request.Headers.Add("X-NuGet-ApiKey", api_key);
    }

    return await client.SendAsync(request);
  }

  public async Task UploadOkAsync(byte[] package) {
    using HttpResponseMessage response = await UploadAsync(package);
    string body = await response.Content.ReadAsStringAsync();
    Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {body}");
  }

  public async Task<HttpResponseMessage> SendWithKeyAsync(HttpMethod method,
    string url,
    HttpContent? content = null) {
    using HttpClient client = CreateClient();
    using HttpRequestMessage request = new(method, url) {
      Content = content
    };
    request.Headers.Add("X-NuGet-ApiKey", C_API_KEY);
    return await client.SendAsync(request);
  }

  public async Task<JsonElement> GetJsonAsync(string url) {
    using HttpClient client = CreateClient();
    using HttpResponseMessage response = await client.GetAsync(url);
    string body = await response.Content.ReadAsStringAsync();
    Assert.True(
      response.IsSuccessStatusCode,
      $"GET {url}: {response.StatusCode} {body}"
    );
    return JsonDocument.Parse(body).RootElement.Clone();
  }

  public async Task<HttpResponseMessage> GetAsync(string url) {
    using HttpClient client = CreateClient();
    return await client.GetAsync(url);
  }
}