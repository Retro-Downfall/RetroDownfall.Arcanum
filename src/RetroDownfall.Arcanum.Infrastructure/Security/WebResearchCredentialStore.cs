using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

/// <summary>
/// Perplexity credential store that prefers the platform credential manager and
/// keeps an owner-only, Data Protection-encrypted fallback for headless hosts.
/// </summary>
public sealed class WebResearchCredentialStore : IWebResearchCredentialStore, IDisposable
{
    internal const int MaxProtectedSecretBytes = 64 * 1024;

    private const string ProtectorPurpose = "Arcanum.WebResearch.PerplexityApiKey";

    private const string CorruptCredentialMessage =
        "perplexity-key.dat is present but could not be decrypted "
        + "(corrupt or wrong Data Protection key ring).";

    private readonly IOsCredentialStore _osStore;

    private readonly IDataProtector _protector;

    private readonly ILogger<WebResearchCredentialStore>? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WebResearchCredentialStore(
        IOsCredentialStore osStore,
        IDataProtectionProvider dataProtectionProvider,
        ILogger<WebResearchCredentialStore>? logger = null)
    {
        _osStore = osStore ?? throw new ArgumentNullException(nameof(osStore));
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        _protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
        _logger = logger;
    }

    public void Dispose() => _gate.Dispose();

    public async Task<SecretStoreReadResult> GetPerplexityApiKeyReadResultAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            OsCredentialStoreResult os = _osStore.TryGet(
                ArcanumCredentialIdentity.Service,
                ArcanumCredentialIdentity.PerplexityApiKeyAccount);

            if (os.Status == OsCredentialStoreStatus.Ok
                && !string.IsNullOrWhiteSpace(os.Value))
            {
                return SecretStoreReadResult.Ok(os.Value);
            }

            if (os.Status == OsCredentialStoreStatus.Failed)
            {
                _logger?.LogWarning(
                    "OS credential store read failed for the Perplexity provider.");
            }

            SecretStoreReadResult fallback =
                await ReadFallbackAsync(cancellationToken).ConfigureAwait(false);

            if (fallback.Status == SecretStoreReadStatus.Ok
                && !string.IsNullOrWhiteSpace(fallback.Value)
                && os.Status is OsCredentialStoreStatus.NotFound or OsCredentialStoreStatus.Ok)
            {
                OsCredentialStoreResult migrate = _osStore.Set(
                    ArcanumCredentialIdentity.Service,
                    ArcanumCredentialIdentity.PerplexityApiKeyAccount,
                    fallback.Value);

                if (migrate.Status == OsCredentialStoreStatus.Ok)
                {
                    _logger?.LogInformation(
                        "Migrated the Perplexity provider credential into the OS credential store.");
                }
                else
                {
                    _logger?.LogWarning(
                        "Could not migrate the Perplexity provider credential into the OS "
                        + "credential store ({Status}); using the encrypted fallback.",
                        migrate.Status);
                }
            }

            return fallback;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Resolves the Perplexity credential without promoting its encrypted fallback into OS storage.
    /// A failed OS read fails closed because the hidden credential may supersede the fallback.
    /// </summary>
    public async Task<SecretStoreReadResult> PeekPerplexityApiKeyReadResultAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            OsCredentialStoreResult os = _osStore.TryGet(
                ArcanumCredentialIdentity.Service,
                ArcanumCredentialIdentity.PerplexityApiKeyAccount);

            if (os.Status == OsCredentialStoreStatus.Ok
                && !string.IsNullOrWhiteSpace(os.Value))
            {
                return SecretStoreReadResult.Ok(os.Value);
            }

            if (os.Status == OsCredentialStoreStatus.Failed)
            {
                return SecretStoreReadResult.Corrupted(
                    "OS key storage failed while peeking at the Perplexity provider credential. "
                    + (os.Message ?? "Restore the credential before retrying."));
            }

            return await ReadFallbackAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SavePerplexityApiKeyAsync(
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        cancellationToken.ThrowIfCancellationRequested();

        string normalized = apiKey.Trim();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            OsCredentialStoreResult os = _osStore.Set(
                ArcanumCredentialIdentity.Service,
                ArcanumCredentialIdentity.PerplexityApiKeyAccount,
                normalized);

            if (os.Status != OsCredentialStoreStatus.Ok)
            {
                _logger?.LogWarning(
                    "OS credential store save failed for the Perplexity provider "
                    + "({Status}); using the encrypted fallback.",
                    os.Status);

                PurgeSupersededOsCredential(os);
            }

            try
            {
                await WriteFallbackAsync(normalized, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (os.Status == OsCredentialStoreStatus.Ok)
            {
                // The primary credential is safely stored. Keep serving while making the
                // failed emergency mirror visible without disclosing the credential.
                _logger?.LogWarning(
                    ex,
                    "OS credential save succeeded, but the encrypted Perplexity fallback failed.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeletePerplexityApiKeyAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            OsCredentialStoreResult os = _osStore.Delete(
                ArcanumCredentialIdentity.Service,
                ArcanumCredentialIdentity.PerplexityApiKeyAccount);

            string path = ArcanumPaths.PerplexityApiKeyStoreFile;

            if (File.Exists(path))
            {
                File.Delete(path);
            }

            if (os.Status == OsCredentialStoreStatus.Failed)
            {
                throw new InvalidOperationException(
                    "The encrypted fallback was deleted, but the OS credential store "
                    + "could not delete the Perplexity credential.");
            }

            if (os.Status == OsCredentialStoreStatus.Unavailable)
            {
                _logger?.LogWarning(
                    "The encrypted Perplexity fallback was deleted while the OS "
                    + "credential store was unavailable.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Reads prefer the OS credential over the encrypted fallback, so a failed OS write has to take
    /// the superseded credential with it — otherwise the replaced credential keeps being used and
    /// the newly stored one never takes effect. When the credential can neither be replaced nor
    /// removed the save fails closed, before the fallback is rewritten, rather than reporting a
    /// replacement it did not perform.
    /// </summary>
    private void PurgeSupersededOsCredential(OsCredentialStoreResult save)
    {
        OsCredentialStoreResult purge = _osStore.Delete(
            ArcanumCredentialIdentity.Service,
            ArcanumCredentialIdentity.PerplexityApiKeyAccount);

        if (purge.Status is OsCredentialStoreStatus.Ok or OsCredentialStoreStatus.NotFound)
        {
            return;
        }

        if (!_osStore.IsAvailable)
        {
            // No reachable backend at all: the encrypted fallback is the documented operating mode
            // here, and every read in this state resolves through it.
            _logger?.LogWarning(
                "The OS credential store is unavailable; the encrypted Perplexity fallback was "
                + "written without reconciling any earlier OS credential.");

            return;
        }

        throw new InvalidOperationException(
            "The Perplexity credential could not be written to the OS credential store "
            + $"({save.Status}), and the superseded OS credential could not be removed "
            + $"({purge.Status}). The previous credential would keep being used, so nothing "
            + "was changed.");
    }

    private async Task<SecretStoreReadResult> ReadFallbackAsync(
        CancellationToken cancellationToken)
    {
        string path = ArcanumPaths.PerplexityApiKeyStoreFile;

        using SecureFileReadResult read = await SecureFileReader
            .ReadBytesAsync(
                path,
                MaxProtectedSecretBytes,
                cancellationToken)
            .ConfigureAwait(false);

        if (read.Status == SecureFileReadStatus.NotFound)
        {
            return SecretStoreReadResult.Missing();
        }

        if (read.Status != SecureFileReadStatus.Success)
        {
            return SecretStoreReadResult.Corrupted(CorruptCredentialMessage);
        }

        byte[] cipher = read.Bytes.ToArray();

        if (cipher.Length == 0)
        {
            CryptographicOperations.ZeroMemory(cipher);

            return SecretStoreReadResult.Corrupted(CorruptCredentialMessage);
        }

        try
        {
            byte[] plain = _protector.Unprotect(cipher);

            try
            {
                string value = Encoding.UTF8.GetString(plain);

                return string.IsNullOrWhiteSpace(value)
                    ? SecretStoreReadResult.Corrupted(CorruptCredentialMessage)
                    : SecretStoreReadResult.Ok(value);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
        catch (CryptographicException)
        {
            return SecretStoreReadResult.Corrupted(CorruptCredentialMessage);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(cipher);
        }
    }

    private async Task WriteFallbackAsync(
        string apiKey,
        CancellationToken cancellationToken)
    {
        string path = ArcanumPaths.PerplexityApiKeyStoreFile;
        string directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Invalid Perplexity credential store path.");

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

                // Durable flush before the atomic replace so an unclean power loss cannot leave a
                // present-but-empty fallback behind the committed rename.
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, path, overwrite: true);
            SecureFilePermissions.ApplyOwnerOnlyFile(path);
        }
        finally
        {
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
}
