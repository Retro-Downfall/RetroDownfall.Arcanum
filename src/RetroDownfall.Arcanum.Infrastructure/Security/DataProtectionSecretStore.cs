using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

public sealed class DataProtectionSecretStore(IDataProtectionProvider dataProtectionProvider) : ISecretStore, IDisposable
{
    private const string ProtectorPurpose = "Arcanum.Core.ApiKey";

    private const string GrimoireProtectorPurpose = "Arcanum.Core.GrimoireEncryption";

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

        return await ReadProtectedAsync(StorePath, _protector).ConfigureAwait(false);

    }

    public async Task SaveApiKeyAsync(string apiKey)
    {
        ArgumentNullException.ThrowIfNull(apiKey);

        await _fileLock.WaitAsync().ConfigureAwait(false);

        try
        {

            await WriteProtectedAsync(StorePath, apiKey, _protector).ConfigureAwait(false);

        }
        finally
        {

            _fileLock.Release();

        }

    }

    public async Task<string?> GetGrimoireEncryptionSecretAsync()
    {

        return await ReadProtectedAsync(GrimoireStorePath, _grimoireProtector).ConfigureAwait(false);

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

    private async Task<string?> ReadProtectedAsync(string path, IDataProtector protector)
    {

        await _fileLock.WaitAsync().ConfigureAwait(false);

        try
        {

            if (!File.Exists(path))
            {

                return null;

            }

            byte[] cipher = await File.ReadAllBytesAsync(path).ConfigureAwait(false);

            if (cipher.Length == 0)
            {

                return null;

            }

            try
            {

                byte[] plain = protector.Unprotect(cipher);

                return Encoding.UTF8.GetString(plain);

            }
            catch (CryptographicException)
            {

                return null;

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

        Directory.CreateDirectory(directory);

        byte[] plain = Encoding.UTF8.GetBytes(plainText);

        byte[] cipher = protector.Protect(plain);

        await File.WriteAllBytesAsync(path, cipher).ConfigureAwait(false);

        ApplyRestrictiveUnixFileMode(path);

    }

    private static void ApplyRestrictiveUnixFileMode(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception)
        {
            // Best effort — secrets remain protected by OS user account isolation.
        }
    }
}
