using BuildPipeline.Orchestrator.Activities;
using BuildPipeline.Orchestrator.Config;
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
                var config = PipelineConfig.Load(context.Configuration);
                services.AddSingleton(config);

                if (config.SimulateBuild)
                    services.AddSingleton<IPipelineActivities, SimulatedPipelineActivities>();
                else
                    services.AddSingleton<IPipelineActivities, PipelineActivities>();

                services.AddHostedService<TemporalWorkerHost>();
            })
            .Build();

        await host.RunAsync();
    }
}
