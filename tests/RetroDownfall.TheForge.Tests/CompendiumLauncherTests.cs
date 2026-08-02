using System.Diagnostics;

using RetroDownfall.TheForge.Ux;

using RetroDownfall.Arcanum.Core.Desktop;

using Xunit;

namespace RetroDownfall.TheForge.Tests;

public class CompendiumLauncherTests
{

    [Fact]
    public void TryLaunch_WhenExecutableExists_StartsProcess()
    {

        string fileName = OperatingSystem.IsWindows()
            ? "RetroDownfall.Compendium.Ux.exe"
            : "RetroDownfall.Compendium.Ux";

        string exe = Path.Combine(Path.GetTempPath(), fileName);

        ProcessStartInfo? started = null;

        CompendiumLauncher launcher = new(
            () => Path.GetTempPath(),
            path => string.Equals(path, exe, StringComparison.Ordinal),
            psi =>
            {
                started = psi;
                return true;
            });

        CompendiumLaunchResult result = launcher.TryLaunch();

        Assert.True(result.Launched);

        Assert.NotNull(started);

        Assert.Equal(exe, started!.FileName);

        Assert.Contains("arcanum.json", result.ConfigPath, StringComparison.Ordinal);

    }

    [Fact]

    public void TryLaunch_WhenExecutableExists_SendsSettingsDeepLinkAsOneArgument()
    {

        string fileName = OperatingSystem.IsWindows()
            ? "RetroDownfall.Compendium.Ux.exe"
            : "RetroDownfall.Compendium.Ux";

        string executable = Path.Combine(Path.GetTempPath(), fileName);

        ProcessStartInfo? started = null;

        CompendiumLauncher launcher = new(
            () => Path.GetTempPath(),
            path => string.Equals(path, executable, StringComparison.Ordinal),
            startInfo =>
            {

                started = startInfo;

                return true;

            });

        CompendiumLaunchResult result = launcher.TryLaunch();

        Assert.True(result.Launched);

        Assert.NotNull(started);

        Assert.Equal(executable, started!.FileName);

        Assert.False(started.UseShellExecute);

        Assert.True(string.IsNullOrEmpty(started.Arguments));

        Assert.Equal(2, started.ArgumentList.Count);

        Assert.Equal(ApplicationDeepLinkCodec.ArgumentName, started.ArgumentList[0]);

        ApplicationDeepLink deepLink = ApplicationDeepLinkCodec.Decode(
            started.ArgumentList[1]);

        Assert.Equal(
            new ApplicationDeepLink(
                ApplicationDeepLink.CurrentSchemaVersion,
                DesktopApplication.Compendium,
                ApplicationResourceKind.Configuration,
                InitialView: ApplicationInitialView.Settings),
            deepLink);

        Assert.DoesNotContain(
            "apiKey",
            started.ArgumentList[1],
            StringComparison.OrdinalIgnoreCase);

    }

    [Fact]

    public void TryLaunch_WhenExecutableStartFails_ContinuesToDevelopmentProject()
    {

        string repositoryRoot = Path.Combine(
            Path.GetTempPath(),
            "arcanum-compendium-continuation");

        string baseDirectory = Path.Combine(
            repositoryRoot,
            "artifacts",
            "bin");

        string fileName = OperatingSystem.IsWindows()
            ? "RetroDownfall.Compendium.Ux.exe"
            : "RetroDownfall.Compendium.Ux";

        string executable = Path.Combine(baseDirectory, fileName);

        string projectPath = Path.Combine(
            repositoryRoot,
            CompendiumLauncher.ProjectRelativePath);

        List<ProcessStartInfo> starts = [];

        CompendiumLauncher launcher = new(
            () => baseDirectory,
            path =>
                string.Equals(path, executable, StringComparison.Ordinal)
                || string.Equals(path, projectPath, StringComparison.Ordinal),
            startInfo =>
            {

                starts.Add(startInfo);

                return string.Equals(
                    startInfo.FileName,
                    "dotnet",
                    StringComparison.Ordinal);

            });

        CompendiumLaunchResult result = launcher.TryLaunch();

        Assert.True(result.Launched);

        Assert.Equal(projectPath, result.ExecutablePath);

        Assert.Equal(2, starts.Count);

        Assert.Equal(executable, starts[0].FileName);

        Assert.Equal("dotnet", starts[1].FileName);

        Assert.False(starts[1].UseShellExecute);

        Assert.Equal(
            [
                "run",
                "--project",
                projectPath,
                "--",
                ApplicationDeepLinkCodec.ArgumentName,
                ApplicationDeepLinkCodec.Encode(CreateSettingsDeepLink()),
            ],
            starts[1].ArgumentList.ToArray());

    }

    [Fact]

    public void TryLaunch_FindsDevelopmentProjectBeyondEightParentDirectories()
    {

        string repositoryRoot = Path.Combine(
            Path.GetTempPath(),
            "arcanum-compendium-deep-root");

        string baseDirectory = repositoryRoot;

        for (int index = 0; index < 16; index++)
        {

            baseDirectory = Path.Combine(baseDirectory, $"nested-{index}");

        }

        string projectPath = Path.Combine(
            repositoryRoot,
            CompendiumLauncher.ProjectRelativePath);

        ProcessStartInfo? started = null;

        CompendiumLauncher launcher = new(
            () => baseDirectory,
            path => string.Equals(path, projectPath, StringComparison.Ordinal),
            startInfo =>
            {

                started = startInfo;

                return true;

            });

        CompendiumLaunchResult result = launcher.TryLaunch();

        Assert.True(result.Launched);

        Assert.Equal(projectPath, result.ExecutablePath);

        Assert.NotNull(started);

        Assert.Equal("dotnet", started!.FileName);

    }

    [Fact]

    public void TryLaunch_WhenDevelopmentStartFails_UsesRepositoryRelativeGuidance()
    {

        string repositoryRoot = Path.Combine(
            Path.GetTempPath(),
            "arcanum-compendium-relative-guidance");

        string baseDirectory = Path.Combine(repositoryRoot, "artifacts", "bin");

        string projectPath = Path.Combine(
            repositoryRoot,
            CompendiumLauncher.ProjectRelativePath);

        CompendiumLauncher launcher = new(
            () => baseDirectory,
            path => string.Equals(path, projectPath, StringComparison.Ordinal),
            _ => false);

        CompendiumLaunchResult result = launcher.TryLaunch();

        Assert.False(result.Launched);

        Assert.Equal(projectPath, result.ExecutablePath);

        Assert.Contains(
            $"dotnet run --project {CompendiumLauncher.ProjectRelativePath}",
            result.Message,
            StringComparison.Ordinal);

        Assert.DoesNotContain(
            projectPath,
            result.Message,
            StringComparison.Ordinal);

        Assert.Contains(result.ConfigPath, result.Message, StringComparison.Ordinal);

        string deepLinkPayload = ApplicationDeepLinkCodec.Encode(
            CreateSettingsDeepLink());

        Assert.Contains(
            $"{ApplicationDeepLinkCodec.ArgumentName} {CommandDisplayFormatter.QuoteArgumentForCurrentPlatform(deepLinkPayload)}",
            result.Message,
            StringComparison.Ordinal);

    }

    [Fact]
    public void TryLaunch_WhenMissing_ReturnsConfigPathInstructions()
    {

        string baseDirectory = Path.GetTempPath();

        CompendiumLauncher launcher = new(
            () => baseDirectory,
            _ => false,
            _ => false);

        CompendiumLaunchResult result = launcher.TryLaunch();

        Assert.False(result.Launched);

        Assert.Contains("arcanum.json", result.Message, StringComparison.Ordinal);

        Assert.Contains("arcanum.json", result.ConfigPath, StringComparison.Ordinal);

        Assert.Contains(
            "arcanum config edit",
            result.Message,
            StringComparison.Ordinal);

        string fileName = OperatingSystem.IsWindows()
            ? "RetroDownfall.Compendium.Ux.exe"
            : "RetroDownfall.Compendium.Ux";

        Assert.Contains(
            $"Executable: {Path.Combine(baseDirectory, fileName)}",
            result.Message,
            StringComparison.Ordinal);

        Assert.Contains(
            $"DevelopmentProject: {CompendiumLauncher.ProjectRelativePath}",
            result.Message,
            StringComparison.Ordinal);

    }

    [Fact]
    public void TryLaunch_DoesNotPersistApiKeys()
    {

        CompendiumLaunchResult result = new FakeCompendiumLauncher().TryLaunch();

        Assert.DoesNotContain("secret", result.Message, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("apiKey", result.ConfigPath, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]

    public void Mac_settings_action_launches_compendium()
    {

        FakeCompendiumLauncher launcher = new();

        CompendiumLaunchResult result = App.OpenSettings(launcher);

        Assert.True(result.Launched);

        Assert.Equal(1, launcher.LaunchCount);

    }

    private static ApplicationDeepLink CreateSettingsDeepLink() =>
        new(
            ApplicationDeepLink.CurrentSchemaVersion,
            DesktopApplication.Compendium,
            ApplicationResourceKind.Configuration,
            InitialView: ApplicationInitialView.Settings);

}
