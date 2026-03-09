using BuildPipeline.Orchestrator.Activities;
using BuildPipeline.Orchestrator.Config;
using BuildPipeline.Orchestrator.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

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
            .ConfigureLogging((context, logging) =>
            {
                var otlpEndpoint = context.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
                if (otlpEndpoint != null)
                {
                    logging.AddOpenTelemetry(otel =>
                    {
                        otel.SetResourceBuilder(ResourceBuilder.CreateDefault()
                            .AddService(Telemetry.ServiceName));
                        otel.IncludeScopes = true;
                        otel.IncludeFormattedMessage = true;
                        otel.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
                    });
                }
            })
            .ConfigureServices((context, services) =>
            {
                var config = PipelineConfig.Load(context.Configuration);
                services.AddSingleton(config);

                if (config.SimulateBuild)
                    services.AddSingleton<IPipelineActivities, SimulatedPipelineActivities>();
                else
                    services.AddSingleton<IPipelineActivities, PipelineActivities>();

                var otelResource = ResourceBuilder.CreateDefault()
                    .AddService(Telemetry.ServiceName);

                if (config.OtlpEndpoint != null)
                {
                    services.AddOpenTelemetry()
                        .WithTracing(tracing =>
                        {
                            tracing
                                .SetResourceBuilder(otelResource)
                                .AddSource(Telemetry.ServiceName)
                                .AddSource("Temporalio.Extensions.OpenTelemetry.Client")
                                .AddSource("Temporalio.Extensions.OpenTelemetry.Workflow")
                                .AddSource("Temporalio.Extensions.OpenTelemetry.Activity")
                                .AddOtlpExporter(o => o.Endpoint = new Uri(config.OtlpEndpoint));
                        })
                        .WithMetrics(metrics =>
                        {
                            metrics
                                .SetResourceBuilder(otelResource)
                                .AddMeter(Telemetry.ServiceName)
                                .AddRuntimeInstrumentation()
                                .AddOtlpExporter(o => o.Endpoint = new Uri(config.OtlpEndpoint));
                        });
                }

                services.AddHostedService<TemporalWorkerHost>();
            })
            .Build();

        if (host.Services.GetRequiredService<PipelineConfig>().OtlpEndpoint == null)
        {
            host.Services.GetRequiredService<ILoggerFactory>()
                .CreateLogger("BuildPipeline.Orchestrator")
                .LogWarning("OTEL_EXPORTER_OTLP_ENDPOINT not set \u2014 tracing/metrics disabled. Set it to enable observability (e.g. http://localhost:4317).");
        }

        await host.RunAsync();
    }
}
