using System.Reflection;
using Microsoft.Extensions.Configuration;

namespace BuildPipeline.Orchestrator.Config;

public enum ProjectCopyStrategy
{
    /// <summary>Full recursive file copy (works everywhere, no OS-specific features).</summary>
    Full,
    /// <summary>NTFS junctions for read-only dirs, hard copy for writable dirs (Windows only, falls back to Full).</summary>
    Junction
}

public sealed record PipelineConfig(
    string TemporalAddress,
    string TemporalNamespace,
    string UnityProjectPath,
    string OutputDirectory,
    string TaskQueue,
    string? UnityEditorPath,
    bool SimulateBuild,
    string? OtlpEndpoint,
    ProjectCopyStrategy CopyStrategy,
    HashSet<string> JunctionDirs)
{
    private static readonly HashSet<string> DefaultJunctionDirs =
        new(["Assets", "Packages", "ProjectSettings"], StringComparer.OrdinalIgnoreCase);

    public static PipelineConfig Load(IConfiguration configuration)
    {
        var entryDirectory = GetAssemblyDirectory();

        var temporalAddress = configuration["TEMPORAL_ADDRESS"] ?? "localhost:7233";
        var temporalNamespace = configuration["TEMPORAL_NAMESPACE"] ?? "default";
        var unityProjectPath = configuration["PIPELINE_UNITY_PROJECT_PATH"]
                               ?? Path.Combine(entryDirectory, "..", "..", "..", "..", "..", "unity-sample");
        var outputDirectory = configuration["PIPELINE_OUTPUT_DIR"]
                              ?? Path.Combine(entryDirectory, "..", "..", "..", "..", "..", "output");
        var taskQueue = configuration["PIPELINE_TASK_QUEUE"] ?? "build-pipeline-task-queue";
        var unityEditorPath = configuration["UNITY_EDITOR_PATH"];
        var simulateBuild = string.Equals(configuration["PIPELINE_SIMULATE"], "true",
            StringComparison.OrdinalIgnoreCase);
        var otlpEndpoint = configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        var copyStrategy = Enum.TryParse<ProjectCopyStrategy>(configuration["PIPELINE_COPY_STRATEGY"], ignoreCase: true, out var cs)
            ? cs
            : ProjectCopyStrategy.Junction;
        var junctionDirsRaw = configuration["PIPELINE_JUNCTION_DIRS"];
        var junctionDirs = string.IsNullOrWhiteSpace(junctionDirsRaw)
            ? DefaultJunctionDirs
            : new HashSet<string>(junctionDirsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);

        return new PipelineConfig(
            temporalAddress,
            temporalNamespace,
            Path.GetFullPath(unityProjectPath),
            Path.GetFullPath(outputDirectory),
            taskQueue,
            unityEditorPath,
            simulateBuild,
            otlpEndpoint,
            copyStrategy,
            junctionDirs);
    }

    private static string GetAssemblyDirectory()
    {
        var location = Assembly.GetExecutingAssembly().Location;
        return Path.GetDirectoryName(location)
               ?? Directory.GetCurrentDirectory();
    }
}
