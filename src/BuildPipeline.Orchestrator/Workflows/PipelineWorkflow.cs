using BuildPipeline.Orchestrator.Activities;
using Microsoft.Extensions.Logging;
using Temporalio.Common;
using Temporalio.Workflows;

namespace BuildPipeline.Orchestrator.Workflows;

[Workflow]
public class PipelineWorkflow
{
    private static ActivityOptions GetValidationOptions(TimeoutConfig timeouts) => new()
    {
        StartToCloseTimeout = timeouts.ValidationTimeout ?? TimeoutConfig.Default.ValidationTimeout!.Value,
        RetryPolicy = new RetryPolicy
        {
            MaximumAttempts = 1,
            NonRetryableErrorTypes = new[] { nameof(InvalidOperationException) },
        },
    };

    private static ActivityOptions GetBuildOptions(TimeoutConfig timeouts) => new()
    {
        StartToCloseTimeout = timeouts.BuildTimeout ?? TimeoutConfig.Default.BuildTimeout!.Value,
        RetryPolicy = new RetryPolicy
        {
            MaximumAttempts = 3,
            InitialInterval = TimeSpan.FromSeconds(5),
            BackoffCoefficient = 2,
            NonRetryableErrorTypes = new[] { nameof(InvalidOperationException) },
        },
    };

    private static ActivityOptions GetReportOptions(TimeoutConfig timeouts) => new()
    {
        StartToCloseTimeout = timeouts.ReportTimeout ?? TimeoutConfig.Default.ReportTimeout!.Value,
        RetryPolicy = new RetryPolicy
        {
            MaximumAttempts = 2,
        },
    };

    [WorkflowRun]
    public async Task<PipelineRunSummary> RunAsync(PipelineWorkflowInput input)
    {
        Workflow.Logger.LogInformation("Starting pipeline workflow for run {RunId}", input.RunId);

        var timeouts = input.Timeouts ?? TimeoutConfig.Default;

        // 1. Validate the Unity project
        var metadata = await Workflow.ExecuteActivityAsync(
            (IPipelineActivities act) => act.ValidateUnityProjectAsync(input),
            GetValidationOptions(timeouts));

        Workflow.Logger.LogInformation("Validation passed: Unity {Version}", metadata.ProjectVersion);

        // 2. Determine target platforms
        var platforms = ParsePlatforms(input);

        // 3. Execute builds (parallel if multiple platforms)
        var buildResults = new List<BuildArtifactResult>();
        var buildTasks = new List<Task<BuildArtifactResult>>();

        foreach (var platform in platforms)
        {
            buildTasks.Add(Workflow.ExecuteActivityAsync(
                (IPipelineActivities act) => act.ExecutePlatformBuildAsync(
                    new PlatformBuildInput(input.RunId, platform, timeouts)),
                GetBuildOptions(timeouts)));
        }

        var results = await Task.WhenAll(buildTasks);
        buildResults.AddRange(results);

        Workflow.Logger.LogInformation("Builds completed: {Platforms}",
            string.Join(", ", buildResults.Select(r => r.Platform)));

        // 4. Generate report
        var preliminarySummary = new PipelineRunSummary(
            input.RunId, metadata, buildResults,
            ReportPath: "", CompletedAtUtc: Workflow.UtcNow);

        var reportPath = await Workflow.ExecuteActivityAsync(
            (IPipelineActivities act) => act.GenerateReportAsync(preliminarySummary),
            GetReportOptions(timeouts));

        // 5. Return final summary with report path
        var summary = preliminarySummary with { ReportPath = reportPath };

        Workflow.Logger.LogInformation("Pipeline complete for run {RunId}. Report: {ReportPath}",
            input.RunId, reportPath);

        return summary;
    }

    private static List<BuildPlatform> ParsePlatforms(PipelineWorkflowInput input)
    {
        string? value = null;
        input.Parameters?.TryGetValue("platforms", out value);
        return PlatformRegistry.Parse(value);
    }
}
