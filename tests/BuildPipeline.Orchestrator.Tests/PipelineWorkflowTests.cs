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

public class PipelineWorkflowTests : IAsyncLifetime
{
    private WorkflowEnvironment _env = null!;

    public async Task InitializeAsync()
    {
        _env = await WorkflowEnvironment.StartLocalAsync();
    }

    public async Task DisposeAsync()
    {
        await _env.DisposeAsync();
    }

    private IPipelineActivities CreateMockActivities(
        BuildArtifactResult? androidResult = null,
        BuildArtifactResult? iosResult = null,
        bool failValidation = false,
        bool failBuild = false)
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
            mock.Setup(a => a.ExecutePlatformBuildAsync(It.IsAny<PlatformBuildInput>()))
                .ThrowsAsync(new Exception("Unity build crashed"));
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
}
