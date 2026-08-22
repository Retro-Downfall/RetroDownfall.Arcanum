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
    /// Ensures a master API key exists in the OS credential store (with security.dat fallback).
    /// The shipping host invokes this only from its post-restore-topology startup callback while its
    /// exact installation maintenance lock remains attached, before Grimoire SQLCipher key derivation.
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
            IOsCredentialStore osStore = provider.GetRequiredService<IOsCredentialStore>();

            OsCredentialStoreResult probe = osStore.TryGet(
                ArcanumCredentialIdentity.Service,
                ArcanumCredentialIdentity.MasterApiKeyAccount);

            ThrowIfOsKeyStorageMayHoldTheLiveKey(probe, osStore);

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
    /// clear a mint outright: nothing of ours is stored, or there is no backend to store it in.
    /// <see cref="OsCredentialStoreStatus.Ok"/> proves a live credential is present and always fails
    /// closed. Unlike the existing-Grimoire refusal this does not depend on what is on disk: overwriting
    /// the surviving key silently 401s every client that holds it, and the old value is unrecoverable.
    ///
    /// <para><see cref="OsCredentialStoreStatus.Failed"/> is the ambiguous one, and the backend's own
    /// reachability resolves it. A locked macOS keychain and an ACL denial after a resign are failures
    /// from a backend that answered — <see cref="IOsCredentialStore.IsAvailable"/> stays true there, and
    /// a credential may well be sitting behind the refusal — so those still fail closed. A headless Linux
    /// host where libsecret loads but no Secret Service is on the bus reports the same Failed status for a
    /// transport GError, and there <see cref="IOsCredentialStore.IsAvailable"/> is false: nothing of ours
    /// can be living in a backend nothing can talk to. Refusing that case would strand first-run
    /// <c>arcanum serve</c> on exactly the hosts the security.dat mirror exists to serve (§11.2 item 4).</para>
    /// </remarks>
    internal static void ThrowIfOsKeyStorageMayHoldTheLiveKey(
        OsCredentialStoreResult probe,
        IOsCredentialStore store)
    {

        ArgumentNullException.ThrowIfNull(store);

        if (probe.Status is OsCredentialStoreStatus.NotFound or OsCredentialStoreStatus.Unavailable)
        {

            return;

        }

        if (probe.Status == OsCredentialStoreStatus.Failed && !store.IsAvailable)
        {

            Log.Warning(
                "OS key storage did not answer ({Message}); no credential of ours can be stored there, so "
                + "the master API key is minted through the security.dat mirror.",
                probe.Message);

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
