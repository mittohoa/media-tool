using System.IO;
using MediaTool.Core.Shell;
using Xunit;

namespace MediaTool.Tests;

/// <summary>
/// Which executable the right-click menu and the shortcuts get pointed at.
///
/// A wrong answer here is quiet and slow to notice: the menu keeps working until the folder
/// it names is rebuilt or cleaned, and only then does right-clicking a drive do nothing.
/// </summary>
public class ShellPathTests
{
    private static string Dir(params string[] parts) => Path.Combine(parts) + Path.DirectorySeparatorChar;

    [Fact]
    public void AnExecutableBesideTheToolWins()
    {
        string here = Dir("C:", "apps", "winnow");
        string expected = Path.Combine(here, ShellIntegration.AppExeName);

        string? found = ShellIntegration.FindAppExecutable(here, p => p == expected);

        Assert.Equal(expected, found);
    }

    [Fact]
    public void TheFallbackKeepsTheBuildConfigurationItWasCalledFrom()
    {
        // The bug this replaces named Debug outright, so installing from a Release build
        // registered the Debug executable.
        string releaseCli = Dir("C:", "repo", "src", "MediaTool.Cli", "bin", "Release", "net9.0-windows");
        string releaseApp = Path.Combine("C:", "repo", "src", "MediaTool.App", "bin", "Release",
            "net9.0-windows", ShellIntegration.AppExeName);

        string? found = ShellIntegration.FindAppExecutable(releaseCli, p => p == releaseApp);

        Assert.Equal(releaseApp, found);
    }

    [Fact]
    public void ADebugToolStillFindsTheDebugApp()
    {
        string debugCli = Dir("C:", "repo", "src", "MediaTool.Cli", "bin", "Debug", "net9.0-windows");
        string debugApp = Path.Combine("C:", "repo", "src", "MediaTool.App", "bin", "Debug",
            "net9.0-windows", ShellIntegration.AppExeName);

        string? found = ShellIntegration.FindAppExecutable(debugCli, p => p == debugApp);

        Assert.Equal(debugApp, found);
    }

    [Fact]
    public void ADebugToolIsNeverPointedAtAReleaseApp()
    {
        // Crossing configurations would register whichever tree happened to be built, which
        // is how the menu ends up naming a folder the user later cleans.
        string debugCli = Dir("C:", "repo", "src", "MediaTool.Cli", "bin", "Debug", "net9.0-windows");
        string releaseApp = Path.Combine("C:", "repo", "src", "MediaTool.App", "bin", "Release",
            "net9.0-windows", ShellIntegration.AppExeName);

        Assert.Null(ShellIntegration.FindAppExecutable(debugCli, p => p == releaseApp));
    }

    [Fact]
    public void NothingFoundIsReportedRatherThanGuessed()
    {
        string here = Dir("C:", "somewhere", "else");

        Assert.Null(ShellIntegration.FindAppExecutable(here, _ => false));
    }
}
