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

    Task SavePerplexityApiKeyAsync(
        string apiKey,
        CancellationToken cancellationToken = default);

    Task DeletePerplexityApiKeyAsync(
        CancellationToken cancellationToken = default);
}
