using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

/// <summary>
/// Master API key store that prefers the OS credential store (shared with The Forge), with a
/// one-time migrate from legacy Data Protection <c>security.dat</c> and an emergency DP fallback
/// when the OS store is unavailable.
/// The dedicated file-encryption key also uses the OS credential store and keeps a best-effort
/// Data Protection recovery mirror. Grimoire encryption secrets remain Data Protection–only.
/// </summary>
public sealed class OsKeychainSecretStore : ISecretStore, IDisposable
{

    private readonly IOsCredentialStore _osStore;

    private readonly DataProtectionSecretStore _dataProtectionStore;

    private readonly IApiKeyDigestCache _apiKeyDigestCache;

    private readonly ILogger<OsKeychainSecretStore>? _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);

    public OsKeychainSecretStore(
        IOsCredentialStore osStore,
        DataProtectionSecretStore dataProtectionStore,
        IApiKeyDigestCache apiKeyDigestCache,
        ILogger<OsKeychainSecretStore>? logger = null)
    {

        _osStore = osStore ?? throw new ArgumentNullException(nameof(osStore));

        _dataProtectionStore = dataProtectionStore ?? throw new ArgumentNullException(nameof(dataProtectionStore));

        _apiKeyDigestCache = apiKeyDigestCache ?? throw new ArgumentNullException(nameof(apiKeyDigestCache));

        _logger = logger;

    }

    public void Dispose()
    {

        _gate.Dispose();

        _dataProtectionStore.Dispose();

    }

    public async Task<string?> GetApiKeyAsync()
    {

        SecretStoreReadResult result = await GetApiKeyReadResultAsync().ConfigureAwait(false);

        return result.Status == SecretStoreReadStatus.Ok ? result.Value : null;

    }

    public async Task<SecretStoreReadResult> GetApiKeyReadResultAsync()
    {

        await _gate.WaitAsync().ConfigureAwait(false);

        try
        {

            OsCredentialStoreResult os = _osStore.TryGet(
                ArcanumCredentialIdentity.Service,
                ArcanumCredentialIdentity.MasterApiKeyAccount);

            if (os.Status == OsCredentialStoreStatus.Ok && !string.IsNullOrWhiteSpace(os.Value))
            {

                return SecretStoreReadResult.Ok(os.Value);

            }

            if (os.Status is OsCredentialStoreStatus.Failed)
            {

                _logger?.LogWarning("OS credential store read failed: {Message}", os.Message);

            }

            SecretStoreReadResult legacy = await _dataProtectionStore.GetApiKeyReadResultAsync().ConfigureAwait(false);

            if (legacy.Status == SecretStoreReadStatus.Ok && !string.IsNullOrWhiteSpace(legacy.Value))
            {

                if (os.Status is OsCredentialStoreStatus.NotFound or OsCredentialStoreStatus.Ok)
                {

                    OsCredentialStoreResult migrate = _osStore.Set(
                        ArcanumCredentialIdentity.Service,
                        ArcanumCredentialIdentity.MasterApiKeyAccount,
                        legacy.Value!);

                    if (migrate.Status == OsCredentialStoreStatus.Ok)
                    {

                        _logger?.LogInformation(
                            "Migrated master API key from security.dat into the OS credential store ({Service}/{Account}).",
                            ArcanumCredentialIdentity.Service,
                            ArcanumCredentialIdentity.MasterApiKeyAccount);

                    }
                    else
                    {

                        _logger?.LogWarning(
                            "Could not migrate master API key into the OS credential store: {Message}. Using security.dat fallback.",
                            migrate.Message);

                    }

                }
                else if (os.Status == OsCredentialStoreStatus.Unavailable)
                {

                    _logger?.LogWarning(
                        "OS credential store unavailable ({Message}); using legacy security.dat for the master API key.",
                        os.Message);

                }

                return legacy;

            }

            if (legacy.Status == SecretStoreReadStatus.Corrupted)
            {

                return legacy;

            }

            // A read that FAILED leaves the credential's existence unknown, so it must not collapse to
            // Missing: Missing is the one status that authorises minting a replacement, and minting
            // overwrites — or, when the write fails too, deletes — whatever credential is still there.
            // Unavailable is different: the backend is absent, so nothing of ours can be living in it.
            if (os.Status == OsCredentialStoreStatus.Failed)
            {

                return SecretStoreReadResult.Corrupted(
                    "OS key storage failed while reading the master API key. "
                    + (os.Message ?? "Restore the credential before retrying."));

            }

            return SecretStoreReadResult.Missing();

        }
        finally
        {

            _gate.Release();

        }

    }

    /// <summary>
    /// Reads the master API key without promoting the Data Protection fallback into OS storage.
    /// An OS read failure remains ambiguous even when the fallback is readable, because that
    /// fallback may have been superseded by a credential the process cannot currently inspect.
    /// </summary>
    public async Task<SecretStoreReadResult> PeekApiKeyReadResultAsync()
    {

        await _gate.WaitAsync().ConfigureAwait(false);

        try
        {

            OsCredentialStoreResult os = _osStore.TryGet(
                ArcanumCredentialIdentity.Service,
                ArcanumCredentialIdentity.MasterApiKeyAccount);

            if (os.Status == OsCredentialStoreStatus.Ok
                && !string.IsNullOrWhiteSpace(os.Value))
            {

                return SecretStoreReadResult.Ok(os.Value);

            }

            if (os.Status == OsCredentialStoreStatus.Failed)
            {

                return SecretStoreReadResult.Corrupted(
                    "OS key storage failed while peeking at the master API key. "
                    + (os.Message ?? "Restore the credential before retrying."));

            }

            SecretStoreReadResult fallback = await _dataProtectionStore
                .GetApiKeyReadResultAsync()
                .ConfigureAwait(false);

            return NormalizePeekFallback(fallback);

        }
        finally
        {

            _gate.Release();

        }

    }

    public async Task SaveApiKeyAsync(string apiKey)
    {

        ArgumentNullException.ThrowIfNull(apiKey);

        await _gate.WaitAsync().ConfigureAwait(false);

        try
        {

            OsCredentialStoreResult os = _osStore.Set(
                ArcanumCredentialIdentity.Service,
                ArcanumCredentialIdentity.MasterApiKeyAccount,
                apiKey);

            if (os.Status == OsCredentialStoreStatus.Ok)
            {

                _apiKeyDigestCache.Invalidate();

                // Best-effort mirror to security.dat so emergency fallback stays usable.
                try
                {

                    await _dataProtectionStore.SaveApiKeyAsync(apiKey).ConfigureAwait(false);

                }
                catch (Exception ex)
                {

                    _logger?.LogWarning(ex, "OS keychain save succeeded but security.dat mirror failed.");

                }

                return;

            }

            _logger?.LogWarning(
                "OS credential store save failed ({Status}: {Message}); falling back to security.dat.",
                os.Status,
                os.Message);

            PurgeSupersededOsCredential(os);

            await _dataProtectionStore.SaveApiKeyAsync(apiKey).ConfigureAwait(false);

            _apiKeyDigestCache.Invalidate();

        }
        finally
        {

            _gate.Release();

        }

    }

    /// <summary>
    /// Reads prefer the OS credential over security.dat, so a failed OS write has to take the
    /// superseded credential with it — otherwise the replaced key keeps authenticating and the newly
    /// stored one never takes effect. When the credential can neither be replaced nor removed the
    /// save fails closed rather than reporting a rotation it did not perform.
    /// </summary>
    private void PurgeSupersededOsCredential(OsCredentialStoreResult save)
    {

        OsCredentialStoreResult purge = _osStore.Delete(
            ArcanumCredentialIdentity.Service,
            ArcanumCredentialIdentity.MasterApiKeyAccount);

        if (purge.Status is OsCredentialStoreStatus.Ok or OsCredentialStoreStatus.NotFound)
        {

            return;

        }

        if (!_osStore.IsAvailable)
        {

            // No reachable backend at all: security.dat is the documented operating mode here, and
            // every read in this state resolves through it.
            _logger?.LogWarning(
                "OS key storage is unavailable ({Message}); the master API key was written to "
                + "security.dat without reconciling any earlier OS credential.",
                purge.Message);

            return;

        }

        throw new InvalidOperationException(
            $"The master API key could not be written to OS key storage ({save.Status}), and the "
            + $"superseded OS credential could not be removed ({purge.Status}): "
            + (purge.Message ?? "no detail reported.")
            + " The previous key would keep authenticating, so nothing was changed.");

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

    public async Task<SecretStoreReadResult> GetFileEncryptionSecretReadResultAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            OsCredentialStoreResult os = _osStore.TryGet(
                ArcanumCredentialIdentity.Service,
                ArcanumCredentialIdentity.FileEncryptionKeyAccount);
            if (os.Status == OsCredentialStoreStatus.Ok
                && !string.IsNullOrWhiteSpace(os.Value))
            {
                return SecretStoreReadResult.Ok(os.Value);
            }

            if (os.Status == OsCredentialStoreStatus.Failed)
            {
                _logger?.LogWarning(
                    "OS credential store file-encryption key read failed: {Message}",
                    os.Message);
            }

            SecretStoreReadResult mirror = await _dataProtectionStore
                .GetFileEncryptionSecretReadResultAsync()
                .ConfigureAwait(false);
            if (mirror.Status == SecretStoreReadStatus.Ok
                && !string.IsNullOrWhiteSpace(mirror.Value))
            {
                if (os.Status is OsCredentialStoreStatus.NotFound
                    or OsCredentialStoreStatus.Ok)
                {
                    OsCredentialStoreResult migrate = _osStore.Set(
                        ArcanumCredentialIdentity.Service,
                        ArcanumCredentialIdentity.FileEncryptionKeyAccount,
                        mirror.Value);
                    if (migrate.Status != OsCredentialStoreStatus.Ok)
                    {
                        return SecretStoreReadResult.Corrupted(
                            "The file-encryption key mirror exists, but it could not be restored "
                            + $"to OS key storage: {migrate.Message}");
                    }
                }

                return mirror;
            }

            if (mirror.Status == SecretStoreReadStatus.Corrupted)
            {
                return mirror;
            }

            if (os.Status == OsCredentialStoreStatus.Failed)
            {
                return SecretStoreReadResult.Corrupted(
                    "OS key storage failed while reading the file-encryption master key. "
                    + (os.Message ?? "Restore the OS credential and backup before retrying."));
            }

            return SecretStoreReadResult.Missing();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Reads the dedicated file-encryption key without restoring its recovery mirror into OS
    /// storage. A failed OS read fails closed because the mirror may be stale.
    /// </summary>
    public async Task<SecretStoreReadResult> PeekFileEncryptionSecretReadResultAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            OsCredentialStoreResult os = _osStore.TryGet(
                ArcanumCredentialIdentity.Service,
                ArcanumCredentialIdentity.FileEncryptionKeyAccount);
            if (os.Status == OsCredentialStoreStatus.Ok
                && !string.IsNullOrWhiteSpace(os.Value))
            {
                return SecretStoreReadResult.Ok(os.Value);
            }

            if (os.Status == OsCredentialStoreStatus.Failed)
            {
                return SecretStoreReadResult.Corrupted(
                    "OS key storage failed while peeking at the file-encryption master key. "
                    + (os.Message ?? "Restore the OS credential and backup before retrying."));
            }

            SecretStoreReadResult fallback = await _dataProtectionStore
                .GetFileEncryptionSecretReadResultAsync()
                .ConfigureAwait(false);

            return NormalizePeekFallback(fallback);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveFileEncryptionSecretAsync(string encryptionSecret)
    {
        ArgumentNullException.ThrowIfNull(encryptionSecret);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            OsCredentialStoreResult os = _osStore.Set(
                ArcanumCredentialIdentity.Service,
                ArcanumCredentialIdentity.FileEncryptionKeyAccount,
                encryptionSecret);
            if (os.Status != OsCredentialStoreStatus.Ok)
            {
                throw new InvalidOperationException(
                    "The dedicated file-encryption key could not be saved to OS key storage: "
                    + (os.Message ?? os.Status.ToString()));
            }

            try
            {
                await _dataProtectionStore.SaveFileEncryptionSecretAsync(encryptionSecret)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(
                    ex,
                    "OS file-encryption key save succeeded but the Data Protection recovery mirror failed.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static SecretStoreReadResult NormalizePeekFallback(SecretStoreReadResult fallback) =>
        fallback.Status == SecretStoreReadStatus.Ok
        && string.IsNullOrWhiteSpace(fallback.Value)
            ? SecretStoreReadResult.Missing()
            : fallback;

}
