using BuildPipeline.Orchestrator.Config;
using BuildPipeline.Orchestrator.Activities;
using BuildPipeline.Orchestrator.Workflows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BuildPipeline.Orchestrator;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var host = Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration(config =>
            {
                config.AddEnvironmentVariables();
            })
            .ConfigureServices((context, services) =>
            {
                services.AddSingleton(sp => PipelineConfig.Load(sp.GetRequiredService<IConfiguration>()));
                services.AddSingleton<IPipelineActivities, PipelineActivities>();

                // TODO: Wire up your preferred logging/telemetry providers here (e.g., structured logs, metrics exporters).
                services.AddHostedService<TemporalWorkerHost>();
            })
            .Build();

        await host.RunAsync();
    }
}
