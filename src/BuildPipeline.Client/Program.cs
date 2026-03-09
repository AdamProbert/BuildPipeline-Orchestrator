using BuildPipeline.Orchestrator.Activities;
using BuildPipeline.Orchestrator.Config;
using BuildPipeline.Orchestrator.Workflows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Temporalio.Client;

var configuration = new ConfigurationBuilder()
    .AddEnvironmentVariables()
    .Build();

using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
var logger = loggerFactory.CreateLogger("BuildPipeline.Client");

var config = PipelineConfig.Load(configuration);

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

return 0;
