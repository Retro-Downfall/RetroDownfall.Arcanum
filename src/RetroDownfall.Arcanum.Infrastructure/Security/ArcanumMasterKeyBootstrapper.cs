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

            // Regenerating over corruption is only safe when OS key storage is known to hold nothing of
            // ours. The read that produced Corrupted may itself have been the OS failure, in which case
            // the live credential's existence is unknown — and SaveApiKeyAsync would overwrite it, or on
            // a failed write delete it outright. Probe once and fail closed unless the answer is clear.
            OsCredentialStoreResult probe = provider.GetRequiredService<IOsCredentialStore>().TryGet(
                ArcanumCredentialIdentity.Service,
                ArcanumCredentialIdentity.MasterApiKeyAccount);

            ThrowIfOsKeyStorageMayHoldTheLiveKey(probe);

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

    /// <summary>
    /// Refuses regeneration unless OS key storage is known to hold no master credential.
    /// </summary>
    /// <remarks>
    /// <see cref="OsCredentialStoreStatus.NotFound"/> and <see cref="OsCredentialStoreStatus.Unavailable"/>
    /// are the only two answers that clear a mint: nothing of ours is stored, or there is no backend to
    /// store it in. <see cref="OsCredentialStoreStatus.Ok"/> proves a live credential is present, and
    /// <see cref="OsCredentialStoreStatus.Failed"/> leaves its existence unknown — the probe is exactly
    /// what broke — so both fail closed. Unlike the existing-Grimoire refusal this does not depend on
    /// what is on disk: overwriting the surviving key silently 401s every client that holds it, and the
    /// old value is unrecoverable.
    /// </remarks>
    internal static void ThrowIfOsKeyStorageMayHoldTheLiveKey(OsCredentialStoreResult probe)
    {

        if (probe.Status is OsCredentialStoreStatus.NotFound or OsCredentialStoreStatus.Unavailable)
        {

            return;

        }

        Log.Fatal(
            "OS key storage may still hold the master API key ({Status}); refusing to generate a replacement. "
            + "Repair the OS credential before restart.",
            probe.Status);

        throw new MasterApiKeyUnavailableException(
            "The master API key store is corrupt and OS key storage could not confirm that the existing "
            + "credential is gone. Repair the OS credential and the Data Protection key ring before "
            + "restarting Arcanum, so a replacement key is not minted over the live one.");

    }

}
