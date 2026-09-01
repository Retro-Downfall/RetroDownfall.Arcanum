using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

public sealed class DataProtectionSecretStore(
    IDataProtectionProvider dataProtectionProvider,
    IApiKeyDigestCache apiKeyDigestCache) : ISecretStore, IDisposable
{

    internal const int MaxProtectedSecretBytes = 64 * 1024;

    private const string ProtectorPurpose = "Arcanum.Core.ApiKey";

    private const string GrimoireProtectorPurpose = "Arcanum.Core.GrimoireEncryption";

    private const string FileEncryptionProtectorPurpose = "Arcanum.Core.FileEncryption.v1";

    private const string CorruptApiKeyRecoveryMessage =
        "security.dat is present but could not be decrypted (corrupt or wrong Data Protection key ring). "
        + "Stop the host, then follow the \"Local Grimoire reinstall\" guidance in docs/Arcanum.Engineering.md: remove both security.dat and the Grimoire .db under ~/.config/arcanum/, or restore from backup. "
        + "Do not delete the Grimoire database alone if you need to keep session data.";

    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);

    private readonly IDataProtector _grimoireProtector = dataProtectionProvider.CreateProtector(GrimoireProtectorPurpose);

    private readonly IDataProtector _fileEncryptionProtector =
        dataProtectionProvider.CreateProtector(FileEncryptionProtectorPurpose);

    private readonly SemaphoreSlim _fileLock = new(1, 1);

    private static string StorePath => ArcanumPaths.ApiKeyStoreFile;

    private static string GrimoireStorePath => ArcanumPaths.GrimoireKeyStoreFile;

    private static string FileEncryptionStorePath => ArcanumPaths.FileEncryptionKeyStoreFile;

    public void Dispose() => _fileLock.Dispose();

    public async Task<string?> GetApiKeyAsync()
    {

        SecretStoreReadResult result = await GetApiKeyReadResultAsync().ConfigureAwait(false);

        return result.Status == SecretStoreReadStatus.Ok ? result.Value : null;

    }

    public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
        ReadProtectedResultAsync(StorePath, _protector, corruptMessage: CorruptApiKeyRecoveryMessage);

    public async Task SaveApiKeyAsync(string apiKey)
    {
        ArgumentNullException.ThrowIfNull(apiKey);

        await _fileLock.WaitAsync().ConfigureAwait(false);

        try
        {

            await WriteProtectedAsync(StorePath, apiKey, _protector).ConfigureAwait(false);

            apiKeyDigestCache.Invalidate();

        }
        finally
        {

            _fileLock.Release();

        }

    }

    public async Task<string?> GetGrimoireEncryptionSecretAsync()
    {

        SecretStoreReadResult result = await GetGrimoireEncryptionSecretReadResultAsync().ConfigureAwait(false);

        return result.Status == SecretStoreReadStatus.Ok ? result.Value : null;

    }

    /// <summary>
    /// Like <see cref="GetGrimoireEncryptionSecretAsync"/> but preserves
    /// <see cref="SecretStoreReadStatus.Corrupted"/> so callers can refuse a silent
    /// API-key fallback when the sealed secret is present but undecryptable.
    /// </summary>
    public Task<SecretStoreReadResult> GetGrimoireEncryptionSecretReadResultAsync() =>
        ReadProtectedResultAsync(
            GrimoireStorePath,
            _grimoireProtector,
            corruptMessage: "grimoire-key.dat is present but could not be decrypted (corrupt or missing Data Protection key).");

    public async Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret)
    {

        ArgumentNullException.ThrowIfNull(encryptionSecret);

        await _fileLock.WaitAsync().ConfigureAwait(false);

        try
        {

            await WriteProtectedAsync(GrimoireStorePath, encryptionSecret, _grimoireProtector).ConfigureAwait(false);

        }
        finally
        {

            _fileLock.Release();

        }

    }

    public Task<SecretStoreReadResult> GetFileEncryptionSecretReadResultAsync() =>
        ReadProtectedResultAsync(
            FileEncryptionStorePath,
            _fileEncryptionProtector,
            corruptMessage:
                "file-encryption-key.dat is present but could not be decrypted. "
                + "Restore the matching Data Protection key ring and file-encryption-key.dat from backup; "
                + "encrypted attachments, uploads, and batch artifacts are otherwise unrecoverable.");

    public async Task SaveFileEncryptionSecretAsync(string encryptionSecret)
    {
        ArgumentNullException.ThrowIfNull(encryptionSecret);
        await _fileLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await WriteProtectedAsync(
                    FileEncryptionStorePath,
                    encryptionSecret,
                    _fileEncryptionProtector)
                .ConfigureAwait(false);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private async Task<SecretStoreReadResult> ReadProtectedResultAsync(
        string path,
        IDataProtector protector,
        string corruptMessage)
    {

        await _fileLock.WaitAsync().ConfigureAwait(false);

        try
        {

            using SecureFileReadResult read = await SecureFileReader
                .ReadBytesAsync(
                    path,
                    MaxProtectedSecretBytes,
                    CancellationToken.None)
                .ConfigureAwait(false);

            if (read.Status == SecureFileReadStatus.NotFound)
            {

                return SecretStoreReadResult.Missing();

            }

            if (read.Status != SecureFileReadStatus.Success)
            {

                return SecretStoreReadResult.Corrupted(corruptMessage);

            }

            byte[] cipher = read.Bytes.ToArray();

            if (cipher.Length == 0)
            {

                CryptographicOperations.ZeroMemory(cipher);

                return SecretStoreReadResult.Corrupted(corruptMessage);

            }

            try
            {

                byte[] plain = protector.Unprotect(cipher);

                string value = Encoding.UTF8.GetString(plain);

                CryptographicOperations.ZeroMemory(plain);

                return SecretStoreReadResult.Ok(value);

            }
            catch (CryptographicException)
            {

                return SecretStoreReadResult.Corrupted(corruptMessage);

            }
            finally
            {

                CryptographicOperations.ZeroMemory(cipher);

            }

        }
        finally
        {

            _fileLock.Release();

        }

    }

    /// <summary>
    /// Test seam for <see cref="WriteProtectedAsync"/>. Production callers only ever pass paths built
    /// by <see cref="ArcanumPaths"/>, which always name a file inside the secret store directory, so
    /// the rooted-path guard on the directory cannot be driven from the public surface.
    /// </summary>
    internal static Task WriteProtectedForTestsAsync(string path, string plainText, IDataProtector protector) =>
        WriteProtectedAsync(path, plainText, protector);

    private static async Task WriteProtectedAsync(string path, string plainText, IDataProtector protector)
    {

        string directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Invalid secret store path.");

        SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(directory);

        byte[] plain = Encoding.UTF8.GetBytes(plainText);

        byte[] cipher = protector.Protect(plain);

        CryptographicOperations.ZeroMemory(plain);

        string tempPath = path + ".tmp." + Guid.NewGuid().ToString("N");

        try
        {

            await using (FileStream stream = SecureFilePermissions.CreateOwnerOnlyTempFile(tempPath))
            {

                await stream.WriteAsync(cipher).ConfigureAwait(false);

                await stream.FlushAsync().ConfigureAwait(false);

                // FlushAsync only drains the managed buffer to the OS. grimoire-key.dat has no
                // OS-credential copy, so the rename must not be able to outrun the data: fsync
                // before the atomic replace, matching GrimoireKdfSidecarFile.Write.
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
                catch (Exception cleanupFailure)
                    when (cleanupFailure is IOException or UnauthorizedAccessException)
                {
                    // Best effort cleanup of temp file. UnauthorizedAccessException belongs here as
                    // much as IOException: Windows raises it for a delete the filesystem refuses, and
                    // an uncaught throw from a finally does not merely fail to clean up -- it replaces
                    // the exception that explains why the save failed with one about the tidying
                    // afterwards. TheForge's OpenAiCompatApiClient already catches the pair for the
                    // same reason.
                }

            }

        }

    }

    internal static bool GrimoireDatabaseExists() => File.Exists(ArcanumPaths.GrimoireDatabaseFile);

}
