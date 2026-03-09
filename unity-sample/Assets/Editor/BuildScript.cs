using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

public static class BuildScript
{
    private const string PlatformArgument = "-buildPlatform";
    private const string OutputArgument = "-buildOutput";

    [MenuItem("Build/Build for Platform")]
    public static void BuildForPlatform()
    {
        var args = Environment.GetCommandLineArgs();

        var platformIndex = Array.IndexOf(args, PlatformArgument);
        var outputIndex = Array.IndexOf(args, OutputArgument);

        if (platformIndex == -1 || platformIndex + 1 >= args.Length)
        {
            Debug.LogError("Platform not specified. Use -buildPlatform <android|ios>");
            if (Application.isBatchMode) EditorApplication.Exit(1);
            return;
        }

        var platformArg = args[platformIndex + 1].ToLowerInvariant();
        var outputPath = outputIndex != -1 && outputIndex + 1 < args.Length
            ? args[outputIndex + 1]
            : GetDefaultOutputPath(platformArg);

        BuildTarget buildTarget;
        string extension;

        switch (platformArg)
        {
            case "android":
                buildTarget = BuildTarget.Android;
                extension = ".apk";
                break;
            case "ios":
                buildTarget = BuildTarget.iOS;
                extension = "";
                break;
            default:
                Debug.LogError($"Unsupported platform: {platformArg}. Supported: android, ios");
                if (Application.isBatchMode) EditorApplication.Exit(1);
                return;
        }

        Debug.Log($"Building for platform: {platformArg}");
        Debug.Log($"Output path: {outputPath}");

        var buildPlayerOptions = new BuildPlayerOptions
        {
            scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray(),
            locationPathName = outputPath + extension,
            target = buildTarget,
            options = BuildOptions.None
        };

        var report = BuildPipeline.BuildPlayer(buildPlayerOptions);
        var summary = report.summary;

        if (summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded)
        {
            Debug.Log($"Build succeeded: {summary.totalSize} bytes");
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError($"Build failed: {summary.result}");
            if (Application.isBatchMode) EditorApplication.Exit(1);
        }
    }

    private static string GetDefaultOutputPath(string platform)
    {
        var projectPath = Application.dataPath.Replace("/Assets", "");
        return $"{projectPath}/Builds/{platform}/build";
    }
}
