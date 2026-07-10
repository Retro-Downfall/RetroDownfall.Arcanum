namespace RetroDownfall.TheForge.Core.Services;

using RetroDownfall.TheForge.Core.Models;

/// <summary>
/// Round-trips <c>forge.json</c> with source-generated JSON. Path is injectable for tests.
/// </summary>
public interface IForgeSettingsStore
{

    string SettingsPath { get; }

    Task<ForgeSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(ForgeSettings settings, CancellationToken cancellationToken = default);

    Task SavePatchAsync(Func<ForgeSettings, ForgeSettings> patch, CancellationToken cancellationToken = default);

}
