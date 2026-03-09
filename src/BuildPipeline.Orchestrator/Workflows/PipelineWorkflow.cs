using System;
using BuildPipeline.Orchestrator.Activities;
using Temporalio.Common;
using Temporalio.Workflows;

namespace BuildPipeline.Orchestrator.Workflows;

[Workflow]
public class PipelineWorkflow
{
    private static readonly ActivityOptions DefaultActivityOptions = new()
    {
        // TODO: Tune activity timeouts and retry policies to match the behaviour you expect from the Unity build steps.
        StartToCloseTimeout = TimeSpan.FromSeconds(30),
        RetryPolicy = new RetryPolicy
        {
            MaximumAttempts = 3,
        },
    };

    [WorkflowRun]
    public Task<PipelineRunSummary> RunAsync(PipelineWorkflowInput input)
    {
        // TODO: Implement the orchestration logic:
        // 1. Validate the Unity project with a deterministic run ID.
        // 2. Decide which platforms to target (e.g., based on input.Parameters["platforms"]).
        // 3. Trigger platform-specific build activities, potentially in parallel, handling retries and failures.
        // 4. Aggregate activity results into a PipelineRunSummary and generate a final report artifact.
        // 5. Return the completed summary with relevant timestamps and artifact locations.

        throw new NotImplementedException("Implement pipeline workflow orchestration.");
    }
}
