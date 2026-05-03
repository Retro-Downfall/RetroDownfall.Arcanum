using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

public sealed class DataProtectionSecretStore(IDataProtectionProvider dataProtectionProvider) : ISecretStore, IDisposable
{
    private const string ProtectorPurpose = "Arcanum.Core.ApiKey";
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private static string StorePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "arcanum", "security.dat");
    public void Dispose() => _fileLock.Dispose();
    public async Task<string?> GetApiKeyAsync()
    {
        await _fileLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!File.Exists(StorePath))
            {
                return null;
            }

            byte[] cipher = await File.ReadAllBytesAsync(StorePath).ConfigureAwait(false);
            if (cipher.Length == 0)
            {
                return null;
            }

            try
            {
                byte[] plain = _protector.Unprotect(cipher);
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

    public async Task SaveApiKeyAsync(string apiKey)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        await _fileLock.WaitAsync().ConfigureAwait(false);
        try
        {
            string directory = Path.GetDirectoryName(StorePath)
                ?? throw new InvalidOperationException("Invalid secret store path.");
            Directory.CreateDirectory(directory);
            byte[] plain = Encoding.UTF8.GetBytes(apiKey);
            byte[] cipher = _protector.Protect(plain);
            await File.WriteAllBytesAsync(StorePath, cipher).ConfigureAwait(false);
        }
        finally
        {
            _fileLock.Release();
        }
    }
}
