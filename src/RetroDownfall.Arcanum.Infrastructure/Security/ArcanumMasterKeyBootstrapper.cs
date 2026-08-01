using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;
using RetroDownfall.Arcanum.Secrets.Security;
using Serilog;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

public static class ArcanumMasterKeyBootstrapper
{
    /// <summary>
    /// Ensures a master API key exists in the OS credential store (with security.dat fallback)
    /// before the generic host starts (required for Grimoire SQLCipher key derivation).
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

        services.AddSingleton<IApiKeyDigestCache, ApiKeyDigestCache>();

        services.AddArcanumSecretStore();

        using ServiceProvider provider = services.BuildServiceProvider();

        ISecretStore store = provider.GetRequiredService<ISecretStore>();

        SecretStoreReadResult existing = await store.GetApiKeyReadResultAsync().ConfigureAwait(false);

        if (existing.Status == SecretStoreReadStatus.Ok)
        {
            return null;
        }

        if (existing.Status == SecretStoreReadStatus.Corrupted)
        {

            bool grimoireExists = File.Exists(ArcanumPaths.GrimoireDatabaseFile);

            if (grimoireExists)
            {

                Log.Fatal(
                    "Master API key store is corrupt while an existing Grimoire database is present. "
                    + "Restore the matching credential and Data Protection key ring before restart.");

                ThrowIfCorruptedWithExistingGrimoire(existing, grimoireExists);

            }

            Log.Warning("Master API key store is corrupt with no Grimoire database; generating a new key.");

        }

        byte[] keyBytes = new byte[32];

        RandomNumberGenerator.Fill(keyBytes);

        string apiKey = Convert.ToBase64String(keyBytes);

        CryptographicOperations.ZeroMemory(keyBytes);

        await store.SaveApiKeyAsync(apiKey).ConfigureAwait(false);

        Log.Information(
            "Master API key stored in the OS credential store ({Service}/{Account}) with security.dat mirror.",
            ArcanumCredentialIdentity.Service,
            ArcanumCredentialIdentity.MasterApiKeyAccount);

        return apiKey;

    }

    internal static void ThrowIfCorruptedWithExistingGrimoire(
        SecretStoreReadResult result,
        bool grimoireExists)
    {

        ArgumentNullException.ThrowIfNull(result);

        if (result.Status == SecretStoreReadStatus.Corrupted && grimoireExists)
        {

            throw new MasterApiKeyUnavailableException();

        }

    }

}
