using System.Diagnostics;
using RetroDownfall.TheForge.Ux.Services.Compendium;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public class CompendiumLauncherTests
{

    [Fact]
    public void TryLaunch_WhenExecutableExists_StartsProcess()
    {

        string exe = Path.Combine(Path.GetTempPath(), "RetroDownfall.Compendium.Ux");

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
    public void TryLaunch_WhenMissing_ReturnsConfigPathInstructions()
    {

        CompendiumLauncher launcher = new(
            () => Path.GetTempPath(),
            _ => false,
            _ => false);

        CompendiumLaunchResult result = launcher.TryLaunch();

        Assert.False(result.Launched);

        Assert.Contains("arcanum.json", result.Message, StringComparison.Ordinal);

        Assert.Contains("arcanum.json", result.ConfigPath, StringComparison.Ordinal);

    }

    [Fact]
    public void TryLaunch_DoesNotPersistApiKeys()
    {

        CompendiumLaunchResult result = new FakeCompendiumLauncher().TryLaunch();

        Assert.DoesNotContain("secret", result.Message, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("apiKey", result.ConfigPath, StringComparison.OrdinalIgnoreCase);

    }

}
