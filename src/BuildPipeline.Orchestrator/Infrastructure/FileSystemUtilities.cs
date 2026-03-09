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
