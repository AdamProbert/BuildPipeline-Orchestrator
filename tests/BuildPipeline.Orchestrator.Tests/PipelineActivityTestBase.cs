using System;
using System.Collections.Generic;
using System.IO;
using BuildPipeline.Orchestrator.Activities;
using BuildPipeline.Orchestrator.Config;
using BuildPipeline.Orchestrator.Infrastructure;

namespace BuildPipeline.Orchestrator.Tests;

public abstract class PipelineActivityTestBase : IDisposable
{
    protected readonly string TempDir;
    protected readonly string OutputDir;

    protected PipelineActivityTestBase()
    {
        TempDir = Path.Combine(Path.GetTempPath(), $"pipeline-test-{Guid.NewGuid():N}");
        OutputDir = Path.Combine(TempDir, "output");
        Directory.CreateDirectory(TempDir);
        Directory.CreateDirectory(OutputDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(TempDir))
        {
            RemoveAllJunctions(TempDir);
            Directory.Delete(TempDir, recursive: true);
        }
    }

    protected static void RemoveAllJunctions(string root)
    {
        foreach (var dir in Directory.GetDirectories(root))
        {
            if (FileSystemUtilities.IsJunction(dir))
                Directory.Delete(dir, false);
            else
                RemoveAllJunctions(dir);
        }
    }

    protected string CreateFakeUnityProject(string? editorVersion = "6000.2.7f2")
    {
        var projectDir = Path.Combine(TempDir, "unity-project");
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

    protected string CreateFakeUnityEditor(params string[] platformModules)
    {
        var editorDir = Path.Combine(TempDir, "Editor");
        var editorExe = Path.Combine(editorDir, "Unity.exe");
        Directory.CreateDirectory(editorDir);
        File.WriteAllText(editorExe, "fake");

        var playbackDir = Path.Combine(editorDir, "Data", "PlaybackEngines");
        Directory.CreateDirectory(playbackDir);
        foreach (var module in platformModules)
            Directory.CreateDirectory(Path.Combine(playbackDir, module));

        return editorExe;
    }

    protected PipelineConfig ConfigFor(
        string unityProjectPath,
        string? unityEditorPath = null,
        ProjectCopyStrategy copyStrategy = ProjectCopyStrategy.Junction) =>
        new(
            TemporalAddress: "localhost:7233",
            TemporalNamespace: "default",
            UnityProjectPath: unityProjectPath,
            OutputDirectory: OutputDir,
            TaskQueue: "test-queue",
            UnityEditorPath: unityEditorPath,
            SimulateBuild: true,
            OtlpEndpoint: null,
            CopyStrategy: copyStrategy,
            JunctionDirs: new HashSet<string>(["Assets", "Packages", "ProjectSettings"], StringComparer.OrdinalIgnoreCase),
            Timeouts: TimeoutConfig.Default);
}
