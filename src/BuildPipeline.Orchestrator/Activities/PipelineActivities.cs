using System;
using System.Threading.Tasks;
using BuildPipeline.Orchestrator.Config;

namespace BuildPipeline.Orchestrator.Activities;

public sealed class PipelineActivities : IPipelineActivities
{
    private readonly PipelineConfig _config;

    public PipelineActivities(PipelineConfig config)
    {
        _config = config;
    }

    public Task<ProjectMetadata> ValidateUnityProjectAsync(PipelineWorkflowInput input)
    {
        // TODO: Inspect the Unity project at _config.UnityProjectPath, confirm required assets/settings exist,
        // and gather metadata (e.g., Unity version). Consider structured logging here so operators can trace failures.
        throw new NotImplementedException("Implement Unity project validation.");
    }

    public Task<BuildArtifactResult> ExecutePlatformBuildAsync(PlatformBuildInput input)
    {
        // TODO: Invoke the Unity CLI (or your orchestration of choice) to produce a deterministic artifact per platform.
        // Recommended CLI (customise as needed):
        // Unity -quit -batchmode -nographics -projectPath <path> -executeMethod BuildScript.BuildForPlatform -buildPlatform <android|ios> -buildOutput <path>
        // Remember to make the activity idempotent, handle retries, persist artifacts to _config.OutputDirectory,
        // and emit telemetry/logging around execution and timing.

        throw new NotImplementedException("Implement platform build execution.");
    }

    public Task<string> GenerateReportAsync(PipelineRunSummary summary)
    {
        // TODO: Compose a report that captures workflow metadata, artifact paths, timings, and any diagnostics you find useful.
        // Make sure the output location is configurable via _config.OutputDirectory and consider emitting summary telemetry.

        throw new NotImplementedException("Implement pipeline report generation.");
    }
}
