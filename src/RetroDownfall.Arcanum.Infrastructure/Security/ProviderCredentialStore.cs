using System.Collections.Concurrent;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

/// <summary>
/// Inference-provider credential store that prefers the platform credential manager and keeps an
/// owner-only, Data Protection-encrypted mirror for headless hosts. One credential per provider
/// name; the account and mirror file name are both derived from the same normalized provider
/// segment used by <c>ARCANUM_PROVIDER_{NAME}_API_KEY</c>. The OS/mirror policy itself lives in
/// <see cref="MirroredOsCredential"/>.
/// </summary>
/// <remarks>
/// The .NET runtime cannot reliably zero an immutable managed <see cref="string"/>: the secret is
/// interned into a GC-managed allocation that may be copied by compaction before any explicit
/// clear. This store therefore minimizes credential lifetime and copies, and zeroes every
/// <c>byte[]</c> buffer it owns in a <c>finally</c>, but it does not claim to erase the managed
/// strings crossing the <see cref="IProviderCredentialStore"/> boundary.
/// </remarks>
public sealed class ProviderCredentialStore : IProviderCredentialStore, IDisposable
{
    internal const int MaxProtectedSecretBytes = ProtectedCredentialFile.MaxProtectedSecretBytes;

    private const string ProtectorPurpose = "Arcanum.Providers.InferenceApiKey";

    private readonly IOsCredentialStore _osStore;

    private readonly IDataProtector _protector;

    private readonly ILogger<ProviderCredentialStore>? _logger;

    private readonly ConcurrentDictionary<string, MirroredOsCredential> _credentials =
        new(StringComparer.Ordinal);

    private bool _disposed;

    public ProviderCredentialStore(
        IOsCredentialStore osStore,
        IDataProtectionProvider dataProtectionProvider,
        ILogger<ProviderCredentialStore>? logger = null)
    {
        _osStore = osStore ?? throw new ArgumentNullException(nameof(osStore));

        ArgumentNullException.ThrowIfNull(dataProtectionProvider);

        _protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);

        _logger = logger;
    }

    /// <summary>
    /// Marks the store disposed without disposing any per-account gate (see
    /// <see cref="MirroredOsCredential"/>): disposing one out from under an in-flight caller turned
    /// that caller's own release into an ObjectDisposedException that replaced whatever the operation
    /// actually returned. Only callers that arrive after Dispose are refused.
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
    }

    public Task<SecretStoreReadResult> GetApiKeyReadResultAsync(
        string providerName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);

        cancellationToken.ThrowIfCancellationRequested();

        return Credential(providerName).GetAsync(cancellationToken);
    }

    /// <summary>
    /// Resolves a provider credential without promoting its encrypted mirror into OS storage. A
    /// failed OS read is not interchangeable with absence: its hidden value may supersede the
    /// mirror, so the peek fails closed rather than returning a potentially stale credential.
    /// </summary>
    public Task<SecretStoreReadResult> PeekApiKeyReadResultAsync(
        string providerName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);

        cancellationToken.ThrowIfCancellationRequested();

        return Credential(providerName).PeekAsync(cancellationToken);
    }

    /// <summary>
    /// Presence/status only: the resolved credential is discarded inside this method so callers that
    /// need a yes/no answer never take a reference to the secret.
    /// </summary>
    public async Task<bool> HasApiKeyAsync(
        string providerName,
        CancellationToken cancellationToken = default)
    {
        SecretStoreReadResult result = await GetApiKeyReadResultAsync(
                providerName,
                cancellationToken)
            .ConfigureAwait(false);

        return result.Status == SecretStoreReadStatus.Ok
            && !string.IsNullOrWhiteSpace(result.Value);
    }

    public Task SaveApiKeyAsync(
        string providerName,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);

        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        cancellationToken.ThrowIfCancellationRequested();

        return Credential(providerName).SaveAsync(apiKey.Trim(), cancellationToken);
    }

    public Task DeleteApiKeyAsync(
        string providerName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);

        cancellationToken.ThrowIfCancellationRequested();

        return Credential(providerName).DeleteAsync(cancellationToken);
    }

    private MirroredOsCredential Credential(string providerName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        string account = ArcanumCredentialIdentity.InferenceProviderApiKeyAccount(providerName);

        return _credentials.GetOrAdd(
            account,
            static (account, state) => new MirroredOsCredential(
                state.Store._osStore,
                account,
                new ProtectedFileCredentialMirror(
                    () => ArcanumPaths.InferenceProviderApiKeyStoreFile(state.ProviderName),
                    state.Store._protector,
                    CorruptMirrorMessage(state.ProviderName)),
                new MirroredCredentialPolicy(
                    $"provider account {account}",
                    "Restore the credential before retrying."),
                state.Store._logger),
            (Store: this, ProviderName: providerName));
    }

    private static string CorruptMirrorMessage(string providerName) =>
        $"{Path.GetFileName(ArcanumPaths.InferenceProviderApiKeyStoreFile(providerName))} is "
        + "present but could not be decrypted (corrupt or wrong Data Protection key ring). "
        + "Re-run 'arcanum setup' or 'arcanum key provider set' to store the credential again.";
}
