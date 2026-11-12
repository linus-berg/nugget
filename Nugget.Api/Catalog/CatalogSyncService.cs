using Microsoft.Extensions.Options;
using Nugget.Api.Configuration;
using Polly;
using Polly.Retry;

namespace Nugget.Api.Catalog;

/// <summary>Loads the catalog at startup, then keeps it in sync.</summary>
public sealed class CatalogSyncService(
  CatalogSynchronizer synchronizer,
  IOptions<NuggetOptions> options,
  ILogger<CatalogSyncService> logger
)
  : BackgroundService {
  protected override async Task ExecuteAsync(CancellationToken stopping) {
    // Keep serving what works without the catalog and retry until S3 is
    // reachable (it may simply not be up yet).
    ResiliencePipeline startup = new ResiliencePipelineBuilder()
                                 .AddRetry(
                                   new RetryStrategyOptions {
                                     ShouldHandle = new PredicateBuilder()
                                       .Handle<Exception>(
                                         _ => !stopping.IsCancellationRequested
                                       ),
                                     MaxRetryAttempts = int.MaxValue,
                                     BackoffType = DelayBackoffType.Exponential,
                                     UseJitter = true,
                                     Delay = TimeSpan.FromSeconds(2),
                                     MaxDelay = TimeSpan.FromSeconds(30),
                                     OnRetry = args => {
                                       logger.LogError(
                                         args.Outcome.Exception,
                                         "Could not load the catalog; " +
                                         "retrying in {Delay}",
                                         args.RetryDelay
                                       );
                                       return ValueTask.CompletedTask;
                                     }
                                   }
                                 )
                                 .Build();
    try {
      await startup.ExecuteAsync(
        async token => await synchronizer.InitializeAsync(token),
        stopping
      );
    } catch (OperationCanceledException) when (
      stopping.IsCancellationRequested) {
      return;
    }

    SyncOptions sync = options.Value.Sync;
    if (sync.PollIntervalSeconds <= 0) {
      return;
    }

    TimeSpan poll = TimeSpan.FromSeconds(sync.PollIntervalSeconds);
    TimeSpan flush =
      TimeSpan.FromSeconds(Math.Max(1, sync.DownloadFlushSeconds));
    DateTimeOffset next_flush = DateTimeOffset.UtcNow + flush;
    while (!stopping.IsCancellationRequested) {
      try {
        await Task.Delay(poll, stopping);
        await synchronizer.PollAsync(stopping);
        if (DateTimeOffset.UtcNow >= next_flush) {
          next_flush = DateTimeOffset.UtcNow + flush;
          await synchronizer.FlushDownloadsAsync(stopping);
        }

        await synchronizer.MaybeWriteSnapshotAsync(stopping);
      } catch (OperationCanceledException) when (stopping
                                                   .IsCancellationRequested) {
        break;
      } catch (Exception ex) {
        logger.LogWarning(ex, "Catalog synchronization failed; will retry");
      }
    }
  }

  public override async Task StopAsync(CancellationToken ct) {
    await base.StopAsync(ct);
    try {
      await synchronizer.FlushDownloadsAsync(ct);
    } catch (Exception ex) {
      logger.LogWarning(ex, "Could not save download counts on shutdown");
    }
  }
}