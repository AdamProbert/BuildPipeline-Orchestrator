using System.Reflection;
using Microsoft.Extensions.Configuration;

namespace BuildPipeline.Orchestrator.Config;

public sealed record PipelineConfig(
    string TemporalAddress,
    string TemporalNamespace,
    string UnityProjectPath,
    string OutputDirectory,
    string TaskQueue,
    string? UnityEditorPath,
    bool SimulateBuild,
    string? OtlpEndpoint)
{
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

        return new PipelineConfig(
            temporalAddress,
            temporalNamespace,
            Path.GetFullPath(unityProjectPath),
            Path.GetFullPath(outputDirectory),
            taskQueue,
            unityEditorPath,
            simulateBuild,
            otlpEndpoint);
    }

    private static string GetAssemblyDirectory()
    {
        var location = Assembly.GetExecutingAssembly().Location;
        return Path.GetDirectoryName(location)
               ?? Directory.GetCurrentDirectory();
    }
}
