using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using Serilog;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

public static class ArcanumMasterKeyBootstrapper
{
    /// <summary>
    /// Ensures a master API key exists on disk before the generic host starts (required for Grimoire SQLCipher key derivation).
    /// </summary>
    /// <returns>The newly generated key material when one was created; otherwise null.</returns>
    public static async Task<string?> EnsureMasterApiKeyExistsAsync(CancellationToken cancellationToken = default)
    {

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ARCANUM_SKIP_KEY_BOOTSTRAP")))
        {

            return null;

        }

        ServiceCollection services = new();

        services.AddDataProtection()
            .SetApplicationName("ArcanumCore")
            .PersistKeysToFileSystem(DataProtectionKeyPaths.EnsureDirectory());

        services.AddSingleton<ISecretStore, DataProtectionSecretStore>();

        using ServiceProvider provider = services.BuildServiceProvider();

        ISecretStore store = provider.GetRequiredService<ISecretStore>();

        SecretStoreReadResult existing = await store.GetApiKeyReadResultAsync().ConfigureAwait(false);

        if (existing.Status == SecretStoreReadStatus.Ok)
        {
            return null;
        }

        if (existing.Status == SecretStoreReadStatus.Corrupted)
        {

            string message = existing.Message
                ?? "security.dat is present but could not be decrypted.";

            if (File.Exists(ArcanumPaths.GrimoireDatabaseFile))
            {

                Log.Fatal(
                    "Master API key store is corrupt and a Grimoire database exists at {DbPath}. {Recovery}",
                    ArcanumPaths.GrimoireDatabaseFile,
                    message);

                Environment.FailFast(message);

            }

            Log.Warning("Master API key store is corrupt with no Grimoire database; generating a new key.");

        }

        byte[] keyBytes = new byte[32];

        RandomNumberGenerator.Fill(keyBytes);

        string apiKey = Convert.ToBase64String(keyBytes);

        CryptographicOperations.ZeroMemory(keyBytes);

        await store.SaveApiKeyAsync(apiKey).ConfigureAwait(false);

        return apiKey;

    }

}
