using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using BuildPipeline.Orchestrator.Activities;
using BuildPipeline.Orchestrator.Config;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BuildPipeline.Orchestrator.Tests;

public class PipelineActivityTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _outputDir;

    public PipelineActivityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"pipeline-test-{Guid.NewGuid():N}");
        _outputDir = Path.Combine(_tempDir, "output");
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(_outputDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string CreateFakeUnityProject(string? editorVersion = "6000.2.7f2")
    {
        var projectDir = Path.Combine(_tempDir, "unity-project");
        Directory.CreateDirectory(Path.Combine(projectDir, "Assets"));
        var settingsDir = Path.Combine(projectDir, "ProjectSettings");
        Directory.CreateDirectory(settingsDir);

        if (editorVersion != null)
        {
            File.WriteAllText(
                Path.Combine(settingsDir, "ProjectVersion.txt"),
                $"m_EditorVersion: {editorVersion}\nm_EditorVersionWithRevision: {editorVersion} (abc123)\n");
        }

        return projectDir;
    }

    private string CreateFakeUnityEditor(params string[] platformModules)
    {
        var editorDir = Path.Combine(_tempDir, "Editor");
        var editorExe = Path.Combine(editorDir, "Unity.exe");
        Directory.CreateDirectory(editorDir);
        File.WriteAllText(editorExe, "fake");

        var playbackDir = Path.Combine(editorDir, "Data", "PlaybackEngines");
        Directory.CreateDirectory(playbackDir);
        foreach (var module in platformModules)
            Directory.CreateDirectory(Path.Combine(playbackDir, module));

        return editorExe;
    }

    private PipelineConfig ConfigFor(string unityProjectPath, string? unityEditorPath = null) =>
        new(
            TemporalAddress: "localhost:7233",
            TemporalNamespace: "default",
            UnityProjectPath: unityProjectPath,
            OutputDirectory: _outputDir,
            TaskQueue: "test-queue",
            UnityEditorPath: unityEditorPath,
            SimulateBuild: true,
            OtlpEndpoint: null);

    #region ValidateUnityProjectAsync — Real activities

    [Fact]
    public async Task Validate_ValidProject_ReturnsMetadata()
    {
        var projectDir = CreateFakeUnityProject("2022.3.10f1");
        var editorPath = CreateFakeUnityEditor("AndroidPlayer", "iOSSupport");
        var sut = new PipelineActivities(
            ConfigFor(projectDir, editorPath),
            NullLogger<PipelineActivities>.Instance);
        var input = new PipelineWorkflowInput("test-run-1");

        var result = await sut.ValidateUnityProjectAsync(input);

        Assert.Equal(projectDir, result.UnityProjectPath);
        Assert.Equal("2022.3.10f1", result.ProjectVersion);
        Assert.True(result.DetectedAtUtc <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Validate_MissingProjectDir_Throws()
    {
        var sut = new PipelineActivities(
            ConfigFor(Path.Combine(_tempDir, "does-not-exist")),
            NullLogger<PipelineActivities>.Instance);
        var input = new PipelineWorkflowInput("test-run-2");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.ValidateUnityProjectAsync(input));
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public async Task Validate_MissingAssetsDir_Throws()
    {
        var projectDir = Path.Combine(_tempDir, "no-assets");
        Directory.CreateDirectory(projectDir);

        var sut = new PipelineActivities(
            ConfigFor(projectDir),
            NullLogger<PipelineActivities>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.ValidateUnityProjectAsync(new PipelineWorkflowInput("run")));
        Assert.Contains("Assets", ex.Message);
    }

    [Fact]
    public async Task Validate_MissingProjectSettings_Throws()
    {
        var projectDir = Path.Combine(_tempDir, "no-settings");
        Directory.CreateDirectory(Path.Combine(projectDir, "Assets"));

        var sut = new PipelineActivities(
            ConfigFor(projectDir),
            NullLogger<PipelineActivities>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.ValidateUnityProjectAsync(new PipelineWorkflowInput("run")));
        Assert.Contains("ProjectSettings", ex.Message);
    }

    [Fact]
    public async Task Validate_MissingVersionFile_Throws()
    {
        var projectDir = Path.Combine(_tempDir, "no-version");
        Directory.CreateDirectory(Path.Combine(projectDir, "Assets"));
        Directory.CreateDirectory(Path.Combine(projectDir, "ProjectSettings"));

        var sut = new PipelineActivities(
            ConfigFor(projectDir),
            NullLogger<PipelineActivities>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.ValidateUnityProjectAsync(new PipelineWorkflowInput("run")));
        Assert.Contains("ProjectVersion.txt", ex.Message);
    }

    [Fact]
    public async Task Validate_ExplicitEditorPathNotFound_Throws()
    {
        var projectDir = CreateFakeUnityProject();
        var sut = new PipelineActivities(
            ConfigFor(projectDir, Path.Combine(_tempDir, "nonexistent", "Unity.exe")),
            NullLogger<PipelineActivities>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.ValidateUnityProjectAsync(new PipelineWorkflowInput("run")));
        Assert.Contains("Unity editor not found at configured path", ex.Message);
        Assert.Contains("UNITY_EDITOR_PATH", ex.Message);
    }

    [Fact]
    public async Task Validate_NoEditorPathAndAutoDetectFails_Throws()
    {
        var projectDir = CreateFakeUnityProject("9999.0.0f1"); // version that won't exist on disk
        var sut = new PipelineActivities(
            ConfigFor(projectDir),
            NullLogger<PipelineActivities>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.ValidateUnityProjectAsync(new PipelineWorkflowInput("run")));
        Assert.Contains("9999.0.0f1", ex.Message);
        Assert.Contains("Unity Hub", ex.Message);
        Assert.Contains("UNITY_EDITOR_PATH", ex.Message);
    }

    [Fact]
    public async Task Validate_MissingAndroidModule_Throws()
    {
        var projectDir = CreateFakeUnityProject();
        var editorPath = CreateFakeUnityEditor(); // No platform modules installed
        var input = new PipelineWorkflowInput("run", new Dictionary<string, string> { ["platforms"] = "android" });

        var sut = new PipelineActivities(
            ConfigFor(projectDir, editorPath),
            NullLogger<PipelineActivities>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.ValidateUnityProjectAsync(input));
        Assert.Contains("AndroidPlayer", ex.Message);
        Assert.Contains("Unity Hub", ex.Message);
    }

    [Fact]
    public async Task Validate_MissingiOSModule_Throws()
    {
        var projectDir = CreateFakeUnityProject();
        var editorPath = CreateFakeUnityEditor(); // No platform modules
        var input = new PipelineWorkflowInput("run", new Dictionary<string, string> { ["platforms"] = "ios" });

        var sut = new PipelineActivities(
            ConfigFor(projectDir, editorPath),
            NullLogger<PipelineActivities>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.ValidateUnityProjectAsync(input));
        Assert.Contains("iOSSupport", ex.Message);
    }

    [Fact]
    public async Task Validate_BothModulesMissing_ListsBoth()
    {
        var projectDir = CreateFakeUnityProject();
        var editorPath = CreateFakeUnityEditor(); // No platform modules
        var input = new PipelineWorkflowInput("run", new Dictionary<string, string> { ["platforms"] = "android,ios" });

        var sut = new PipelineActivities(
            ConfigFor(projectDir, editorPath),
            NullLogger<PipelineActivities>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.ValidateUnityProjectAsync(input));
        Assert.Contains("AndroidPlayer", ex.Message);
        Assert.Contains("iOSSupport", ex.Message);
    }

    [Fact]
    public async Task Validate_WithAndroidModuleInstalled_Passes()
    {
        var projectDir = CreateFakeUnityProject("2022.3.10f1");
        var editorPath = CreateFakeUnityEditor("AndroidPlayer");
        var input = new PipelineWorkflowInput("run", new Dictionary<string, string> { ["platforms"] = "android" });

        var sut = new PipelineActivities(
            ConfigFor(projectDir, editorPath),
            NullLogger<PipelineActivities>.Instance);

        var result = await sut.ValidateUnityProjectAsync(input);

        Assert.Equal("2022.3.10f1", result.ProjectVersion);
    }

    [Fact]
    public async Task Validate_WithBothModulesInstalled_Passes()
    {
        var projectDir = CreateFakeUnityProject();
        var editorPath = CreateFakeUnityEditor("AndroidPlayer", "iOSSupport");
        var input = new PipelineWorkflowInput("run", new Dictionary<string, string> { ["platforms"] = "android,ios" });

        var sut = new PipelineActivities(
            ConfigFor(projectDir, editorPath),
            NullLogger<PipelineActivities>.Instance);

        var result = await sut.ValidateUnityProjectAsync(input);

        Assert.Equal("6000.2.7f2", result.ProjectVersion);
    }

    #endregion

    #region ValidateUnityProjectAsync — Simulated activities

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
        var projectDir = Path.Combine(_tempDir, "sim-no-version");
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
            ConfigFor(Path.Combine(_tempDir, "nope")),
            NullLogger<SimulatedPipelineActivities>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.ValidateUnityProjectAsync(new PipelineWorkflowInput("run")));
    }

    #endregion

    #region ExecutePlatformBuildAsync — Simulated

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

    #region GenerateReportAsync — Simulated

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
            BuildResults: new[] { new BuildArtifactResult(BuildPlatform.Android, "/fake/path.apk", DateTimeOffset.UtcNow) },
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
            ReportPath: "",
            CompletedAtUtc: DateTimeOffset.UtcNow);

        var path1 = await sut.GenerateReportAsync(summary);
        var path2 = await sut.GenerateReportAsync(summary);

        Assert.Equal(path1, path2);
        Assert.True(File.Exists(path2));
    }

    #endregion

    #region PrepareProjectCopyAsync / CleanupProjectCopyAsync

    [Fact]
    public async Task PrepareProjectCopy_ClonesProjectToTempDir()
    {
        var projectDir = CreateFakeUnityProject();
        // Add a Temp dir (should be excluded from clone)
        Directory.CreateDirectory(Path.Combine(projectDir, "Temp"));
        File.WriteAllText(Path.Combine(projectDir, "Temp", "UnityLockfile"), "locked");

        var sut = new SimulatedPipelineActivities(
            ConfigFor(projectDir),
            NullLogger<SimulatedPipelineActivities>.Instance);
        var input = new PrepareProjectCopyInput("clone-run", BuildPlatform.Android);

        var clonedPath = await sut.PrepareProjectCopyAsync(input);

        try
        {
            Assert.True(Directory.Exists(clonedPath));
            Assert.Contains("clone-run-android", clonedPath);
        }
        finally
        {
            if (Directory.Exists(clonedPath))
                Directory.Delete(clonedPath, recursive: true);
        }
    }

    [Fact]
    public async Task CleanupProjectCopy_RemovesDirectory()
    {
        var projectDir = CreateFakeUnityProject();
        var sut = new SimulatedPipelineActivities(
            ConfigFor(projectDir),
            NullLogger<SimulatedPipelineActivities>.Instance);
        var input = new PrepareProjectCopyInput("cleanup-run", BuildPlatform.iOS);

        var clonedPath = await sut.PrepareProjectCopyAsync(input);
        Assert.True(Directory.Exists(clonedPath));

        await sut.CleanupProjectCopyAsync(clonedPath);
        Assert.False(Directory.Exists(clonedPath));
    }

    [Fact]
    public async Task RealPrepareProjectCopy_ExcludesTempDir()
    {
        var projectDir = CreateFakeUnityProject();
        // Simulate Unity lock file in Temp
        var tempDir = Path.Combine(projectDir, "Temp");
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "UnityLockfile"), "locked");

        var editorPath = CreateFakeUnityEditor("AndroidPlayer");
        var sut = new PipelineActivities(
            ConfigFor(projectDir, editorPath),
            NullLogger<PipelineActivities>.Instance);
        var input = new PrepareProjectCopyInput("real-clone-run", BuildPlatform.Android);

        var clonedPath = await sut.PrepareProjectCopyAsync(input);

        try
        {
            Assert.True(Directory.Exists(clonedPath));
            Assert.True(Directory.Exists(Path.Combine(clonedPath, "Assets")));
            Assert.True(Directory.Exists(Path.Combine(clonedPath, "ProjectSettings")));
            // Temp dir should NOT be copied
            Assert.False(Directory.Exists(Path.Combine(clonedPath, "Temp")));
        }
        finally
        {
            if (Directory.Exists(clonedPath))
                Directory.Delete(clonedPath, recursive: true);
        }
    }

    [Fact]
    public async Task RealCleanupProjectCopy_RefusesPathOutsideTempDir()
    {
        var projectDir = CreateFakeUnityProject();
        var sut = new PipelineActivities(
            ConfigFor(projectDir),
            NullLogger<PipelineActivities>.Instance);

        // Should not delete an arbitrary path
        await sut.CleanupProjectCopyAsync(projectDir);
        Assert.True(Directory.Exists(projectDir));
    }

    #endregion
}
