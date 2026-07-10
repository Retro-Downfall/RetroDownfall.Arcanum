using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

/// <summary>
/// Master API key store that prefers the OS credential store (shared with The Forge), with a
/// one-time migrate from legacy Data Protection <c>security.dat</c> and an emergency DP fallback
/// when the OS store is unavailable.
/// Grimoire encryption secrets remain Data Protection–only.
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

            if (os.Status == OsCredentialStoreStatus.Unavailable)
            {

                return SecretStoreReadResult.Missing();

            }

            return SecretStoreReadResult.Missing();

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

            await _dataProtectionStore.SaveApiKeyAsync(apiKey).ConfigureAwait(false);

            _apiKeyDigestCache.Invalidate();

        }
        finally
        {

            _gate.Release();

        }

    }

    public Task<string?> GetGrimoireEncryptionSecretAsync() =>
        _dataProtectionStore.GetGrimoireEncryptionSecretAsync();

    public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) =>
        _dataProtectionStore.SaveGrimoireEncryptionSecretAsync(encryptionSecret);

}
