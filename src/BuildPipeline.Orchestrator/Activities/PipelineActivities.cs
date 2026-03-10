using System.Collections.Concurrent;
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
        var logFile = Path.Combine(projectPath, "Logs", $"build-{platformName}.log");
        FileSystemUtilities.EnsureDirectory(Path.GetDirectoryName(logFile)!);
        var args = $"-quit -batchmode -nographics " +
                   $"-projectPath \"{projectPath}\" " +
                   $"-logFile \"{logFile}\" " +
                   $"-executeMethod BuildScript.BuildForPlatform " +
                   $"-buildPlatform {platformName} " +
                   $"-buildOutput \"{artifactPath}\"";

        var timeouts = input.Timeouts ?? TimeoutConfig.Default;
        var maxLicensingRetries = timeouts.LicensingMaxRetries;
        var licensingDelay = timeouts.LicensingRetryDelay ?? TimeSpan.FromSeconds(30);
        var ct = Temporalio.Activities.ActivityExecutionContext.Current.CancellationToken;

        var issues = new ConcurrentBag<PipelineIssue>();

        for (var attempt = 1; attempt <= maxLicensingRetries + 1; attempt++)
        {
            var (exitCode, stdout, stderr) = await RunUnityProcessAsync(unityPath, args, logFile, issues, platformName, ct);

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

                return new BuildArtifactResult(input.Platform, artifactPath + extension, DateTimeOffset.UtcNow, issues.ToList());
            }

            var isLicensingError = IsLicensingError(stdout, stderr);

            if (isLicensingError && attempt <= maxLicensingRetries)
            {
                _logger.LogWarning(
                    "Unity licensing error on attempt {Attempt}/{MaxAttempts}. Retrying in {Delay}s...",
                    attempt, maxLicensingRetries + 1, licensingDelay.TotalSeconds);
                await Task.Delay(licensingDelay, ct);
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

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunUnityProcessAsync(string unityPath, string args, string logFilePath, ConcurrentBag<PipelineIssue> issues, string platformName, CancellationToken ct)
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

        // Tail the Unity editor log file in the background, forwarding lines through
        // ILogger so they inherit the current Activity trace/span IDs.
        using var tailCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tailTask = TailUnityLogAsync(logFilePath, issues, platformName, tailCts.Token);

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Activity cancelled — killing Unity process tree (PID {Pid})", process.Id);
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            // Wait for the process to fully exit so it releases file handles (e.g. build log)
            // before the cleanup activity tries to delete the project copy.
            try { await process.WaitForExitAsync(); } catch { }
            tailCts.Cancel();
            try { await tailTask; } catch (OperationCanceledException) { }
            throw;
        }

        // Give the tail a moment to flush remaining lines, then stop
        await Task.Delay(500);
        tailCts.Cancel();
        try { await tailTask; } catch (OperationCanceledException) { }

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private async Task TailUnityLogAsync(string logFilePath, ConcurrentBag<PipelineIssue> issues, string platformName, CancellationToken ct)
    {
        // Wait for Unity to create the log file
        for (var i = 0; i < 60 && !File.Exists(logFilePath); i++)
        {
            await Task.Delay(500, ct);
        }

        if (!File.Exists(logFilePath))
        {
            _logger.LogWarning("Unity log file not created after 30s: {Path}", logFilePath);
            return;
        }

        using var stream = new FileStream(logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line != null)
            {
                // Classify Unity log lines by severity
                if (line.Contains("Error", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("Crash", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogError("[Unity] {Line}", line);
                    issues.Add(new PipelineIssue(IssueSeverity.Error, line, platformName));
                }
                else if (line.Contains("Warning", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("[Unity] {Line}", line);
                    issues.Add(new PipelineIssue(IssueSeverity.Warning, line, platformName));
                }
                else
                    _logger.LogInformation("[Unity] {Line}", line);

                // Heartbeat Temporal so long-running builds don't time out
                Temporalio.Activities.ActivityExecutionContext.Current.Heartbeat();
            }
            else
            {
                // No new data — poll interval
                await Task.Delay(1000, ct);
            }
        }
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

        switch (_config.CopyStrategy)
        {
            case ProjectCopyStrategy.Junction:
                _logger.LogInformation(
                    "Cloning Unity project to {TempDir} for {Platform} build (hybrid copy with junctions)",
                    tempDir, platformName);
                FileSystemUtilities.CopyDirectoryHybrid(
                    _config.UnityProjectPath, tempDir,
                    junctionDirs: _config.JunctionDirs,
                    excludeDirs: ["Temp"]);
                break;

            default:
                _logger.LogInformation(
                    "Cloning Unity project to {TempDir} for {Platform} build (full copy)",
                    tempDir, platformName);
                FileSystemUtilities.CopyDirectory(
                    _config.UnityProjectPath, tempDir,
                    excludeDirs: ["Temp"]);
                break;
        }

        return Task.FromResult(tempDir);
    }

    public async Task CleanupProjectCopyAsync(string projectCopyPath)
    {
        var expectedBase = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "unity-builds"));
        var fullPath = Path.GetFullPath(projectCopyPath);

        if (!fullPath.StartsWith(expectedBase, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Refusing to delete path outside temp directory: {Path}", projectCopyPath);
            return;
        }

        if (!Directory.Exists(fullPath))
            return;

        _logger.LogInformation("Cleaning up cloned project at {Path}", fullPath);
        // Remove junctions first so recursive delete doesn't follow them into original dirs
        RemoveJunctions(fullPath);

        // Retry deletion — killed Unity child processes may still be releasing file handles
        const int maxAttempts = 5;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                Directory.Delete(fullPath, recursive: true);
                return;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                _logger.LogWarning(
                    "Cleanup attempt {Attempt}/{Max} failed (files still locked), retrying in {Delay}s",
                    attempt, maxAttempts, attempt);
                await Task.Delay(TimeSpan.FromSeconds(attempt));
            }
        }
    }

    private static void RemoveJunctions(string directory)
    {
        foreach (var subDir in Directory.GetDirectories(directory))
        {
            if (FileSystemUtilities.IsJunction(subDir))
            {
                // Delete the junction reparse point itself (does not follow into target)
                Directory.Delete(subDir, false);
            }
        }
    }
}
