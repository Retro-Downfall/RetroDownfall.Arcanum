using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Security;

public sealed class FileEncryptionRuntimeCompositionTests
{
    [Fact]
    public async Task File_encryption_interfaces_share_one_disposed_singleton_and_one_runtime_status()
    {
        ServiceCollection services = [];
        services.AddDataProtection();
        services.AddSingleton<IApiKeyDigestCache, ApiKeyDigestCache>();
        services.AddArcanumSecretStore();
        services.RemoveAll<ISecretStore>();
        services.AddSingleton<ISecretStore>(
            new MissingFileEncryptionSecretStore());
        services.RemoveAll<IEncryptedBlobPresenceInspector>();
        services.AddSingleton<IEncryptedBlobPresenceInspector>(
            new FixedPresenceInspector(EncryptedBlobPresence.Absent));

        ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });

        FileEncryptionKeyProvider concrete =
            provider.GetRequiredService<FileEncryptionKeyProvider>();
        IFileEncryptionKeyProvider keyProvider =
            provider.GetRequiredService<IFileEncryptionKeyProvider>();
        IFileEncryptionKeyStartupValidator validator =
            provider.GetRequiredService<IFileEncryptionKeyStartupValidator>();
        IFileEncryptionKeyRing keyRing =
            provider.GetRequiredService<IFileEncryptionKeyRing>();
        FileEncryptionRuntimeStatus concreteStatus =
            provider.GetRequiredService<FileEncryptionRuntimeStatus>();
        IFileEncryptionRuntimeStatus publicStatus =
            provider.GetRequiredService<IFileEncryptionRuntimeStatus>();

        Assert.Same(concrete, keyProvider);
        Assert.Same(concrete, validator);
        Assert.Same(concrete, keyRing);
        Assert.Same(concreteStatus, publicStatus);

        FileEncryptionKeyMaterial material = await keyProvider.GetForWriteAsync();
        ReadOnlyMemory<byte> retainedView = material.MasterKey;

        await provider.DisposeAsync();

        Assert.All(retainedView.ToArray(), static value => Assert.Equal(0, value));

        concrete.Dispose();
    }

    private sealed class FixedPresenceInspector(EncryptedBlobPresence presence) :
        IEncryptedBlobPresenceInspector
    {
        public EncryptedBlobPresence Inspect(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return presence;
        }
    }

    private sealed class MissingFileEncryptionSecretStore : ISecretStore
    {
        public Task<string?> GetApiKeyAsync() => Task.FromResult<string?>(null);

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(SecretStoreReadResult.Missing());

        public Task SaveApiKeyAsync(string apiKey) => Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() =>
            Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) =>
            Task.CompletedTask;

        public Task<SecretStoreReadResult> GetFileEncryptionSecretReadResultAsync() =>
            Task.FromResult(SecretStoreReadResult.Missing());

        public Task<SecretStoreReadResult> PeekFileEncryptionSecretReadResultAsync() =>
            Task.FromResult(SecretStoreReadResult.Missing());

        public Task SaveFileEncryptionSecretAsync(string encryptionSecret) =>
            Task.CompletedTask;
    }
}
