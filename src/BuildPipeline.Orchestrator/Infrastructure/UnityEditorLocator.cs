using System.Runtime.InteropServices;

namespace BuildPipeline.Orchestrator.Infrastructure;

public static class UnityEditorLocator
{
    /// <summary>
    /// Resolves the Unity editor executable path for a given editor version.
    /// Probes Unity Hub's default install locations per-platform.
    /// Returns null if no matching installation is found.
    /// </summary>
    /// Side Note: In a production system, this is likely not required but it helps out with local development!
    public static string? Resolve(string editorVersion)
    {
        foreach (var candidate in GetCandidatePaths(editorVersion))
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static IEnumerable<string> GetCandidatePaths(string version)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Unity", "Hub", "Editor", version, "Editor", "Unity.exe");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            yield return Path.Combine(
                "/Applications", "Unity", "Hub", "Editor", version,
                "Unity.app", "Contents", "MacOS", "Unity");
        }
        else
        {
            // Linux
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Unity", "Hub", "Editor", version, "Editor", "Unity");
        }
    }
}
