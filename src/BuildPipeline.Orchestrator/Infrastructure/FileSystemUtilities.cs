using System.Linq;
using System.Text.Json;

namespace BuildPipeline.Orchestrator.Infrastructure;

public static class FileSystemUtilities
{
    public static void EnsureDirectory(string path) => Directory.CreateDirectory(path);

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
        }, cancellationToken);
    }
}
