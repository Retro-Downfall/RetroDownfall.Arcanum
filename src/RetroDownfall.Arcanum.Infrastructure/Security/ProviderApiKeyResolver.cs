using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

/// <summary>
/// Composes the two supported provider-credential sources in a fixed order: the configured (or
/// derived) environment reference first, then the OS-backed secure store. Environment references
/// keep working exactly as before, so a per-process override always wins over stored state.
/// </summary>
public sealed class ProviderApiKeyResolver(
    IProviderCredentialStore credentialStore) : IProviderApiKeyResolver
{

    public async Task<string?> ResolveAsync(
        ProviderSettings provider,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(provider);

        if (EnvironmentCredentialResolver.ResolveProviderApiKey(provider) is { } fromEnvironment)
        {

            return fromEnvironment;

        }

        if (string.IsNullOrWhiteSpace(provider.Name))
        {

            return null;

        }

        SecretStoreReadResult stored = await credentialStore
            .GetApiKeyReadResultAsync(provider.Name, cancellationToken)
            .ConfigureAwait(false);

        return stored.Status == SecretStoreReadStatus.Ok
            && !string.IsNullOrWhiteSpace(stored.Value)
                ? stored.Value
                : null;

    }

}
