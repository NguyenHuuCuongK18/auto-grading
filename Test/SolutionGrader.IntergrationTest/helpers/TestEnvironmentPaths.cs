using System;
using System.IO;

namespace SolutionGrader.IntergrationTest.Helpers;

internal static class TestEnvironmentPaths
{
    public static string SolutionRoot = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\..\.."));

    public static string GetUiExecutablePath()
    {
        var configuration = GetBuildConfiguration();
        var exePath = Path.Combine(SolutionRoot,
            "Application",
            "SolutionGrader.UI",
            "bin",
            configuration,
            "net8.0-windows",
            "SolutionGrader.UI.exe");

        if (!File.Exists(exePath))
        {
            throw new FileNotFoundException($"SolutionGrader.UI executable not found at '{exePath}'. Build the UI project before running integration tests.");
        }

        return exePath;
    }

    public static string GetSampleTestKitFolder()
    {
        var folder = Path.Combine(SolutionRoot,
            "Test",
            "SolutionGrader.IntergrationTest",
            "resources",
            "TestKitSample");

        if (!Directory.Exists(folder))
        {
            throw new DirectoryNotFoundException($"Sample test kit folder not found at '{folder}'.");
        }

        return folder;
    }

    private static string GetBuildConfiguration()
    {
        var baseDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        var configurationDirectory = baseDirectory.Parent;
        if (configurationDirectory == null)
        {
            throw new InvalidOperationException("Unable to determine build configuration from the current test directory.");
        }

        return configurationDirectory.Name;
    }
}
