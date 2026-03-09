using System.Threading.Tasks;
using Xunit;

namespace BuildPipeline.Orchestrator.Tests;

public class PipelineWorkflowTests
{
    [Fact(Skip = "TODO: Add workflow execution tests once workflow is implemented.")]
    public Task PipelineWorkflow_Completes_WithExpectedArtefacts()
    {
        // TODO: Provide coverage for a full pipeline run once activities are implemented.
        return Task.CompletedTask;
    }

    [Fact(Skip = "TODO: Add test for platform-specific builds")]
    public Task PipelineWorkflow_BuildsOnlyRequestedPlatform()
    {
        // TODO: Enforce behaviour for per-platform execution when orchestration logic is ready.
        return Task.CompletedTask;
    }
}
