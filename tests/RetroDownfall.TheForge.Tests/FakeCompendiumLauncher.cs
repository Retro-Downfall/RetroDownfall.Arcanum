using System.Diagnostics;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.TheForge.Ux.Services.Compendium;

namespace RetroDownfall.TheForge.Tests;

internal sealed class FakeCompendiumLauncher : ICompendiumLauncher
{

    public bool LaunchSucceeded { get; set; } = true;

    public string? LastMessage { get; private set; }

    public int LaunchCount { get; private set; }

    public string ConfigPath { get; } = Path.Combine(ArcanumPaths.GrimoireDirectory, "arcanum.json");

    public CompendiumLaunchResult TryLaunch()
    {

        LaunchCount++;

        LastMessage = LaunchSucceeded
            ? "Opened Compendium."
            : $"Compendium was not found. Edit {ConfigPath}.";

        return new CompendiumLaunchResult(LaunchSucceeded, LaunchSucceeded ? "/tmp/compendium" : null, ConfigPath, LastMessage);

    }

}
