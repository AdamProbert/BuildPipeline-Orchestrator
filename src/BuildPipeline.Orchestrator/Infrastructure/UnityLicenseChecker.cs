using System.Runtime.InteropServices;

namespace BuildPipeline.Orchestrator.Infrastructure;

public static class UnityLicenseChecker
{
    /// <summary>
    /// Returns the expected Unity license file path for the current OS.
    /// Paths are per the Unity documentation:
    /// https://docs.unity3d.com/6000.2/Documentation/Manual/ActivationFAQ.html
    /// </summary>
    public static string? GetLicenseFilePath()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Serial-based: %PROGRAMDATA%\Unity\Unity_lic.ulf
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var serialPath = Path.Combine(programData, "Unity", "Unity_lic.ulf");
            if (File.Exists(serialPath))
                return serialPath;

            // Named user: %LOCALAPPDATA%\Unity\licenses\UnityEntitlementLicense.xml
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "Unity", "licenses", "UnityEntitlementLicense.xml");
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            const string serialPath = "/Library/Application Support/Unity/Unity_lic.ulf";
            if (File.Exists(serialPath))
                return serialPath;

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Unity", "licenses", "UnityEntitlementLicense.xml");
        }

        // Linux
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var serialPath = Path.Combine(home, ".local", "share", "unity3d", "Unity", "Unity_lic.ulf");
            if (File.Exists(serialPath))
                return serialPath;

            return Path.Combine(home, ".config", "unity3d", "Unity", "licenses", "UnityEntitlementLicense.xml");
        }
    }
}
