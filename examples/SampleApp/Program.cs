using Humanizer;
using Newtonsoft.Json;
using Polly;
using Serilog;
using Serilog.Core;

namespace SampleApp;

/// <summary>Uses a few packages restored from the Nugget dev server.</summary>
public static class Program {
  public static void Main() {
    using Logger logger = new LoggerConfiguration()
                          .WriteTo.Console()
                          .CreateLogger();
    ResiliencePipeline pipeline = new ResiliencePipelineBuilder()
                                  .AddTimeout(TimeSpan.FromSeconds(5))
                                  .Build();
    string json = pipeline.Execute(
      () => JsonConvert.SerializeObject(
        new {
          Source = "Nugget",
          Uptime = TimeSpan.FromMinutes(90).Humanize()
        }
      )
    );
    logger.Information("Packages restored from Nugget work: {Json}", json);
  }
}
