using System.Diagnostics;
using BuildPipeline.Orchestrator.Config;
using BuildPipeline.Orchestrator.Infrastructure;
using Microsoft.Extensions.Logging;

namespace BuildPipeline.Orchestrator.Activities;

public sealed class PipelineActivities : IPipelineActivities
{
    private readonly PipelineConfig _config;
    private readonly ILogger<PipelineActivities> _logger;
    private string? _resolvedEditorPath;

    public PipelineActivities(PipelineConfig config, ILogger<PipelineActivities> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task<ProjectMetadata> ValidateUnityProjectAsync(PipelineWorkflowInput input)
    {
        Activity.Current?.SetTag("run.id", input.RunId);
        Activity.Current?.SetTag("project.path", _config.UnityProjectPath);

        _logger.LogInformation("Validating Unity project at {ProjectPath} for run {RunId}",
            _config.UnityProjectPath, input.RunId);

        var projectDir = _config.UnityProjectPath;

        if (!Directory.Exists(projectDir))
            throw new InvalidOperationException($"Unity project directory not found: {projectDir}");

        var assetsDir = Path.Combine(projectDir, "Assets");
        if (!Directory.Exists(assetsDir))
            throw new InvalidOperationException($"Assets directory missing in Unity project: {assetsDir}");

        var projectSettingsDir = Path.Combine(projectDir, "ProjectSettings");
        if (!Directory.Exists(projectSettingsDir))
            throw new InvalidOperationException($"ProjectSettings directory missing: {projectSettingsDir}");

        var versionFile = Path.Combine(projectSettingsDir, "ProjectVersion.txt");
        if (!File.Exists(versionFile))
            throw new InvalidOperationException($"ProjectVersion.txt not found: {versionFile}");

        var versionContent = await File.ReadAllTextAsync(versionFile);
        var version = versionContent
            .Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("m_EditorVersion:"))
            ?.Split(':', 2)[1].Trim() ?? "unknown";

        // Resolve and verify the Unity editor executable
        _resolvedEditorPath = ResolveEditorPath(version);

        // Validate that required platform build support modules are installed
        var platforms = ParseRequestedPlatforms(input);
        ValidatePlatformModules(platforms);

        _logger.LogInformation("Validated project: Unity {Version} at {Path}, editor: {EditorPath}, platforms: {Platforms}",
            version, projectDir, _resolvedEditorPath, string.Join(", ", platforms));

        Activity.Current?.SetTag("unity.version", version);
        Telemetry.ValidationsTotal.Add(1, new KeyValuePair<string, object?>("status", "success"));

        return new ProjectMetadata(projectDir, version, DateTimeOffset.UtcNow);
    }

    private string ResolveEditorPath(string projectVersion)
    {
        // 1. Explicit env var / config takes priority
        if (!string.IsNullOrWhiteSpace(_config.UnityEditorPath))
        {
            if (!File.Exists(_config.UnityEditorPath))
                throw new InvalidOperationException(
                    $"Unity editor not found at configured path: {_config.UnityEditorPath}. " +
                    $"Verify UNITY_EDITOR_PATH points to a valid Unity executable.");

            _logger.LogInformation("Using explicitly configured Unity editor: {Path}", _config.UnityEditorPath);
            return _config.UnityEditorPath;
        }

        // 2. Auto-detect from project version via Unity Hub default paths
        var detected = UnityEditorLocator.Resolve(projectVersion);
        if (detected != null)
        {
            _logger.LogInformation("Auto-detected Unity {Version} at {Path}", projectVersion, detected);
            return detected;
        }

        // 3. Nothing found — actionable error
        throw new InvalidOperationException(
            $"Unity editor {projectVersion} not found. " +
            $"Install it via Unity Hub or set the UNITY_EDITOR_PATH environment variable.");
    }

    private static List<BuildPlatform> ParseRequestedPlatforms(PipelineWorkflowInput input)
    {
        string? value = null;
        input.Parameters?.TryGetValue("platforms", out value);
        return PlatformRegistry.Parse(value);
    }

    private void ValidatePlatformModules(List<BuildPlatform> platforms)
    {
        var editorDir = Path.GetDirectoryName(_resolvedEditorPath);
        if (editorDir == null)
        {
            _logger.LogWarning("Cannot determine Unity editor directory from path: {Path}. Skipping platform module check.",
                _resolvedEditorPath);
            return;
        }

        var playbackEnginesDir = Path.Combine(editorDir, "Data", "PlaybackEngines");
        if (!Directory.Exists(playbackEnginesDir))
        {
            _logger.LogWarning("PlaybackEngines directory not found at {Path}. Skipping platform module check.",
                playbackEnginesDir);
            return;
        }

        var missing = new List<string>();
        foreach (var platform in platforms)
        {
            if (PlatformRegistry.Platforms.TryGetValue(platform, out var meta))
            {
                var modulePath = Path.Combine(playbackEnginesDir, meta.ModuleFolderName);
                if (!Directory.Exists(modulePath))
                {
                    missing.Add($"{platform} (requires '{meta.ModuleFolderName}' module)");
                    _logger.LogError("Missing build support module: {Module} not found at {Path}",
                        meta.ModuleFolderName, modulePath);
                }
            }
        }

        if (missing.Count > 0)
        {
            var installed = Directory.GetDirectories(playbackEnginesDir)
                .Select(Path.GetFileName)
                .ToArray();
            throw new InvalidOperationException(
                $"Missing Unity platform modules: {string.Join(", ", missing)}. " +
                $"Installed modules: [{string.Join(", ", installed)}]. " +
                $"Install the required modules via Unity Hub.");
        }
    }

    public async Task<BuildArtifactResult> ExecutePlatformBuildAsync(PlatformBuildInput input)
    {
        var platformName = input.Platform.ToString().ToLowerInvariant();
        Activity.Current?.SetTag("run.id", input.RunId);
        Activity.Current?.SetTag("build.platform", platformName);

        _logger.LogInformation("Starting {Platform} build for run {RunId}", platformName, input.RunId);

        var sw = Stopwatch.StartNew();

        var extension = PlatformRegistry.Platforms.TryGetValue(input.Platform, out var meta) ? meta.ArtifactExtension : "";
        var artifactBaseName = $"{input.RunId}-{platformName}";
        var artifactPath = Path.Combine(_config.OutputDirectory, input.RunId, artifactBaseName);

        FileSystemUtilities.EnsureDirectory(Path.GetDirectoryName(artifactPath)!);

        var projectPath = input.ProjectPathOverride ?? _config.UnityProjectPath;
        var lockFile = Path.Combine(projectPath, "Temp", "UnityLockfile");
        if (File.Exists(lockFile))
        {
            throw new InvalidOperationException(
                $"Unity Editor already has this project open. Close the editor before running a build. " +
                $"Project: {projectPath}");
        }

        var licensePath = UnityLicenseChecker.GetLicenseFilePath();
        if (licensePath == null || !File.Exists(licensePath))
        {
            throw new InvalidOperationException(
                $"Unity license file not found at expected path: {licensePath ?? "(unknown OS)"}. " +
                $"Activate a license via Unity Hub or the command line. " +
                $"Refer to: https://docs.unity3d.com/6000.2/Documentation/Manual/LicenseActivationMethods.html");
        }

        var unityPath = _resolvedEditorPath
            ?? throw new InvalidOperationException("Unity editor path not resolved. Run validation first.");
        var args = $"-quit -batchmode -nographics " +
                   $"-projectPath \"{projectPath}\" " +
                   $"-executeMethod BuildScript.BuildForPlatform " +
                   $"-buildPlatform {platformName} " +
                   $"-buildOutput \"{artifactPath}\"";

        var timeouts = input.Timeouts ?? TimeoutConfig.Default;
        var maxLicensingRetries = timeouts.LicensingMaxRetries;
        var licensingDelay = timeouts.LicensingRetryDelay ?? TimeSpan.FromSeconds(30);

        for (var attempt = 1; attempt <= maxLicensingRetries + 1; attempt++)
        {
            var (exitCode, stdout, stderr) = await RunUnityProcessAsync(unityPath, args);

            if (exitCode == 0)
            {
                sw.Stop();
                _logger.LogInformation("Completed {Platform} build in {ElapsedMs}ms -> {ArtifactPath}",
                    platformName, sw.ElapsedMilliseconds, artifactPath);

                Activity.Current?.SetTag("build.duration_ms", sw.ElapsedMilliseconds);
                Activity.Current?.SetTag("build.exit_code", 0);
                Telemetry.BuildDuration.Record(sw.ElapsedMilliseconds, new KeyValuePair<string, object?>("platform", platformName));
                Telemetry.BuildsTotal.Add(1,
                    new KeyValuePair<string, object?>("platform", platformName),
                    new KeyValuePair<string, object?>("status", "success"));

                return new BuildArtifactResult(input.Platform, artifactPath + extension, DateTimeOffset.UtcNow);
            }

            var isLicensingError = IsLicensingError(stdout, stderr);

            if (isLicensingError && attempt <= maxLicensingRetries)
            {
                _logger.LogWarning(
                    "Unity licensing error on attempt {Attempt}/{MaxAttempts}. Retrying in {Delay}s...",
                    attempt, maxLicensingRetries + 1, licensingDelay.TotalSeconds);
                await Task.Delay(licensingDelay);
                continue;
            }

            _logger.LogError("Unity build failed (exit code {ExitCode}).\nStdout: {Stdout}\nStderr: {Stderr}",
                exitCode, stdout, stderr);

            Activity.Current?.SetTag("build.exit_code", exitCode);
            Telemetry.BuildsTotal.Add(1,
                new KeyValuePair<string, object?>("platform", platformName),
                new KeyValuePair<string, object?>("status", "failure"));

            if (isLicensingError)
                throw new InvalidOperationException(
                    $"Unity licensing failed after {attempt} attempts. Stdout: {stdout} Stderr: {stderr}");

            throw new Exception(
                $"Unity {platformName} build failed with exit code {exitCode}. Stdout: {stdout} Stderr: {stderr}");
        }

        // Unreachable, but satisfies the compiler
        throw new InvalidOperationException("Unexpected state in build retry loop.");
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunUnityProcessAsync(string unityPath, string args)
    {
        _logger.LogInformation("Invoking Unity: {UnityPath} {Args}", unityPath, args);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = unityPath,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };

        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static bool IsLicensingError(string stdout, string stderr)
    {
        // Unity prefixes all licensing log lines with "[Licensing::".
        // A failure is a licensing-prefixed line that also contains a failure keyword.
        var lines = (stdout + "\n" + stderr).Split('\n');
        return lines.Any(line =>
            line.Contains("[Licensing::", StringComparison.Ordinal)
            && (line.Contains("fail", StringComparison.OrdinalIgnoreCase)
                || line.Contains("error", StringComparison.OrdinalIgnoreCase)
                || line.Contains("unable", StringComparison.OrdinalIgnoreCase)
                || line.Contains("could not", StringComparison.OrdinalIgnoreCase)));
    }

    public async Task<string> GenerateReportAsync(PipelineRunSummary summary)
    {
        Activity.Current?.SetTag("run.id", summary.RunId);

        _logger.LogInformation("Generating report for run {RunId}", summary.RunId);

        var reportName = FileSystemUtilities.SanitizeFileName($"report-{summary.RunId}.json");
        var reportPath = Path.Combine(_config.OutputDirectory, summary.RunId, reportName);

        await FileSystemUtilities.WriteJsonFileAsync(reportPath, summary);

        _logger.LogInformation("Report written to {ReportPath}", reportPath);

        Activity.Current?.SetTag("report.path", reportPath);

        return reportPath;
    }

    public Task<string> PrepareProjectCopyAsync(PrepareProjectCopyInput input)
    {
        var platformName = input.Platform.ToString().ToLowerInvariant();
        var tempDir = Path.Combine(Path.GetTempPath(), "unity-builds", $"{input.RunId}-{platformName}");

        _logger.LogInformation("Cloning Unity project to {TempDir} for {Platform} build", tempDir, platformName);

        FileSystemUtilities.CopyDirectory(_config.UnityProjectPath, tempDir, excludeDirs: ["Temp"]);

        return Task.FromResult(tempDir);
    }

    public Task CleanupProjectCopyAsync(string projectCopyPath)
    {
        var expectedBase = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "unity-builds"));
        var fullPath = Path.GetFullPath(projectCopyPath);

        if (!fullPath.StartsWith(expectedBase, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Refusing to delete path outside temp directory: {Path}", projectCopyPath);
            return Task.CompletedTask;
        }

        if (Directory.Exists(fullPath))
        {
            _logger.LogInformation("Cleaning up cloned project at {Path}", fullPath);
            Directory.Delete(fullPath, recursive: true);
        }

        return Task.CompletedTask;
    }
}
