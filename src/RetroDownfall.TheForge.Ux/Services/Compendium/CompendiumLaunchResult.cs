namespace RetroDownfall.TheForge.Ux.Services.Compendium;

/// <summary>Outcome of attempting to launch Compendium from The Forge.</summary>
public sealed record CompendiumLaunchResult(
    bool Launched,
    string? ExecutablePath,
    string ConfigPath,
    string Message);
