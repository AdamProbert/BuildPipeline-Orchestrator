namespace BuildPipeline.Orchestrator.Activities;

public record TimeoutConfig(
    TimeSpan? ValidationTimeout = null,
    TimeSpan? BuildTimeout = null,
    TimeSpan? ReportTimeout = null,
    int LicensingMaxRetries = 5,
    TimeSpan? LicensingRetryDelay = null)
{
    public static readonly TimeoutConfig Default = new(
        ValidationTimeout: TimeSpan.FromSeconds(30),
        BuildTimeout: TimeSpan.FromMinutes(30),
        ReportTimeout: TimeSpan.FromSeconds(60),
        LicensingMaxRetries: 5,
        LicensingRetryDelay: TimeSpan.FromSeconds(30));
}

public record PipelineWorkflowInput(
    string RunId,
    IDictionary<string, string>? Parameters = null,
    TimeoutConfig? Timeouts = null)
{
    public static PipelineWorkflowInput CreateDefault(string? runId = null, IDictionary<string, string>? parameters = null, TimeoutConfig? timeouts = null) =>
        new(runId ?? GenerateRunId(), parameters, timeouts);

    private static string GenerateRunId()
    {
        string[] locations = ["old-kent-road", "whitechapel", "angel-islington", "euston-road", "pentonville",
            "pall-mall", "whitehall", "northumberland", "bow-street", "marlborough",
            "vine-street", "strand", "fleet-street", "trafalgar-square", "leicester-square",
            "coventry-street", "piccadilly", "regent-street", "oxford-street", "bond-street",
            "park-lane", "mayfair", "boardwalk", "marvin-gardens"];
        string[] pokemon = ["pikachu", "charizard", "mewtwo", "snorlax", "eevee", "gengar",
            "jigglypuff", "bulbasaur", "squirtle", "lucario", "gardevoir", "rayquaza",
            "arceus", "mew", "dragonite", "gyarados", "alakazam", "machamp",
            "lapras", "umbreon", "togekiss", "blaziken", "greninja", "mimikyu"];

        var rng = Random.Shared;
        var loc = locations[rng.Next(locations.Length)];
        var poke = pokemon[rng.Next(pokemon.Length)];
        var suffix = rng.Next(100, 999);
        return $"{loc}-{poke}-{suffix}";
    }
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

/// <summary>Per-platform build metadata used by activities and validation.</summary>
public record PlatformMetadata(string ModuleFolderName, string ArtifactExtension);

public static class PlatformRegistry
{
    public static readonly IReadOnlyDictionary<BuildPlatform, PlatformMetadata> Platforms =
        new Dictionary<BuildPlatform, PlatformMetadata>
        {
            [BuildPlatform.Android] = new("AndroidPlayer", ".apk"),
            [BuildPlatform.iOS] = new("iOSSupport", ""),
        };

    /// <summary>
    /// Parse a comma-separated platform list (e.g. "android,ios") into enum values.
    /// Returns all registered platforms when the input is null or empty.
    /// </summary>
    public static List<BuildPlatform> Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return Platforms.Keys.ToList();

        var result = new List<BuildPlatform>();
        foreach (var token in input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse<BuildPlatform>(token, ignoreCase: true, out var platform) && Platforms.ContainsKey(platform))
                result.Add(platform);
            else
                throw new ArgumentException($"Unknown platform '{token}'. Known platforms: {string.Join(", ", Platforms.Keys)}");
        }

        return result.Count > 0 ? result : Platforms.Keys.ToList();
    }
}

public record PrepareProjectCopyInput(string RunId, BuildPlatform Platform);

public record PlatformBuildInput(
    string RunId,
    BuildPlatform Platform,
    TimeoutConfig? Timeouts = null,
    string? ProjectPathOverride = null);

public record BuildArtifactResult(
    BuildPlatform Platform,
    string ArtifactPath,
    DateTimeOffset CompletedAtUtc);

public record PipelineRunSummary(
    string RunId,
    ProjectMetadata ProjectMetadata,
    IReadOnlyList<BuildArtifactResult> BuildResults,
    string ReportPath,
    DateTimeOffset CompletedAtUtc);
