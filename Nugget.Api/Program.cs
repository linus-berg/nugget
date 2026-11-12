using System.Diagnostics;
using System.IO.Compression;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Options;
using Nugget.Api.Catalog;
using Nugget.Api.Configuration;
using Nugget.Api.Endpoints;
using Nugget.Api.Packages;
using Nugget.Api.Storage;
using Nugget.Api.Vulnerabilities;

namespace Nugget.Api;

/// <summary>
///   Nugget: an offline NuGet server backed only by S3 that speaks the
///   nuget.org V3 protocol, so api.nuget.org can be pointed at it.
/// </summary>
public class Program {
  public static async Task Main(string[] args) {
    WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

    builder.Services.Configure<NuggetOptions>(
      builder.Configuration.GetSection(NuggetOptions.C_SECTION)
    );
    builder.Services.Configure<StorageOptions>(
      builder.Configuration.GetSection(StorageOptions.C_SECTION)
    );
    builder.Services.AddSingleton<IObjectStore>(
      services => new S3ObjectStore(
        services.GetRequiredService<IOptions<StorageOptions>>().Value,
        services.GetRequiredService<ILogger<S3ObjectStore>>()
      )
    );
    builder.Services.AddSingleton<DownloadStatistics>();
    builder.Services.AddSingleton<PackageCatalog>();
    builder.Services.AddSingleton<ChangeFeed>();
    builder.Services.AddSingleton<PackageRepository>();
    builder.Services.AddSingleton<PackagePublisher>();
    builder.Services.AddSingleton<VulnerabilityStore>();
    builder.Services.AddSingleton<CatalogSynchronizer>();
    builder.Services.AddHostedService<CatalogSyncService>();
    builder.Services.AddProblemDetails();
    builder.Services.AddResponseCompression(
      options => {
        // Registration and search documents compress ~10x; packages are
        // zip files already and are left alone.
        options.EnableForHttps = true;
        options.Providers.Add<BrotliCompressionProvider>();
        options.Providers.Add<GzipCompressionProvider>();
        options.MimeTypes = [
          "application/json",
          "text/xml",
          "text/markdown",
          "text/plain"
        ];
      }
    );
    builder.Services.Configure<BrotliCompressionProviderOptions>(
      options => options.Level = CompressionLevel.Fastest
    );
    builder.Services.Configure<GzipCompressionProviderOptions>(
      options => options.Level = CompressionLevel.Fastest
    );

    WebApplication app = builder.Build();
    LogConfiguration(app);

    app.UseExceptionHandler();
    app.UseStatusCodePages();
    app.Use(LogRequestsAsync);
    app.UseResponseCompression();
    app.MapServiceIndexEndpoints();
    app.MapFlatContainerEndpoints();
    app.MapRegistrationEndpoints();
    app.MapSearchEndpoints();
    app.MapVulnerabilityEndpoints();
    app.MapPublishEndpoints();
    ILogger fallback_logger = app.Services
                                 .GetRequiredService<ILoggerFactory>()
                                 .CreateLogger("Nugget.Unhandled");
    app.MapFallback(
      (HttpContext context) => {
        // Anything a client asks for that is not implemented shows up here.
        fallback_logger.LogWarning(
          "No handler for {Method} {Path}{Query} (User-Agent: {UserAgent})",
          context.Request.Method,
          context.Request.Path,
          context.Request.QueryString,
          context.Request.Headers.UserAgent.ToString()
        );
        return Results.NotFound();
      }
    );

    await app.RunAsync();
  }

  private static void LogConfiguration(WebApplication app) {
    ILogger logger = app.Services
                        .GetRequiredService<ILoggerFactory>()
                        .CreateLogger("Nugget");
    NuggetOptions options = app.Services
                               .GetRequiredService<IOptions<NuggetOptions>>()
                               .Value;
    if (options.GetApiKeys().Count == 0) {
      logger.LogWarning(
        "No API key configured: uploads are disabled. Set Nugget__ApiKey " +
        "(or Nugget__ApiKeys__0, ...) to enable them"
      );
    }

    logger.LogInformation(
      "Public base URL: {Url}",
      string.IsNullOrWhiteSpace(options.PublicBaseUrl)
        ? "taken from each request's host"
        : options.PublicBaseUrl
    );
  }

  private static async Task LogRequestsAsync(HttpContext context,
                                             RequestDelegate next) {
    long start = Stopwatch.GetTimestamp();
    await next(context);
    if (context.Request.Path.StartsWithSegments("/health") ||
        !context.RequestServices
                .GetRequiredService<IOptions<NuggetOptions>>()
                .Value
                .LogRequests) {
      return;
    }

    context.RequestServices
           .GetRequiredService<ILoggerFactory>()
           .CreateLogger("Nugget.Requests")
           .LogInformation(
             "{Method} {Host}{Path}{Query} -> {Status} in {Elapsed:0} ms ({UserAgent})",
             context.Request.Method,
             context.Request.Host.Value,
             context.Request.Path,
             context.Request.QueryString,
             context.Response.StatusCode,
             Stopwatch.GetElapsedTime(start).TotalMilliseconds,
             context.Request.Headers.UserAgent.ToString()
           );
  }
}