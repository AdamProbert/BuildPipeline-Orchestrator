using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using BuildPipeline.Orchestrator.Activities;
using BuildPipeline.Orchestrator.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BuildPipeline.Orchestrator.Tests;

public class ProjectCopyActivityTests : PipelineActivityTestBase
{
    [Fact]
    public async Task PrepareProjectCopy_ClonesProjectToTempDir()
    {
        var projectDir = CreateFakeUnityProject();
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

        await sut.CleanupProjectCopyAsync(projectDir);
        Assert.True(Directory.Exists(projectDir));
    }

    [SkippableFact]
    public void TryCreateJunction_CreatesWorkingJunction()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Junctions are Windows-only");

        var sourceDir = Path.Combine(TempDir, "junction-source");
        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(Path.Combine(sourceDir, "test.txt"), "hello");

        var junctionPath = Path.Combine(TempDir, "junction-link");

        var created = FileSystemUtilities.TryCreateJunction(junctionPath, sourceDir);

        Assert.True(created);
        Assert.True(FileSystemUtilities.IsJunction(junctionPath));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(junctionPath, "test.txt")));
    }
}
