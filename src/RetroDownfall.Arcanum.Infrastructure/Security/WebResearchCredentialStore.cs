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

    private async Task<SecretStoreReadResult> ReadFallbackAsync(
        CancellationToken cancellationToken)
    {
        string path = ArcanumPaths.PerplexityApiKeyStoreFile;

        if (!File.Exists(path))
        {
            return SecretStoreReadResult.Missing();
        }

        byte[] cipher = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);

        if (cipher.Length == 0)
        {
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
