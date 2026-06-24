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
    private const string ProtectorPurpose = "Arcanum.Core.ApiKey";

    private const string GrimoireProtectorPurpose = "Arcanum.Core.GrimoireEncryption";

    private const string CorruptApiKeyRecoveryMessage =
        "security.dat is present but could not be decrypted (corrupt or wrong Data Protection key ring). "
        + "Stop the host, then follow DESIGN.md §16.3: remove both security.dat and the Grimoire .db under ~/.config/arcanum/, or restore from backup. "
        + "Do not delete the Grimoire database alone if you need to keep session data.";

    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);

    private readonly IDataProtector _grimoireProtector = dataProtectionProvider.CreateProtector(GrimoireProtectorPurpose);

    private readonly SemaphoreSlim _fileLock = new(1, 1);

    private static string StorePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "arcanum", "security.dat");

    private static string GrimoireStorePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "arcanum", "grimoire-key.dat");

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

        SecretStoreReadResult result = await ReadProtectedResultAsync(
            GrimoireStorePath,
            _grimoireProtector,
            corruptMessage: "grimoire-key.dat is present but could not be decrypted.").ConfigureAwait(false);

        return result.Status == SecretStoreReadStatus.Ok ? result.Value : null;

    }

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

    private async Task<SecretStoreReadResult> ReadProtectedResultAsync(
        string path,
        IDataProtector protector,
        string corruptMessage)
    {

        await _fileLock.WaitAsync().ConfigureAwait(false);

        try
        {

            if (!File.Exists(path))
            {

                return SecretStoreReadResult.Missing();

            }

            byte[] cipher = await File.ReadAllBytesAsync(path).ConfigureAwait(false);

            if (cipher.Length == 0)
            {

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

        }
        finally
        {

            _fileLock.Release();

        }

    }

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

            await using (FileStream stream = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true))
            {

                await stream.WriteAsync(cipher).ConfigureAwait(false);

                await stream.FlushAsync().ConfigureAwait(false);

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
                    // Best effort cleanup of temp file.
                }

            }

        }

    }

    internal static bool GrimoireDatabaseExists() => File.Exists(ArcanumPaths.GrimoireDatabaseFile);

}
