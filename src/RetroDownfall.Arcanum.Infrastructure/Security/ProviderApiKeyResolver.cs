using System.Security.Cryptography;
using System.Text;
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
    private readonly SemaphoreSlim _identityGate = new(1, 1);

    private readonly Guid _bootId = Guid.NewGuid();

    private readonly byte[] _comparisonKey = RandomNumberGenerator.GetBytes(32);

    private readonly Dictionary<(string Account, string Variable), ObservedCredential> _observed = [];

    private long _generation;

    /// <summary>
    /// Captures the exact credential used by a client with an opaque process-local identity.
    /// External changes wholly between observations cannot be detected. Local successful writes
    /// through the shared store invalidate even an unobserved A-B-A transition.
    /// </summary>
    public async Task<ResolvedProviderCredential> ResolveWithIdentityAsync(
        ProviderSettings provider,
        CancellationToken cancellationToken = default) => (await CaptureIdentityAsync(provider, false, null, cancellationToken).ConfigureAwait(false)).Credential;

    public async Task<bool> IsCurrentAsync(
        ProviderSettings provider,
        ProviderCredentialStamp stamp,
        CancellationToken cancellationToken = default)
    {
        return await GetCurrentnessAsync(provider, stamp, cancellationToken).ConfigureAwait(false) == ProviderCredentialCurrentness.Current;
    }

    public async Task<ProviderCredentialCurrentness> GetCurrentnessAsync(
        ProviderSettings provider,
        ProviderCredentialStamp stamp,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stamp);

        return (await CaptureIdentityAsync(provider, true, stamp, cancellationToken).ConfigureAwait(false)).Currentness;
    }

    private async Task<(ResolvedProviderCredential Credential, ProviderCredentialCurrentness Currentness)> CaptureIdentityAsync(
        ProviderSettings provider,
        bool peek,
        ProviderCredentialStamp? expected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);

        // Capture source selection before the await; mutable configuration must not change the
        // source name attached to an already-read secret.
        string providerName = provider.Name;

        string variable = EnvironmentCredentialResolver.GetProviderApiKeyEnvironmentVariableName(provider);

        var source = (EnvironmentCredentialResolver.NormalizeProviderName(providerName), variable);

        await _identityGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            long before = LocalGeneration(providerName);

            string? secret = EnvironmentCredentialResolver.ResolveProviderApiKey(new ProviderSettings
            {
                Name = providerName,

                CredentialEnvironmentVariable = variable,
            });

            bool fromEnvironment = secret is not null;

            bool known = fromEnvironment;

            if (!fromEnvironment && !string.IsNullOrWhiteSpace(providerName))
            {
                SecretStoreReadResult result;

                bool identityProven = true;

                if (!peek && credentialStore is ProviderCredentialStore productionStore)
                {
                    MirroredCredentialRead read = await productionStore
                        .GetApiKeyWithProofAsync(providerName, cancellationToken).ConfigureAwait(false);

                    result = read.Result;

                    identityProven = read.IdentityProven;
                }
                else
                {
                    result = peek
                        ? await credentialStore.PeekApiKeyReadResultAsync(providerName, cancellationToken).ConfigureAwait(false)
                        : await credentialStore.GetApiKeyReadResultAsync(providerName, cancellationToken).ConfigureAwait(false);
                }

                known = identityProven && (result.Status == SecretStoreReadStatus.Missing
                    || (result.Status == SecretStoreReadStatus.Ok && !string.IsNullOrWhiteSpace(result.Value)));

                secret = result.Status == SecretStoreReadStatus.Ok && !string.IsNullOrWhiteSpace(result.Value)
                    ? result.Value
                    : null;
            }

            long after = LocalGeneration(providerName);

            if (!known || before != after)
            {
                // A failed OS peek cannot hide a local owner mutation or an already superseded
                // stamp. Classify from this gated observation before discarding comparison state.
                bool stale = before != after || expected is not null
                    && (!_observed.TryGetValue(source, out ObservedCredential? prior)
                        || prior.Stamp != expected || prior.LocalGeneration != after);

                Forget(source);

                return (new(secret, null), stale ? ProviderCredentialCurrentness.ConfirmedStale : ProviderCredentialCurrentness.Unavailable);
            }

            byte[] bytes = Encoding.UTF8.GetBytes(secret ?? string.Empty);

            byte[] comparison;

            try
            {
                comparison = HMACSHA256.HashData(_comparisonKey, bytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }

            if (_observed.TryGetValue(source, out ObservedCredential? previous)
                && previous.LocalGeneration == after
                && previous.FromEnvironment == fromEnvironment
                && previous.Keyless == (secret is null)
                && CryptographicOperations.FixedTimeEquals(previous.Comparison, comparison))
            {
                CryptographicOperations.ZeroMemory(comparison);

                return (new(secret, previous.Stamp), previous.Stamp == expected
                    ? ProviderCredentialCurrentness.Current : ProviderCredentialCurrentness.ConfirmedStale);
            }

            Forget(source);

            ProviderCredentialStamp stamp = new(_bootId, Guid.NewGuid(), checked(++_generation));

            _observed[source] = new(comparison, after, fromEnvironment, secret is null, stamp);

            return (new(secret, stamp), ProviderCredentialCurrentness.ConfirmedStale);
        }
        finally
        {
            _identityGate.Release();
        }
    }

    private long LocalGeneration(string providerName) =>
        credentialStore is ProviderCredentialStore local && !string.IsNullOrWhiteSpace(providerName)
            ? local.MutationGeneration(providerName)
            : 0;

    private void Forget((string Account, string Variable) source)
    {
        if (_observed.Remove(source, out ObservedCredential? previous))
        {
            CryptographicOperations.ZeroMemory(previous.Comparison);
        }
    }

    // Only private keyed comparison material is retained. It never leaves the resolver and cannot
    // be used as a provider identifier, cache key, authentication decision, or diagnostic label.
    private sealed class ObservedCredential(
        byte[] comparison,
        long localGeneration,
        bool fromEnvironment,
        bool keyless,
        ProviderCredentialStamp stamp)
    {
        internal byte[] Comparison { get; } = comparison;

        internal long LocalGeneration { get; } = localGeneration;

        internal bool FromEnvironment { get; } = fromEnvironment;

        internal bool Keyless { get; } = keyless;

        internal ProviderCredentialStamp Stamp { get; } = stamp;
    }

    public async Task<string?> ResolveAsync(
        ProviderSettings provider,
        CancellationToken cancellationToken = default)
        => await ResolveCoreAsync(provider, peek: false, cancellationToken).ConfigureAwait(false);

    public async Task<string?> PeekAsync(
        ProviderSettings provider,
        CancellationToken cancellationToken = default)
        => await ResolveCoreAsync(provider, peek: true, cancellationToken).ConfigureAwait(false);

    private async Task<string?> ResolveCoreAsync(
        ProviderSettings provider,
        bool peek,
        CancellationToken cancellationToken)
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

        SecretStoreReadResult stored = peek
            ? await credentialStore
                .PeekApiKeyReadResultAsync(provider.Name, cancellationToken)
                .ConfigureAwait(false)
            : await credentialStore
                .GetApiKeyReadResultAsync(provider.Name, cancellationToken)
                .ConfigureAwait(false);

        return stored.Status == SecretStoreReadStatus.Ok
            && !string.IsNullOrWhiteSpace(stored.Value)
                ? stored.Value
                : null;
    }
}
