namespace RetroDownfall.TheForge.Core.Services;

using RetroDownfall.TheForge.Core.Models;

/// <summary>
/// Round-trips <c>the-forge.json</c> with source-generated JSON. Path is injectable for tests.
/// </summary>
public interface ITheForgeSettingsStore
{

    string SettingsPath { get; }

    Task<TheForgeSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(TheForgeSettings settings, CancellationToken cancellationToken = default);

    Task SavePatchAsync(Func<TheForgeSettings, TheForgeSettings> patch, CancellationToken cancellationToken = default);

}
