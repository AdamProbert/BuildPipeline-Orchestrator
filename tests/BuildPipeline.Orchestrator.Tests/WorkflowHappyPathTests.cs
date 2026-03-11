using System.Collections.Generic;
using System.Threading.Tasks;
using BuildPipeline.Orchestrator.Activities;
using Xunit;

namespace BuildPipeline.Orchestrator.Tests;

public class WorkflowHappyPathTests : WorkflowTestBase, IClassFixture<TemporalFixture>
{
    public WorkflowHappyPathTests(TemporalFixture fixture) : base(fixture) { }

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
}
