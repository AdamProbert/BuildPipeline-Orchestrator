using System;
using System.Threading.Tasks;
using BuildPipeline.Orchestrator.Activities;
using BuildPipeline.Orchestrator.Workflows;
using Moq;
using Temporalio.Client;
using Temporalio.Testing;
using Temporalio.Worker;

namespace BuildPipeline.Orchestrator.Tests;

public abstract class WorkflowTestBase
{
    protected readonly WorkflowEnvironment Env;

    protected WorkflowTestBase(TemporalFixture fixture)
    {
        Env = fixture.Env;
    }

    protected IPipelineActivities CreateMockActivities(
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
                mock.Setup(a => a.ExecutePlatformBuildAsync(It.Is<PlatformBuildInput>(p => p.Platform == failPlatform.Value)))
                    .ThrowsAsync(new Exception($"Unity {failPlatform.Value} build crashed"));

                var succeedPlatform = failPlatform.Value == BuildPlatform.Android ? BuildPlatform.iOS : BuildPlatform.Android;
                var succeedExt = succeedPlatform == BuildPlatform.Android ? ".apk" : "";
                mock.Setup(a => a.ExecutePlatformBuildAsync(It.Is<PlatformBuildInput>(p => p.Platform == succeedPlatform)))
                    .ReturnsAsync(new BuildArtifactResult(succeedPlatform, $"/output/build{succeedExt}", DateTimeOffset.UtcNow, Array.Empty<PipelineIssue>()));
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
                    BuildPlatform.Android, "/output/build.apk", DateTimeOffset.UtcNow, Array.Empty<PipelineIssue>()));

            mock.Setup(a => a.ExecutePlatformBuildAsync(It.Is<PlatformBuildInput>(p => p.Platform == BuildPlatform.iOS)))
                .ReturnsAsync(iosResult ?? new BuildArtifactResult(
                    BuildPlatform.iOS, "/output/build-ios", DateTimeOffset.UtcNow, Array.Empty<PipelineIssue>()));
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

    protected async Task<PipelineRunSummary> RunWorkflowAsync(PipelineWorkflowInput input, IPipelineActivities activities)
    {
        var taskQueue = $"test-{Guid.NewGuid()}";
        using var worker = new TemporalWorker(
            Env.Client,
            new TemporalWorkerOptions(taskQueue)
                .AddWorkflow<PipelineWorkflow>()
                .AddAllActivities(activities));

        return await worker.ExecuteAsync(() =>
            Env.Client.ExecuteWorkflowAsync(
                (PipelineWorkflow wf) => wf.RunAsync(input),
                new(id: $"test-{Guid.NewGuid()}", taskQueue: taskQueue)));
    }
}
