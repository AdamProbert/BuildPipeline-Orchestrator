using System.Diagnostics;
using BuildPipeline.Orchestrator.Config;
using BuildPipeline.Orchestrator.Infrastructure;
using Microsoft.Extensions.Logging;

namespace BuildPipeline.Orchestrator.Activities;

public sealed class SimulatedPipelineActivities : IPipelineActivities
{
    private readonly PipelineConfig _config;
    private readonly ILogger<SimulatedPipelineActivities> _logger;

    public SimulatedPipelineActivities(PipelineConfig config, ILogger<SimulatedPipelineActivities> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task<ProjectMetadata> ValidateUnityProjectAsync(PipelineWorkflowInput input)
    {
        _logger.LogInformation("[Simulated] Validating Unity project for run {RunId}", input.RunId);

        var projectDir = _config.UnityProjectPath;

        if (!Directory.Exists(projectDir))
            throw new InvalidOperationException($"Unity project directory not found: {projectDir}");

        // Read real version if the file exists, otherwise use a placeholder
        var versionFile = Path.Combine(projectDir, "ProjectSettings", "ProjectVersion.txt");
        var version = "simulated-6000.0.0f1";

        if (File.Exists(versionFile))
        {
            var content = await File.ReadAllTextAsync(versionFile);
            version = content
                .Split('\n')
                .Select(l => l.Trim())
                .FirstOrDefault(l => l.StartsWith("m_EditorVersion:"))
                ?.Split(':', 2)[1].Trim() ?? version;
        }

        _logger.LogInformation("[Simulated] Project valid: Unity {Version}", version);
        return new ProjectMetadata(projectDir, version, DateTimeOffset.UtcNow);
    }

    public async Task<BuildArtifactResult> ExecutePlatformBuildAsync(PlatformBuildInput input)
    {
        var platformName = input.Platform.ToString().ToLowerInvariant();
        _logger.LogInformation("[Simulated] Starting {Platform} build for run {RunId}", platformName, input.RunId);

        var sw = Stopwatch.StartNew();

        var extension = PlatformRegistry.Platforms.TryGetValue(input.Platform, out var meta) ? meta.ArtifactExtension : "";
        var artifactName = $"{input.RunId}-{platformName}{extension}";
        var artifactPath = Path.Combine(_config.OutputDirectory, input.RunId, artifactName);

        FileSystemUtilities.EnsureDirectory(Path.GetDirectoryName(artifactPath)!);

        // Simulate build duration
        await Task.Delay(TimeSpan.FromSeconds(2));

        await File.WriteAllTextAsync(artifactPath,
            $"Simulated {platformName} build artifact for {input.RunId}\n" +
            $"Generated at {DateTimeOffset.UtcNow:O}");

        sw.Stop();
        _logger.LogInformation("[Simulated] Completed {Platform} build in {ElapsedMs}ms -> {ArtifactPath}",
            platformName, sw.ElapsedMilliseconds, artifactPath);

        return new BuildArtifactResult(input.Platform, artifactPath, DateTimeOffset.UtcNow);
    }

    public async Task<string> GenerateReportAsync(PipelineRunSummary summary)
    {
        _logger.LogInformation("[Simulated] Generating report for run {RunId}", summary.RunId);

        var reportName = FileSystemUtilities.SanitizeFileName($"report-{summary.RunId}.json");
        var reportPath = Path.Combine(_config.OutputDirectory, summary.RunId, reportName);

        await FileSystemUtilities.WriteJsonFileAsync(reportPath, summary);

        _logger.LogInformation("[Simulated] Report written to {ReportPath}", reportPath);
        return reportPath;
    }
}
