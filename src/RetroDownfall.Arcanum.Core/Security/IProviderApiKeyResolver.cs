using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Core.Security;

/// <summary>
/// Resolves the effective API key for one inference provider. The configured (or derived)
/// environment reference always wins so an operator can override a stored credential per process;
/// otherwise the OS-backed secure store is consulted. A missing or corrupt credential resolves to
/// <see langword="null"/> — keyless local providers are a supported configuration, not an error.
/// </summary>
public interface IProviderApiKeyResolver
{

    Task<string?> ResolveAsync(
        ProviderSettings provider,
        CancellationToken cancellationToken = default);

}

/// <summary>
/// Environment-only resolver used where no secure store is composed (tests, isolated tools). Keeps
/// the historical <see cref="EnvironmentCredentialResolver"/> behavior verbatim.
/// </summary>
public sealed class EnvironmentOnlyProviderApiKeyResolver : IProviderApiKeyResolver
{

    public static EnvironmentOnlyProviderApiKeyResolver Instance { get; } = new();

    public Task<string?> ResolveAsync(
        ProviderSettings provider,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(provider);

        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(EnvironmentCredentialResolver.ResolveProviderApiKey(provider));

    }

}
