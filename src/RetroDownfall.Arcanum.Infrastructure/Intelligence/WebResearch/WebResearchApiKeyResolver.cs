using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Intelligence.WebResearch;

/// <summary>
/// Resolves a Perplexity key at call time. An explicitly referenced environment
/// variable takes precedence over the local secure store for unattended hosts.
/// </summary>
public sealed class WebResearchApiKeyResolver(
    IOptionsMonitor<ArcanumSettings> settings,
    IWebResearchCredentialStore credentialStore) : IWebResearchApiKeyResolver
{
    public async ValueTask<string?> ResolveApiKeyAsync(
        string providerName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.Equals(
                providerName,
                WebResearchConstants.PerplexityProviderName,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        WebBrowsingSettings webResearch = settings.CurrentValue.ResolveWebBrowsing();
        string? environmentCredential =
            EnvironmentCredentialResolver.ResolveWebResearchApiKey(webResearch);

        if (!string.IsNullOrWhiteSpace(environmentCredential))
        {
            return environmentCredential;
        }

        SecretStoreReadResult stored = await credentialStore
            .GetPerplexityApiKeyReadResultAsync(cancellationToken)
            .ConfigureAwait(false);

        return stored.Status == SecretStoreReadStatus.Ok
            && !string.IsNullOrWhiteSpace(stored.Value)
                ? stored.Value
                : null;
    }
}
