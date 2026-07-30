namespace RetroDownfall.Arcanum.Infrastructure.Intelligence.WebResearch;

/// <summary>
/// Resolves provider credentials at the invocation boundary so key rotation does not require a
/// process restart. Implementations must never log or cache returned key material indefinitely.
/// </summary>
public interface IWebResearchApiKeyResolver
{
    ValueTask<string?> ResolveApiKeyAsync(
        string providerName,
        CancellationToken cancellationToken = default);
}
