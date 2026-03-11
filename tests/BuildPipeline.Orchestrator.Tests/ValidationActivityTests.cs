using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using BuildPipeline.Orchestrator.Activities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BuildPipeline.Orchestrator.Tests;

public class ValidationActivityTests : PipelineActivityTestBase
{
    [Fact]
    public async Task Validate_ValidProject_ReturnsMetadata()
    {
        var projectDir = CreateFakeUnityProject("2022.3.10f1");
        var editorPath = CreateFakeUnityEditor("AndroidPlayer", "iOSSupport");
        var sut = new PipelineActivities(
            ConfigFor(projectDir, editorPath),
            NullLogger<PipelineActivities>.Instance);

        var result = await sut.ValidateUnityProjectAsync(new PipelineWorkflowInput("test-run-1"));

        Assert.Equal(projectDir, result.UnityProjectPath);
        Assert.Equal("2022.3.10f1", result.ProjectVersion);
        Assert.True(result.DetectedAtUtc <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Validate_MissingProjectDir_Throws()
    {
        var sut = new PipelineActivities(
            ConfigFor(Path.Combine(TempDir, "does-not-exist")),
            NullLogger<PipelineActivities>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.ValidateUnityProjectAsync(new PipelineWorkflowInput("test-run-2")));
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public async Task Validate_MissingAssetsDir_Throws()
    {
        var projectDir = Path.Combine(TempDir, "no-assets");
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
        var projectDir = Path.Combine(TempDir, "no-settings");
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
        var projectDir = Path.Combine(TempDir, "no-version");
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
            ConfigFor(projectDir, Path.Combine(TempDir, "nonexistent", "Unity.exe")),
            NullLogger<PipelineActivities>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.ValidateUnityProjectAsync(new PipelineWorkflowInput("run")));
        Assert.Contains("Unity editor not found at configured path", ex.Message);
        Assert.Contains("UNITY_EDITOR_PATH", ex.Message);
    }

    [Fact]
    public async Task Validate_NoEditorPathAndAutoDetectFails_Throws()
    {
        var projectDir = CreateFakeUnityProject("9999.0.0f1");
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
        var editorPath = CreateFakeUnityEditor();
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
        var editorPath = CreateFakeUnityEditor();
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
        var editorPath = CreateFakeUnityEditor();
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
}
