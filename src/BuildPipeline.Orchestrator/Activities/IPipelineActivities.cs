using Temporalio.Activities;

namespace BuildPipeline.Orchestrator.Activities;

public interface IPipelineActivities
{
    [Activity]
    Task<ProjectMetadata> ValidateUnityProjectAsync(PipelineWorkflowInput input);

    [Activity]
    Task<BuildArtifactResult> ExecutePlatformBuildAsync(PlatformBuildInput input);

    [Activity]
    Task<string> GenerateReportAsync(PipelineRunSummary summary);

    [Activity]
    Task<string> PrepareProjectCopyAsync(PrepareProjectCopyInput input);

    [Activity]
    Task CleanupProjectCopyAsync(string projectCopyPath);
}
