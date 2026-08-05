using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

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
/// segment used by <c>ARCANUM_PROVIDER_{NAME}_API_KEY</c>.
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

    internal const int MaxProtectedSecretBytes = 64 * 1024;

    private const string ProtectorPurpose = "Arcanum.Providers.InferenceApiKey";

    private readonly IOsCredentialStore _osStore;

    private readonly IDataProtector _protector;

    private readonly ILogger<ProviderCredentialStore>? _logger;

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates =
        new(StringComparer.Ordinal);

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

    public void Dispose()
    {

        foreach (SemaphoreSlim gate in _gates.Values)
        {

            gate.Dispose();

        }

        _gates.Clear();

    }

    public async Task<SecretStoreReadResult> GetApiKeyReadResultAsync(
        string providerName,
        CancellationToken cancellationToken = default)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);

        cancellationToken.ThrowIfCancellationRequested();

        string account = ArcanumCredentialIdentity.InferenceProviderApiKeyAccount(providerName);

        SemaphoreSlim gate = Gate(account);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {

            OsCredentialStoreResult os = _osStore.TryGet(
                ArcanumCredentialIdentity.Service,
                account);

            if (os.Status == OsCredentialStoreStatus.Ok
                && !string.IsNullOrWhiteSpace(os.Value))
            {

                return SecretStoreReadResult.Ok(os.Value);

            }

            if (os.Status == OsCredentialStoreStatus.Failed)
            {

                _logger?.LogWarning(
                    "OS credential store read failed for inference provider account {Account}.",
                    account);

            }

            SecretStoreReadResult fallback = await ReadMirrorAsync(
                    providerName,
                    cancellationToken)
                .ConfigureAwait(false);

            if (fallback.Status == SecretStoreReadStatus.Ok
                && !string.IsNullOrWhiteSpace(fallback.Value)
                && os.Status is OsCredentialStoreStatus.NotFound or OsCredentialStoreStatus.Ok)
            {

                OsCredentialStoreResult migrate = _osStore.Set(
                    ArcanumCredentialIdentity.Service,
                    account,
                    fallback.Value);

                if (migrate.Status == OsCredentialStoreStatus.Ok)
                {

                    _logger?.LogInformation(
                        "Migrated inference provider credential {Account} into the OS credential store.",
                        account);

                }
                else
                {

                    _logger?.LogWarning(
                        "Could not migrate inference provider credential {Account} into the OS "
                        + "credential store ({Status}); using the encrypted mirror.",
                        account,
                        migrate.Status);

                }

            }

            return fallback;

        }
        finally
        {

            _ = gate.Release();

        }

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

    public async Task SaveApiKeyAsync(
        string providerName,
        string apiKey,
        CancellationToken cancellationToken = default)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);

        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        cancellationToken.ThrowIfCancellationRequested();

        string account = ArcanumCredentialIdentity.InferenceProviderApiKeyAccount(providerName);

        string normalized = apiKey.Trim();

        SemaphoreSlim gate = Gate(account);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {

            OsCredentialStoreResult os = _osStore.Set(
                ArcanumCredentialIdentity.Service,
                account,
                normalized);

            if (os.Status != OsCredentialStoreStatus.Ok)
            {

                _logger?.LogWarning(
                    "OS credential store save failed for inference provider account {Account} "
                    + "({Status}); using the encrypted mirror.",
                    account,
                    os.Status);

            }

            try
            {

                await WriteMirrorAsync(providerName, normalized, cancellationToken)
                    .ConfigureAwait(false);

            }
            catch (Exception exception) when (os.Status == OsCredentialStoreStatus.Ok)
            {

                // The primary credential is safely stored. Keep serving while making the failed
                // emergency mirror visible without disclosing the credential.
                _logger?.LogWarning(
                    exception,
                    "OS credential save succeeded, but the encrypted mirror for {Account} failed.",
                    account);

            }

        }
        finally
        {

            _ = gate.Release();

        }

    }

    public async Task DeleteApiKeyAsync(
        string providerName,
        CancellationToken cancellationToken = default)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);

        cancellationToken.ThrowIfCancellationRequested();

        string account = ArcanumCredentialIdentity.InferenceProviderApiKeyAccount(providerName);

        SemaphoreSlim gate = Gate(account);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {

            OsCredentialStoreResult os = _osStore.Delete(
                ArcanumCredentialIdentity.Service,
                account);

            string path = ArcanumPaths.InferenceProviderApiKeyStoreFile(providerName);

            if (File.Exists(path))
            {

                File.Delete(path);

            }

            if (os.Status == OsCredentialStoreStatus.Failed)
            {

                throw new InvalidOperationException(
                    "The encrypted mirror was deleted, but the OS credential store could not "
                    + $"delete the credential for provider account {account}.");

            }

            if (os.Status == OsCredentialStoreStatus.Unavailable)
            {

                _logger?.LogWarning(
                    "The encrypted mirror for {Account} was deleted while the OS credential "
                    + "store was unavailable.",
                    account);

            }

        }
        finally
        {

            _ = gate.Release();

        }

    }

    private SemaphoreSlim Gate(string account) =>
        _gates.GetOrAdd(account, static _ => new SemaphoreSlim(1, 1));

    private async Task<SecretStoreReadResult> ReadMirrorAsync(
        string providerName,
        CancellationToken cancellationToken)
    {

        string path = ArcanumPaths.InferenceProviderApiKeyStoreFile(providerName);

        using SecureFileReadResult read = await SecureFileReader
            .ReadBytesAsync(path, MaxProtectedSecretBytes, cancellationToken)
            .ConfigureAwait(false);

        if (read.Status == SecureFileReadStatus.NotFound)
        {

            return SecretStoreReadResult.Missing();

        }

        if (read.Status != SecureFileReadStatus.Success)
        {

            return SecretStoreReadResult.Corrupted(CorruptMirrorMessage(providerName));

        }

        byte[] cipher = read.Bytes.ToArray();

        if (cipher.Length == 0)
        {

            CryptographicOperations.ZeroMemory(cipher);

            return SecretStoreReadResult.Corrupted(CorruptMirrorMessage(providerName));

        }

        try
        {

            byte[] plain = _protector.Unprotect(cipher);

            try
            {

                string value = Encoding.UTF8.GetString(plain);

                return string.IsNullOrWhiteSpace(value)
                    ? SecretStoreReadResult.Corrupted(CorruptMirrorMessage(providerName))
                    : SecretStoreReadResult.Ok(value);

            }
            finally
            {

                CryptographicOperations.ZeroMemory(plain);

            }

        }
        catch (CryptographicException)
        {

            return SecretStoreReadResult.Corrupted(CorruptMirrorMessage(providerName));

        }
        finally
        {

            CryptographicOperations.ZeroMemory(cipher);

        }

    }

    private async Task WriteMirrorAsync(
        string providerName,
        string apiKey,
        CancellationToken cancellationToken)
    {

        string path = ArcanumPaths.InferenceProviderApiKeyStoreFile(providerName);

        string directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Invalid provider credential store path.");

        SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(directory);

        byte[] plain = Encoding.UTF8.GetBytes(apiKey);

        byte[] cipher;

        try
        {

            cipher = _protector.Protect(plain);

        }
        finally
        {

            CryptographicOperations.ZeroMemory(plain);

        }

        string tempPath = path + ".tmp." + Guid.NewGuid().ToString("N");

        try
        {

            await using (FileStream stream =
                SecureFilePermissions.CreateOwnerOnlyTempFile(tempPath))
            {

                await stream.WriteAsync(cipher, cancellationToken).ConfigureAwait(false);

                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

            }

            File.Move(tempPath, path, overwrite: true);

            SecureFilePermissions.ApplyOwnerOnlyFile(path);

        }
        finally
        {

            CryptographicOperations.ZeroMemory(cipher);

            if (File.Exists(tempPath))
            {

                try
                {

                    File.Delete(tempPath);

                }
                catch (IOException)
                {

                    // Best-effort cleanup of an owner-only temporary file.

                }

            }

        }

    }

    private static string CorruptMirrorMessage(string providerName) =>
        $"{Path.GetFileName(ArcanumPaths.InferenceProviderApiKeyStoreFile(providerName))} is "
        + "present but could not be decrypted (corrupt or wrong Data Protection key ring). "
        + "Re-run 'arcanum setup' or 'arcanum key provider set' to store the credential again.";

}
