namespace BuildPipeline.Orchestrator.Activities;

public record PipelineWorkflowInput(
    string RunId,
    IDictionary<string, string>? Parameters = null)
{
    public static PipelineWorkflowInput CreateDefault(string? runId = null, IDictionary<string, string>? parameters = null) =>
        new(runId ?? $"run-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}", parameters);
}

public record ProjectMetadata(
    string UnityProjectPath,
    string ProjectVersion,
    DateTimeOffset DetectedAtUtc);

public enum BuildPlatform
{
    Android,
    iOS
}

public record PlatformBuildInput(
    string RunId,
    BuildPlatform Platform);

public record BuildArtifactResult(
    BuildPlatform Platform,
    string ArtifactPath,
    DateTimeOffset CompletedAtUtc);

public record PipelineRunSummary(
    string RunId,
    ProjectMetadata ProjectMetadata,
    BuildArtifactResult? AndroidBuild,
    BuildArtifactResult? iOSBuild,
    string ReportPath,
    DateTimeOffset CompletedAtUtc);
