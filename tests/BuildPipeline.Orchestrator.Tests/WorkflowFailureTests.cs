using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BuildPipeline.Orchestrator.Activities;
using Moq;
using Xunit;

namespace BuildPipeline.Orchestrator.Tests;

public class WorkflowFailureTests : WorkflowTestBase, IClassFixture<TemporalFixture>
{
    public WorkflowFailureTests(TemporalFixture fixture) : base(fixture) { }

    [Fact]
    public async Task PipelineWorkflow_ValidationFails_ThrowsActivityFailure()
    {
        var input = new PipelineWorkflowInput("run-fail");
        var activities = CreateMockActivities(failValidation: true);

        await Assert.ThrowsAsync<Temporalio.Exceptions.WorkflowFailedException>(
            () => RunWorkflowAsync(input, activities));
    }

    [Fact]
    public async Task PipelineWorkflow_BuildFails_ThrowsWorkflowFailedException()
    {
        var timeouts = new TimeoutConfig(BuildRetryInterval: TimeSpan.FromMilliseconds(1));
        var input = new PipelineWorkflowInput("run-build-fail",
            new Dictionary<string, string> { ["platforms"] = "android" }, timeouts);
        var activities = CreateMockActivities(failBuild: true);

        var ex = await Assert.ThrowsAsync<Temporalio.Exceptions.WorkflowFailedException>(
            () => RunWorkflowAsync(input, activities));

        Assert.IsType<Temporalio.Exceptions.ActivityFailureException>(ex.InnerException);
    }

    [Fact]
    public async Task PipelineWorkflow_PartialPlatformFailure_FailsEntireWorkflow()
    {
        var timeouts = new TimeoutConfig(BuildRetryInterval: TimeSpan.FromMilliseconds(1));
        var input = new PipelineWorkflowInput("run-partial-fail",
            new Dictionary<string, string> { ["platforms"] = "android,ios" }, timeouts);
        var activities = CreateMockActivities(failBuild: true, failPlatform: BuildPlatform.iOS);

        await Assert.ThrowsAsync<Temporalio.Exceptions.WorkflowFailedException>(
            () => RunWorkflowAsync(input, activities));
    }

    [Fact]
    public async Task PipelineWorkflow_BuildFails_CleanupStillRuns()
    {
        var mock = new Mock<IPipelineActivities>();

        mock.Setup(a => a.ValidateUnityProjectAsync(It.IsAny<PipelineWorkflowInput>()))
            .ReturnsAsync(new ProjectMetadata("/fake/project", "6000.2.7f2", DateTimeOffset.UtcNow));

        mock.Setup(a => a.PrepareProjectCopyAsync(It.IsAny<PrepareProjectCopyInput>()))
            .ReturnsAsync((PrepareProjectCopyInput input) =>
                $"/tmp/unity-builds/{input.RunId}-{input.Platform.ToString().ToLowerInvariant()}");

        mock.Setup(a => a.ExecutePlatformBuildAsync(It.IsAny<PlatformBuildInput>()))
            .ThrowsAsync(new Exception("Build crashed"));

        mock.Setup(a => a.CleanupProjectCopyAsync(It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        var timeouts = new TimeoutConfig(BuildRetryInterval: TimeSpan.FromMilliseconds(1));
        var input = new PipelineWorkflowInput("run-cleanup-verify",
            new Dictionary<string, string> { ["platforms"] = "android" }, timeouts);

        await Assert.ThrowsAsync<Temporalio.Exceptions.WorkflowFailedException>(
            () => RunWorkflowAsync(input, mock.Object));

        mock.Verify(a => a.CleanupProjectCopyAsync(It.IsAny<string>()), Times.Once);
    }
}
