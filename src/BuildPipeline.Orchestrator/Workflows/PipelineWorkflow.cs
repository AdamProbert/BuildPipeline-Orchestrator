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

    private static ActivityOptions GetCloneOptions(TimeoutConfig timeouts) => new()
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(5),
        RetryPolicy = new RetryPolicy
        {
            MaximumAttempts = 2,
            NonRetryableErrorTypes = new[] { nameof(InvalidOperationException) },
        },
    };

    private static ActivityOptions GetCleanupOptions() => new()
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(2),
        RetryPolicy = new RetryPolicy { MaximumAttempts = 2 },
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

        // 3. Clone project & build per platform (each gets an isolated copy for concurrency)
        var buildTasks = new List<Task<BuildArtifactResult>>();

        foreach (var platform in platforms)
        {
            buildTasks.Add(BuildWithIsolatedProjectAsync(input.RunId, platform, timeouts));
        }

        var results = await Task.WhenAll(buildTasks);
        var buildResults = new List<BuildArtifactResult>(results);

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

    private static async Task<BuildArtifactResult> BuildWithIsolatedProjectAsync(
        string runId, BuildPlatform platform, TimeoutConfig timeouts)
    {
        var clonedPath = await Workflow.ExecuteActivityAsync(
            (IPipelineActivities act) => act.PrepareProjectCopyAsync(
                new PrepareProjectCopyInput(runId, platform)),
            GetCloneOptions(timeouts));

        try
        {
            return await Workflow.ExecuteActivityAsync(
                (IPipelineActivities act) => act.ExecutePlatformBuildAsync(
                    new PlatformBuildInput(runId, platform, timeouts, clonedPath)),
                GetBuildOptions(timeouts));
        }
        finally
        {
            await Workflow.ExecuteActivityAsync(
                (IPipelineActivities act) => act.CleanupProjectCopyAsync(clonedPath),
                GetCleanupOptions());
        }
    }

    private static List<BuildPlatform> ParsePlatforms(PipelineWorkflowInput input)
    {
        string? value = null;
        input.Parameters?.TryGetValue("platforms", out value);
        return PlatformRegistry.Parse(value);
    }
}
