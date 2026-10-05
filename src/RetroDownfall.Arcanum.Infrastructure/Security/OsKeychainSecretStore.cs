using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

/// <summary>
/// Master API key store that prefers the OS credential store (shared with The Forge), with a
/// one-time migrate from legacy Data Protection <c>security.dat</c> and an emergency DP fallback
/// when the OS store is unavailable.
/// The dedicated file-encryption key also uses the OS credential store and keeps a best-effort
/// Data Protection recovery mirror. Grimoire encryption secrets remain Data Protection–only.
/// Both mirrored credentials follow the one policy in <see cref="MirroredOsCredential"/>.
/// </summary>
public sealed class OsKeychainSecretStore : ISecretStore, IDisposable
{
    private readonly DataProtectionSecretStore _dataProtectionStore;

    private readonly IApiKeyDigestCache _apiKeyDigestCache;

    private readonly MirroredOsCredential _masterApiKey;

    private readonly MirroredOsCredential _fileEncryptionKey;

    private bool _disposed;

    public OsKeychainSecretStore(
        IOsCredentialStore osStore,
        DataProtectionSecretStore dataProtectionStore,
        IApiKeyDigestCache apiKeyDigestCache,
        ILogger<OsKeychainSecretStore>? logger = null)
        : this(osStore, dataProtectionStore, apiKeyDigestCache, logger, osReadTimeout: null)
    {
    }

    /// <param name="osReadTimeout">
    /// Bounds every OS credential read, startup included (<see cref="MirroredOsCredential.DefaultOsReadTimeout"/>
    /// when null). Tests shorten it.
    /// </param>
    internal OsKeychainSecretStore(
        IOsCredentialStore osStore,
        DataProtectionSecretStore dataProtectionStore,
        IApiKeyDigestCache apiKeyDigestCache,
        ILogger<OsKeychainSecretStore>? logger,
        TimeSpan? osReadTimeout)
    {
        ArgumentNullException.ThrowIfNull(osStore);

        _dataProtectionStore = dataProtectionStore ?? throw new ArgumentNullException(nameof(dataProtectionStore));

        _apiKeyDigestCache = apiKeyDigestCache ?? throw new ArgumentNullException(nameof(apiKeyDigestCache));

        // The mirror write never invalidates the canonical digest: a save invalidates once below,
        // and a read that only re-synchronizes the copy must leave the live digest in place.
        _masterApiKey = new MirroredOsCredential(
            osStore,
            ArcanumCredentialIdentity.MasterApiKeyAccount,
            new DataProtectionStoreCredentialMirror(
                static () => ArcanumPaths.ApiKeyStoreFile,
                dataProtectionStore.GetApiKeyReadResultAsync,
                dataProtectionStore.SaveApiKeyMirrorAsync),
            new MirroredCredentialPolicy(
                "the master API key",
                "Restore the credential before retrying.",
                SynchronizeMirrorFromOs: true,
                OsReadFailureWithoutMirrorIsCorrupt: true),
            logger,
            osReadTimeout);

        _fileEncryptionKey = new MirroredOsCredential(
            osStore,
            ArcanumCredentialIdentity.FileEncryptionKeyAccount,
            new DataProtectionStoreCredentialMirror(
                static () => ArcanumPaths.FileEncryptionKeyStoreFile,
                dataProtectionStore.GetFileEncryptionSecretReadResultAsync,
                dataProtectionStore.SaveFileEncryptionSecretAsync),
            new MirroredCredentialPolicy(
                "the file-encryption master key",
                "Restore the OS credential and backup before retrying.",
                MirrorRestoreFailureIsCorrupt: true,
                OsReadFailureWithoutMirrorIsCorrupt: true,
                RequireOsWrite: true),
            logger,
            osReadTimeout);
    }

    public void Dispose()
    {
        _disposed = true;

        _dataProtectionStore.Dispose();
    }

    public async Task<string?> GetApiKeyAsync()
    {
        SecretStoreReadResult result = await GetApiKeyReadResultAsync().ConfigureAwait(false);

        return result.Status == SecretStoreReadStatus.Ok ? result.Value : null;
    }

    /// <summary>
    /// Reads the master API key, migrating a <c>security.dat</c> mirror into an empty OS store and
    /// re-synchronizing the mirror from the canonical OS credential. The CLI reads that mirror first,
    /// so a normal launch opens Keychain only in the server process.
    /// </summary>
    public Task<SecretStoreReadResult> GetApiKeyReadResultAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return _masterApiKey.GetAsync(CancellationToken.None);
    }

    /// <summary>
    /// Reads the master API key without promoting the Data Protection fallback into OS storage.
    /// An OS read failure remains ambiguous even when the fallback is readable, because that
    /// fallback may have been superseded by a credential the process cannot currently inspect.
    /// </summary>
    public Task<SecretStoreReadResult> PeekApiKeyReadResultAsync() =>
        PeekApiKeyReadResultAsync(CancellationToken.None);

    /// <summary>
    /// The request-path peek. The OS read inside is bounded by the store's own timeout and fails
    /// closed when it expires; <paramref name="cancellationToken"/> lets a caller that no longer
    /// needs the answer stop waiting for it.
    /// </summary>
    public Task<SecretStoreReadResult> PeekApiKeyReadResultAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return _masterApiKey.PeekAsync(cancellationToken);
    }

    /// <inheritdoc />
    public bool ServesMasterApiKeyFromMirrorDuringOsFailure => _masterApiKey.ServingMirrorDuringOsFailure;

    public async Task SaveApiKeyAsync(string apiKey)
    {
        ArgumentNullException.ThrowIfNull(apiKey);

        ObjectDisposedException.ThrowIf(_disposed, this);

        await _masterApiKey.SaveAsync(apiKey, CancellationToken.None).ConfigureAwait(false);

        _apiKeyDigestCache.Invalidate();
    }

    public Task<string?> GetGrimoireEncryptionSecretAsync() =>
        _dataProtectionStore.GetGrimoireEncryptionSecretAsync();

    /// <summary>
    /// Forwards to <see cref="DataProtectionSecretStore.GetGrimoireEncryptionSecretReadResultAsync"/>.
    /// </summary>
    public Task<SecretStoreReadResult> GetGrimoireEncryptionSecretReadResultAsync() =>
        _dataProtectionStore.GetGrimoireEncryptionSecretReadResultAsync();

    public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) =>
        _dataProtectionStore.SaveGrimoireEncryptionSecretAsync(encryptionSecret);

    public Task<SecretStoreReadResult> GetFileEncryptionSecretReadResultAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return _fileEncryptionKey.GetAsync(CancellationToken.None);
    }

    /// <summary>
    /// Reads the dedicated file-encryption key without restoring its recovery mirror into OS
    /// storage. A failed OS read fails closed because the mirror may be stale.
    /// </summary>
    public Task<SecretStoreReadResult> PeekFileEncryptionSecretReadResultAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return _fileEncryptionKey.PeekAsync(CancellationToken.None);
    }

    public Task SaveFileEncryptionSecretAsync(string encryptionSecret)
    {
        ArgumentNullException.ThrowIfNull(encryptionSecret);

        ObjectDisposedException.ThrowIf(_disposed, this);

        return _fileEncryptionKey.SaveAsync(encryptionSecret, CancellationToken.None);
    }

    /// <summary>Removes the OS credential and its encrypted mirror, then drops the cached digest.</summary>
    public async Task DeleteApiKeyAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _masterApiKey.DeleteAsync(CancellationToken.None).ConfigureAwait(false);

        _apiKeyDigestCache.Invalidate();
    }

    /// <summary>
    /// Forwards to <see cref="DataProtectionSecretStore.DeleteGrimoireEncryptionSecretAsync"/>; the
    /// Grimoire secret has no OS copy.
    /// </summary>
    public Task DeleteGrimoireEncryptionSecretAsync() =>
        _dataProtectionStore.DeleteGrimoireEncryptionSecretAsync();

    /// <summary>Removes the OS credential and its encrypted mirror.</summary>
    public Task DeleteFileEncryptionSecretAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return _fileEncryptionKey.DeleteAsync(CancellationToken.None);
    }
}
