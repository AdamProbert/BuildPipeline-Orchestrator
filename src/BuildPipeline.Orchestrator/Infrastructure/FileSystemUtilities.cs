using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BuildPipeline.Orchestrator.Infrastructure;

public static class FileSystemUtilities
{
    public static void EnsureDirectory(string path) => Directory.CreateDirectory(path);

    public static void CopyDirectory(string source, string destination, IEnumerable<string>? excludeDirs = null)
    {
        var excludeSet = new HashSet<string>(excludeDirs ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);

        Directory.CreateDirectory(destination);

        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);

        foreach (var dir in Directory.GetDirectories(source))
        {
            var dirName = Path.GetFileName(dir);
            if (!excludeSet.Contains(dirName))
                CopyDirectory(dir, Path.Combine(destination, dirName), excludeDirs);
        }
    }

    /// <summary>
    /// Creates a hybrid copy: junctions for read-only directories, hard copies for the rest.
    /// Falls back to full copy if junction creation fails.
    /// </summary>
    public static void CopyDirectoryHybrid(
        string source,
        string destination,
        IEnumerable<string>? junctionDirs = null,
        IEnumerable<string>? excludeDirs = null)
    {
        var junctionSet = new HashSet<string>(junctionDirs ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var excludeSet = new HashSet<string>(excludeDirs ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);

        Directory.CreateDirectory(destination);

        // Copy root-level files
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);

        foreach (var dir in Directory.GetDirectories(source))
        {
            var dirName = Path.GetFileName(dir);
            if (excludeSet.Contains(dirName))
                continue;

            var destDir = Path.Combine(destination, dirName);

            if (junctionSet.Contains(dirName))
            {
                if (!TryCreateJunction(destDir, dir))
                    CopyDirectory(dir, destDir); // fallback to full copy
            }
            else
            {
                CopyDirectory(dir, destDir, excludeDirs);
            }
        }
    }

    /// <summary>Creates an NTFS directory junction. Returns false on non-Windows or failure.</summary>
    public static bool TryCreateJunction(string junctionPath, string targetPath)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return false;

        var fullTarget = Path.GetFullPath(targetPath);
        if (!Directory.Exists(fullTarget))
            return false;

        // Junction must not already exist
        if (Directory.Exists(junctionPath) || File.Exists(junctionPath))
            return false;

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{junctionPath}\" \"{fullTarget}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var process = System.Diagnostics.Process.Start(psi);
            process?.WaitForExit(10_000);
            return process?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Returns true if the given path is an NTFS junction (reparse point).</summary>
    public static bool IsJunction(string path)
    {
        var info = new DirectoryInfo(path);
        return info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint);
    }

    public static string SanitizeFileName(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        return new string(value.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray());
    }

    public static async Task WriteJsonFileAsync<T>(string path, T payload, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        }, cancellationToken);
    }
}
