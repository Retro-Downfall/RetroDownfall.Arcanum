namespace RetroDownfall.TheForge.Core.Services;

/// <summary>
/// Supplies the master API key for outbound Arcanum HTTP calls. Implementations cache the resolved
/// value in memory after the first successful resolution.
/// </summary>
public interface ITheForgeApiKeyProvider
{

    /// <summary>
    /// Returns the master API key, or <see langword="null"/> when none could be resolved (caller
    /// should surface an auth error / paste prompt).
    /// </summary>
    Task<string?> GetApiKeyAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Stores a user-pasted key into the OS credential store and updates the in-memory cache.
    /// </summary>
    Task PersistPastedKeyAsync(string apiKey, CancellationToken cancellationToken);

}
