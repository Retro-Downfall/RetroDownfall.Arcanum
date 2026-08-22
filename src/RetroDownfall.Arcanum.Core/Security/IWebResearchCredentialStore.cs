namespace RetroDownfall.Arcanum.Core.Security;

/// <summary>
/// Secure storage for credentials used by native web-research providers.
/// Implementations must never expose credential values through configuration,
/// diagnostics, or logs.
/// </summary>
public interface IWebResearchCredentialStore
{
    Task<SecretStoreReadResult> GetPerplexityApiKeyReadResultAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the Perplexity credential without migrating, repairing, or persisting credential
    /// state. The default preserves compatibility for stores whose ordinary read is already pure;
    /// stores whose ordinary read can mutate state must override it.
    /// </summary>
    Task<SecretStoreReadResult> PeekPerplexityApiKeyReadResultAsync(
        CancellationToken cancellationToken = default) =>
        GetPerplexityApiKeyReadResultAsync(cancellationToken);

    Task SavePerplexityApiKeyAsync(
        string apiKey,
        CancellationToken cancellationToken = default);

    Task DeletePerplexityApiKeyAsync(
        CancellationToken cancellationToken = default);
}
