using BuildPipeline.Orchestrator.Activities;
using BuildPipeline.Orchestrator.Config;
using BuildPipeline.Orchestrator.Workflows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Temporalio.Client;
using Temporalio.Common;

var configuration = new ConfigurationBuilder()
    .AddEnvironmentVariables()
    .Build();

using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
var logger = loggerFactory.CreateLogger("BuildPipeline.Client");

var config = PipelineConfig.Load(configuration);

// Parse CLI arguments for platform selection
var platformArg = args.Length > 0 ? args[0].ToLowerInvariant() : "both";
var parameters = new Dictionary<string, string>
{
    ["platforms"] = platformArg
};

var runInput = PipelineWorkflowInput.CreateDefault(parameters: parameters);

try
{
    logger.LogInformation("Connecting to Temporal at {Address}/{Namespace}", config.TemporalAddress, config.TemporalNamespace);
    logger.LogInformation("Building platforms: {Platforms}", platformArg);

    var client = await TemporalClient.ConnectAsync(new TemporalClientConnectOptions
    {
        TargetHost = config.TemporalAddress,
        Namespace = config.TemporalNamespace,
    });

    var workflowId = $"pipeline-{runInput.RunId}";
    var handle = await client.StartWorkflowAsync(
        (PipelineWorkflow wf) => wf.RunAsync(runInput),
        new WorkflowOptions(id: workflowId, taskQueue: config.TaskQueue));

    logger.LogInformation("Started workflow {WorkflowId} with Temporal run {RunId}", handle.Id, handle.ResultRunId);
    logger.LogInformation("Inspect progress in the Temporal UI at http://localhost:8080 once the worker is running.");
}
catch (Exception ex)
{
    logger.LogError(ex, "Failed to start workflow. Ensure Temporal is running and reachable.");
}
