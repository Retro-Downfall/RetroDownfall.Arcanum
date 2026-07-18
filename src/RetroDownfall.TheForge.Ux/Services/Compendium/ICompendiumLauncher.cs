namespace RetroDownfall.TheForge.Ux.Services.Compendium;

/// <summary>Discovers and launches Compendium when installed or available in a development tree.</summary>
public interface ICompendiumLauncher
{

    /// <summary>Absolute path to <c>arcanum.json</c> that Compendium edits.</summary>
    string ConfigPath { get; }

    /// <summary>Attempts to start Compendium; never throws for missing binaries.</summary>
    CompendiumLaunchResult TryLaunch();

}
