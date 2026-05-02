using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

public static class ArcanumMasterKeyBootstrapper
{
    /// <summary>

    /// Ensures a master API key exists on disk before the generic host starts (required for Grimoire SQLCipher key derivation).

    /// </summary>

    /// <returns>The newly generated key material when one was created; otherwise null.</returns>

    public static async Task<string?> EnsureMasterApiKeyExistsAsync(CancellationToken cancellationToken = default)
    {
        ServiceCollection services = new();

        services.AddDataProtection().SetApplicationName("ArcanumCore");

        services.AddSingleton<ISecretStore, DataProtectionSecretStore>();

        using ServiceProvider provider = services.BuildServiceProvider();

        ISecretStore store = provider.GetRequiredService<ISecretStore>();

        if (await store.GetApiKeyAsync().ConfigureAwait(false) is not null)
        {
            return null;
        }

        byte[] keyBytes = new byte[32];

        RandomNumberGenerator.Fill(keyBytes);

        string apiKey = Convert.ToBase64String(keyBytes);

        await store.SaveApiKeyAsync(apiKey).ConfigureAwait(false);

        return apiKey;
    }
}
