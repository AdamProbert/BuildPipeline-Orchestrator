using BuildPipeline.Orchestrator.Activities;
using BuildPipeline.Orchestrator.Config;
using BuildPipeline.Orchestrator.Infrastructure;
using BuildPipeline.Orchestrator.Workflows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Temporalio.Client;
using Temporalio.Extensions.OpenTelemetry;

var configuration = new ConfigurationBuilder()
    .AddEnvironmentVariables()
    .Build();

using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
var logger = loggerFactory.CreateLogger("BuildPipeline.Client");

var config = PipelineConfig.Load(configuration);

// Set up tracing (only when OTLP endpoint is configured)
var otlpEndpoint = configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
TracerProvider? tracerProvider = null;
if (otlpEndpoint != null)
{
    tracerProvider = Sdk.CreateTracerProviderBuilder()
        .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("build-pipeline-client"))
        .AddSource(Telemetry.ServiceName)
        .AddSource("Temporalio")
        .AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint))
        .Build();
}
else
{
    logger.LogWarning("OTEL_EXPORTER_OTLP_ENDPOINT not set — tracing/metrics disabled. Set it to enable observability (e.g. http://localhost:4317).");
}

// Parse CLI arguments: [platform] [--wait]
var positionalArgs = args.Where(a => !a.StartsWith("--")).ToArray();
var waitForResult = args.Contains("--wait", StringComparer.OrdinalIgnoreCase);

var platformArg = positionalArgs.Length > 0 ? positionalArgs[0].ToLowerInvariant() : "";
var parameters = new Dictionary<string, string>();
if (!string.IsNullOrEmpty(platformArg))
    parameters["platforms"] = platformArg;

var runInput = PipelineWorkflowInput.CreateDefault(parameters: parameters);

try
{
    logger.LogInformation("Connecting to Temporal at {Address}/{Namespace}", config.TemporalAddress, config.TemporalNamespace);
    logger.LogInformation("Building platforms: {Platforms}", string.IsNullOrEmpty(platformArg) ? "all" : platformArg);

    var client = await TemporalClient.ConnectAsync(new TemporalClientConnectOptions
    {
        TargetHost = config.TemporalAddress,
        Namespace = config.TemporalNamespace,
        Interceptors = [new TracingInterceptor()],
    });

    var workflowId = $"pipeline-{runInput.RunId}";
    var handle = await client.StartWorkflowAsync(
        (PipelineWorkflow wf) => wf.RunAsync(runInput),
        new WorkflowOptions(id: workflowId, taskQueue: config.TaskQueue));

    logger.LogInformation("Started workflow {WorkflowId}", handle.Id);

    if (waitForResult)
    {
        logger.LogInformation("Waiting for workflow to complete...");
        var summary = await handle.GetResultAsync();
        logger.LogInformation("Workflow completed. Run: {RunId}, Builds: {BuildCount}, Report: {ReportPath}",
            summary.RunId, summary.BuildResults.Count, summary.ReportPath);
    }
    else
    {
        logger.LogInformation("Workflow submitted (fire-and-forget). Use --wait to block until completion.");
        logger.LogInformation("Inspect progress in the Temporal UI at http://localhost:8080");
    }
}
catch (Exception ex)
{
    logger.LogError(ex, "Workflow failed.");
    return 1;
}
finally
{
    tracerProvider?.Dispose();
}

return 0;
