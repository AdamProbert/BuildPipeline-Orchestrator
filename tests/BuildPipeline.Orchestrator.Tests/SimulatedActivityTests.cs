using System;
using System.IO;
using System.Threading.Tasks;
using BuildPipeline.Orchestrator.Activities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BuildPipeline.Orchestrator.Tests;

public class SimulatedActivityTests : PipelineActivityTestBase
{
    #region ValidateUnityProjectAsync

    [Fact]
    public async Task SimulatedValidate_ValidProject_ReturnsMetadata()
    {
        var projectDir = CreateFakeUnityProject("6000.2.7f2");
        var sut = new SimulatedPipelineActivities(
            ConfigFor(projectDir),
            NullLogger<SimulatedPipelineActivities>.Instance);

        var result = await sut.ValidateUnityProjectAsync(new PipelineWorkflowInput("sim-run"));

        Assert.Equal("6000.2.7f2", result.ProjectVersion);
    }

    [Fact]
    public async Task SimulatedValidate_MissingVersionFile_UsesPlaceholder()
    {
        var projectDir = Path.Combine(TempDir, "sim-no-version");
        Directory.CreateDirectory(projectDir);
        var sut = new SimulatedPipelineActivities(
            ConfigFor(projectDir),
            NullLogger<SimulatedPipelineActivities>.Instance);

        var result = await sut.ValidateUnityProjectAsync(new PipelineWorkflowInput("sim-run"));

        Assert.Equal("simulated-6000.0.0f1", result.ProjectVersion);
    }

    [Fact]
    public async Task SimulatedValidate_MissingProjectDir_Throws()
    {
        var sut = new SimulatedPipelineActivities(
            ConfigFor(Path.Combine(TempDir, "nope")),
            NullLogger<SimulatedPipelineActivities>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.ValidateUnityProjectAsync(new PipelineWorkflowInput("run")));
    }

    #endregion

    #region ExecutePlatformBuildAsync

    [Theory]
    [InlineData(BuildPlatform.Android, ".apk")]
    [InlineData(BuildPlatform.iOS, "")]
    public async Task SimulatedBuild_ProducesArtifact(BuildPlatform platform, string expectedExtension)
    {
        var projectDir = CreateFakeUnityProject();
        var sut = new SimulatedPipelineActivities(
            ConfigFor(projectDir),
            NullLogger<SimulatedPipelineActivities>.Instance);
        var input = new PlatformBuildInput("build-run-1", platform);

        var result = await sut.ExecutePlatformBuildAsync(input);

        Assert.Equal(platform, result.Platform);
        Assert.True(File.Exists(result.ArtifactPath), $"Artifact should exist at {result.ArtifactPath}");
        Assert.EndsWith(expectedExtension, result.ArtifactPath);
        Assert.True(result.CompletedAtUtc <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task SimulatedBuild_IsIdempotent()
    {
        var projectDir = CreateFakeUnityProject();
        var sut = new SimulatedPipelineActivities(
            ConfigFor(projectDir),
            NullLogger<SimulatedPipelineActivities>.Instance);
        var input = new PlatformBuildInput("idempotent-run", BuildPlatform.Android);

        var result1 = await sut.ExecutePlatformBuildAsync(input);
        var result2 = await sut.ExecutePlatformBuildAsync(input);

        Assert.Equal(result1.ArtifactPath, result2.ArtifactPath);
        Assert.True(File.Exists(result2.ArtifactPath));
    }

    #endregion

    #region GenerateReportAsync

    [Fact]
    public async Task SimulatedReport_WritesJsonFile()
    {
        var projectDir = CreateFakeUnityProject();
        var sut = new SimulatedPipelineActivities(
            ConfigFor(projectDir),
            NullLogger<SimulatedPipelineActivities>.Instance);

        var summary = new PipelineRunSummary(
            RunId: "report-run-1",
            ProjectMetadata: new ProjectMetadata(projectDir, "6000.2.7f2", DateTimeOffset.UtcNow),
            BuildResults: new[] { new BuildArtifactResult(BuildPlatform.Android, "/fake/path.apk", DateTimeOffset.UtcNow, Array.Empty<PipelineIssue>()) },
            Issues: Array.Empty<PipelineIssue>(),
            ReportPath: "",
            CompletedAtUtc: DateTimeOffset.UtcNow);

        var reportPath = await sut.GenerateReportAsync(summary);

        Assert.True(File.Exists(reportPath));
        var content = await File.ReadAllTextAsync(reportPath);
        Assert.Contains("report-run-1", content);
        Assert.Contains("Android", content);
    }

    [Fact]
    public async Task SimulatedReport_IsIdempotent()
    {
        var projectDir = CreateFakeUnityProject();
        var sut = new SimulatedPipelineActivities(
            ConfigFor(projectDir),
            NullLogger<SimulatedPipelineActivities>.Instance);

        var summary = new PipelineRunSummary(
            RunId: "report-idem",
            ProjectMetadata: new ProjectMetadata(projectDir, "6000.2.7f2", DateTimeOffset.UtcNow),
            BuildResults: Array.Empty<BuildArtifactResult>(),
            Issues: Array.Empty<PipelineIssue>(),
            ReportPath: "",
            CompletedAtUtc: DateTimeOffset.UtcNow);

        var path1 = await sut.GenerateReportAsync(summary);
        var path2 = await sut.GenerateReportAsync(summary);

        Assert.Equal(path1, path2);
        Assert.True(File.Exists(path2));
    }

    #endregion
}
