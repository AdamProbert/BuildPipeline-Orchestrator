using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BuildPipeline.Orchestrator.Activities;
using BuildPipeline.Orchestrator.Workflows;
using Moq;
using Temporalio.Client;
using Temporalio.Testing;
using Temporalio.Worker;
using Xunit;

namespace BuildPipeline.Orchestrator.Tests;

public class TemporalFixture : IAsyncLifetime
{
    public WorkflowEnvironment Env { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Env = await WorkflowEnvironment.StartLocalAsync();
    }

    public async Task DisposeAsync()
    {
        await Env.DisposeAsync();
    }
}

public class PipelineWorkflowTests : IClassFixture<TemporalFixture>
{
    private readonly WorkflowEnvironment _env;

    public PipelineWorkflowTests(TemporalFixture fixture)
    {
        _env = fixture.Env;
    }

    private IPipelineActivities CreateMockActivities(
        BuildArtifactResult? androidResult = null,
        BuildArtifactResult? iosResult = null,
        bool failValidation = false,
        bool failBuild = false,
        BuildPlatform? failPlatform = null)
    {
        var mock = new Mock<IPipelineActivities>();

        if (failValidation)
        {
            mock.Setup(a => a.ValidateUnityProjectAsync(It.IsAny<PipelineWorkflowInput>()))
                .ThrowsAsync(new InvalidOperationException("Project not found"));
        }
        else
        {
            mock.Setup(a => a.ValidateUnityProjectAsync(It.IsAny<PipelineWorkflowInput>()))
                .ReturnsAsync(new ProjectMetadata("/fake/project", "6000.2.7f2", DateTimeOffset.UtcNow));
        }

        if (failBuild)
        {
            if (failPlatform.HasValue)
            {
                // Fail only the specified platform, succeed on the other
                mock.Setup(a => a.ExecutePlatformBuildAsync(It.Is<PlatformBuildInput>(p => p.Platform == failPlatform.Value)))
                    .ThrowsAsync(new Exception($"Unity {failPlatform.Value} build crashed"));

                var succeedPlatform = failPlatform.Value == BuildPlatform.Android ? BuildPlatform.iOS : BuildPlatform.Android;
                var succeedExt = succeedPlatform == BuildPlatform.Android ? ".apk" : "";
                mock.Setup(a => a.ExecutePlatformBuildAsync(It.Is<PlatformBuildInput>(p => p.Platform == succeedPlatform)))
                    .ReturnsAsync(new BuildArtifactResult(succeedPlatform, $"/output/build{succeedExt}", DateTimeOffset.UtcNow));
            }
            else
            {
                mock.Setup(a => a.ExecutePlatformBuildAsync(It.IsAny<PlatformBuildInput>()))
                    .ThrowsAsync(new Exception("Unity build crashed"));
            }
        }
        else
        {
            mock.Setup(a => a.ExecutePlatformBuildAsync(It.Is<PlatformBuildInput>(p => p.Platform == BuildPlatform.Android)))
                .ReturnsAsync(androidResult ?? new BuildArtifactResult(
                    BuildPlatform.Android, "/output/build.apk", DateTimeOffset.UtcNow));

            mock.Setup(a => a.ExecutePlatformBuildAsync(It.Is<PlatformBuildInput>(p => p.Platform == BuildPlatform.iOS)))
                .ReturnsAsync(iosResult ?? new BuildArtifactResult(
                    BuildPlatform.iOS, "/output/build-ios", DateTimeOffset.UtcNow));
        }

        mock.Setup(a => a.GenerateReportAsync(It.IsAny<PipelineRunSummary>()))
            .ReturnsAsync("/output/report.json");

        mock.Setup(a => a.PrepareProjectCopyAsync(It.IsAny<PrepareProjectCopyInput>()))
            .ReturnsAsync((PrepareProjectCopyInput input) =>
                $"/tmp/unity-builds/{input.RunId}-{input.Platform.ToString().ToLowerInvariant()}");

        mock.Setup(a => a.CleanupProjectCopyAsync(It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        return mock.Object;
    }

    private async Task<PipelineRunSummary> RunWorkflowAsync(PipelineWorkflowInput input, IPipelineActivities activities)
    {
        var taskQueue = $"test-{Guid.NewGuid()}";
        using var worker = new TemporalWorker(
            _env.Client,
            new TemporalWorkerOptions(taskQueue)
                .AddWorkflow<PipelineWorkflow>()
                .AddAllActivities(activities));

        return await worker.ExecuteAsync(() =>
            _env.Client.ExecuteWorkflowAsync(
                (PipelineWorkflow wf) => wf.RunAsync(input),
                new(id: $"test-{Guid.NewGuid()}", taskQueue: taskQueue)));
    }

    [Fact]
    public async Task PipelineWorkflow_AndroidOnly_BuildsAndReports()
    {
        var input = new PipelineWorkflowInput("run-1", new Dictionary<string, string> { ["platforms"] = "android" });
        var activities = CreateMockActivities();

        var result = await RunWorkflowAsync(input, activities);

        Assert.Equal("run-1", result.RunId);
        Assert.Single(result.BuildResults);
        Assert.Equal(BuildPlatform.Android, result.BuildResults[0].Platform);
        Assert.Equal("/output/report.json", result.ReportPath);
        Assert.Equal("6000.2.7f2", result.ProjectMetadata.ProjectVersion);
    }

    [Fact]
    public async Task PipelineWorkflow_iOSOnly_BuildsAndReports()
    {
        var input = new PipelineWorkflowInput("run-2", new Dictionary<string, string> { ["platforms"] = "ios" });
        var activities = CreateMockActivities();

        var result = await RunWorkflowAsync(input, activities);

        Assert.Equal("run-2", result.RunId);
        Assert.Single(result.BuildResults);
        Assert.Equal(BuildPlatform.iOS, result.BuildResults[0].Platform);
        Assert.Equal("/output/report.json", result.ReportPath);
    }

    [Fact]
    public async Task PipelineWorkflow_MultiplePlatforms_BuildsAllAndReports()
    {
        var input = new PipelineWorkflowInput("run-3", new Dictionary<string, string> { ["platforms"] = "android,ios" });
        var activities = CreateMockActivities();

        var result = await RunWorkflowAsync(input, activities);

        Assert.Equal(2, result.BuildResults.Count);
        Assert.Contains(result.BuildResults, r => r.Platform == BuildPlatform.Android);
        Assert.Contains(result.BuildResults, r => r.Platform == BuildPlatform.iOS);
        Assert.Equal("/output/report.json", result.ReportPath);
    }

    [Fact]
    public async Task PipelineWorkflow_NoPlatformParam_BuildsAll()
    {
        var input = new PipelineWorkflowInput("run-4");
        var activities = CreateMockActivities();

        var result = await RunWorkflowAsync(input, activities);

        Assert.Equal(2, result.BuildResults.Count);
        Assert.Contains(result.BuildResults, r => r.Platform == BuildPlatform.Android);
        Assert.Contains(result.BuildResults, r => r.Platform == BuildPlatform.iOS);
    }

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

        // The inner cause should be an ActivityFailureException wrapping the build error
        Assert.IsType<Temporalio.Exceptions.ActivityFailureException>(ex.InnerException);
    }

    [Fact]
    public async Task PipelineWorkflow_PartialPlatformFailure_FailsEntireWorkflow()
    {
        // iOS fails, Android would succeed — but parallel fan-out means the workflow fails
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

        // Verify cleanup was called even though the build failed (finally semantics)
        mock.Verify(a => a.CleanupProjectCopyAsync(It.IsAny<string>()), Times.Once);
    }

}
