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
    HashSet<string> JunctionDirs,
    Activities.TimeoutConfig Timeouts)
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

        var timeouts = new Activities.TimeoutConfig(
            ValidationTimeout: ParseTimeSpan(configuration["PIPELINE_VALIDATION_TIMEOUT_SECONDS"], Activities.TimeoutConfig.Default.ValidationTimeout),
            BuildTimeout: ParseTimeSpan(configuration["PIPELINE_BUILD_TIMEOUT_MINUTES"], Activities.TimeoutConfig.Default.BuildTimeout, minutes: true),
            ReportTimeout: ParseTimeSpan(configuration["PIPELINE_REPORT_TIMEOUT_SECONDS"], Activities.TimeoutConfig.Default.ReportTimeout),
            LicensingMaxRetries: int.TryParse(configuration["PIPELINE_LICENSING_MAX_RETRIES"], out var lmr) ? lmr : Activities.TimeoutConfig.Default.LicensingMaxRetries,
            LicensingRetryDelay: ParseTimeSpan(configuration["PIPELINE_LICENSING_RETRY_DELAY_SECONDS"], Activities.TimeoutConfig.Default.LicensingRetryDelay),
            BuildRetryInterval: ParseTimeSpan(configuration["PIPELINE_BUILD_RETRY_INTERVAL_SECONDS"], Activities.TimeoutConfig.Default.BuildRetryInterval));

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
            junctionDirs,
            timeouts);
    }

    private static TimeSpan? ParseTimeSpan(string? value, TimeSpan? fallback, bool minutes = false)
    {
        if (double.TryParse(value, out var v))
            return minutes ? TimeSpan.FromMinutes(v) : TimeSpan.FromSeconds(v);
        return fallback;
    }

    private static string GetAssemblyDirectory()
    {
        var location = Assembly.GetExecutingAssembly().Location;
        return Path.GetDirectoryName(location)
               ?? Directory.GetCurrentDirectory();
    }
}
