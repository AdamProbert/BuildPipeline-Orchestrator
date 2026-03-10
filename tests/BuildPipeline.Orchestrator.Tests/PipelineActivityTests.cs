using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using BuildPipeline.Orchestrator.Activities;
using BuildPipeline.Orchestrator.Config;
using BuildPipeline.Orchestrator.Infrastructure;
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
        {
            RemoveAllJunctions(_tempDir);
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    /// <summary>Recursively find and remove junctions so Directory.Delete doesn't follow into targets.</summary>
    private static void RemoveAllJunctions(string root)
    {
        foreach (var dir in Directory.GetDirectories(root))
        {
            if (FileSystemUtilities.IsJunction(dir))
                Directory.Delete(dir, false);
            else
                RemoveAllJunctions(dir);
        }
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

    private PipelineConfig ConfigFor(string unityProjectPath, string? unityEditorPath = null, ProjectCopyStrategy copyStrategy = ProjectCopyStrategy.Junction) =>
        new(
            TemporalAddress: "localhost:7233",
            TemporalNamespace: "default",
            UnityProjectPath: unityProjectPath,
            OutputDirectory: _outputDir,
            TaskQueue: "test-queue",
            UnityEditorPath: unityEditorPath,
            SimulateBuild: true,
            OtlpEndpoint: null,
            CopyStrategy: copyStrategy,
            JunctionDirs: new HashSet<string>(["Assets", "Packages", "ProjectSettings"], StringComparer.OrdinalIgnoreCase));

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
            {
                RemoveAllJunctions(clonedPath);
                Directory.Delete(clonedPath, recursive: true);
            }
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

    [SkippableFact]
    public void TryCreateJunction_CreatesWorkingJunction()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Junctions are Windows-only");

        var sourceDir = Path.Combine(_tempDir, "junction-source");
        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(Path.Combine(sourceDir, "test.txt"), "hello");

        var junctionPath = Path.Combine(_tempDir, "junction-link");

        var created = FileSystemUtilities.TryCreateJunction(junctionPath, sourceDir);

        Assert.True(created);
        Assert.True(FileSystemUtilities.IsJunction(junctionPath));
        // Can read files through the junction
        Assert.Equal("hello", File.ReadAllText(Path.Combine(junctionPath, "test.txt")));
    }

    [SkippableFact]
    public void JunctionDeletion_DoesNotDeleteOriginal()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Junctions are Windows-only");

        var sourceDir = Path.Combine(_tempDir, "junction-original");
        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(Path.Combine(sourceDir, "keep.txt"), "preserve me");

        var junctionPath = Path.Combine(_tempDir, "junction-to-delete");
        FileSystemUtilities.TryCreateJunction(junctionPath, sourceDir);

        // Delete the junction
        Directory.Delete(junctionPath, false);

        // Original must survive
        Assert.False(Directory.Exists(junctionPath));
        Assert.True(Directory.Exists(sourceDir));
        Assert.True(File.Exists(Path.Combine(sourceDir, "keep.txt")));
    }

    [SkippableFact]
    public void CopyDirectoryHybrid_CreatesJunctionsForSpecifiedDirs()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Junctions are Windows-only");

        var sourceDir = Path.Combine(_tempDir, "hybrid-source");
        Directory.CreateDirectory(Path.Combine(sourceDir, "Assets", "Scripts"));
        File.WriteAllText(Path.Combine(sourceDir, "Assets", "Scripts", "Main.cs"), "code");
        Directory.CreateDirectory(Path.Combine(sourceDir, "Library"));
        File.WriteAllText(Path.Combine(sourceDir, "Library", "cache.db"), "data");
        File.WriteAllText(Path.Combine(sourceDir, "root.txt"), "root");

        var destDir = Path.Combine(_tempDir, "hybrid-dest");

        FileSystemUtilities.CopyDirectoryHybrid(
            sourceDir, destDir,
            junctionDirs: ["Assets"],
            excludeDirs: ["Temp"]);

        // Assets should be a junction
        Assert.True(FileSystemUtilities.IsJunction(Path.Combine(destDir, "Assets")));
        // Library should be a real copy
        Assert.False(FileSystemUtilities.IsJunction(Path.Combine(destDir, "Library")));
        Assert.True(File.Exists(Path.Combine(destDir, "Library", "cache.db")));
        // Root files should be copied
        Assert.True(File.Exists(Path.Combine(destDir, "root.txt")));
        // Can read through junction
        Assert.Equal("code", File.ReadAllText(Path.Combine(destDir, "Assets", "Scripts", "Main.cs")));
    }

    [SkippableFact]
    public async Task PrepareProjectCopy_UsesJunctionsForReadOnlyDirs()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Junctions are Windows-only");

        var projectDir = CreateFakeUnityProject();
        // Add Packages dir (junction candidate) and Library (must be copied)
        Directory.CreateDirectory(Path.Combine(projectDir, "Packages"));
        File.WriteAllText(Path.Combine(projectDir, "Packages", "manifest.json"), "{}");
        Directory.CreateDirectory(Path.Combine(projectDir, "Library"));
        File.WriteAllText(Path.Combine(projectDir, "Library", "ArtifactDB"), "db");

        var editorPath = CreateFakeUnityEditor("AndroidPlayer");
        var sut = new PipelineActivities(
            ConfigFor(projectDir, editorPath),
            NullLogger<PipelineActivities>.Instance);
        var input = new PrepareProjectCopyInput("junction-run", BuildPlatform.Android);

        var clonedPath = await sut.PrepareProjectCopyAsync(input);

        try
        {
            // Assets and Packages should be junctions
            Assert.True(FileSystemUtilities.IsJunction(Path.Combine(clonedPath, "Assets")));
            Assert.True(FileSystemUtilities.IsJunction(Path.Combine(clonedPath, "Packages")));
            Assert.True(FileSystemUtilities.IsJunction(Path.Combine(clonedPath, "ProjectSettings")));
            // Library should be a real copy
            Assert.False(FileSystemUtilities.IsJunction(Path.Combine(clonedPath, "Library")));
            Assert.True(File.Exists(Path.Combine(clonedPath, "Library", "ArtifactDB")));
        }
        finally
        {
            await sut.CleanupProjectCopyAsync(clonedPath);
        }
    }

    [SkippableFact]
    public async Task CleanupProjectCopy_WithJunctions_PreservesOriginals()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Junctions are Windows-only");

        var projectDir = CreateFakeUnityProject();
        Directory.CreateDirectory(Path.Combine(projectDir, "Packages"));
        File.WriteAllText(Path.Combine(projectDir, "Packages", "manifest.json"), "{}");

        var editorPath = CreateFakeUnityEditor("AndroidPlayer");
        var sut = new PipelineActivities(
            ConfigFor(projectDir, editorPath),
            NullLogger<PipelineActivities>.Instance);
        var input = new PrepareProjectCopyInput("cleanup-junction-run", BuildPlatform.iOS);

        var clonedPath = await sut.PrepareProjectCopyAsync(input);

        // Cleanup
        await sut.CleanupProjectCopyAsync(clonedPath);

        // Cloned dir should be gone
        Assert.False(Directory.Exists(clonedPath));
        // Original project must be intact
        Assert.True(Directory.Exists(Path.Combine(projectDir, "Assets")));
        Assert.True(Directory.Exists(Path.Combine(projectDir, "Packages")));
        Assert.True(File.Exists(Path.Combine(projectDir, "Packages", "manifest.json")));
        Assert.True(Directory.Exists(Path.Combine(projectDir, "ProjectSettings")));
    }

    #endregion
}
