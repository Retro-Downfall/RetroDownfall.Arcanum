using System.Security.Cryptography;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Security;

public sealed class FileEncryptionKeyProviderTests
{
    [Fact]
    public async Task GetForWriteAsync_generates_a_dedicated_256_bit_secret_once()
    {
        RecordingSecretStore secrets = new(SecretStoreReadResult.Missing());
        FileEncryptionKeyProvider provider = new(secrets);

        FileEncryptionKeyMaterial first = await provider.GetForWriteAsync();
        FileEncryptionKeyMaterial second = await provider.GetForWriteAsync();

        Assert.Equal(first.KeyId, second.KeyId);
        Assert.Equal(1, secrets.SaveCount);
        Assert.NotNull(secrets.SavedSecret);
        Assert.Equal(32, Convert.FromBase64String(secrets.SavedSecret!).Length);
    }

    [Fact]
    public async Task GetForReadAsync_missing_or_wrong_key_fails_closed_with_recovery_guidance()
    {
        FileEncryptionKeyProvider missing = new(
            new RecordingSecretStore(SecretStoreReadResult.Missing()));

        EncryptedBlobKeyException missingError =
            await Assert.ThrowsAsync<EncryptedBlobKeyException>(
                () => missing.GetForReadAsync("0123456789abcdef").AsTask());
        Assert.Contains("restore", missingError.Message, StringComparison.OrdinalIgnoreCase);

        string secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        FileEncryptionKeyProvider wrong = new(
            new RecordingSecretStore(SecretStoreReadResult.Ok(secret)));
        await Assert.ThrowsAsync<EncryptedBlobKeyException>(
            () => wrong.GetForReadAsync("0123456789abcdef").AsTask());
    }

    [Fact]
    public async Task Corrupt_protected_secret_never_generates_a_replacement()
    {
        RecordingSecretStore secrets = new(
            SecretStoreReadResult.Corrupted("protected secret is corrupt; restore backup"));
        FileEncryptionKeyProvider provider = new(secrets);

        EncryptedBlobKeyException error =
            await Assert.ThrowsAsync<EncryptedBlobKeyException>(
                () => provider.GetForWriteAsync().AsTask());

        Assert.Contains("restore", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, secrets.SaveCount);
    }

    [Fact]
    public async Task Missing_secret_with_existing_ciphertext_never_generates_a_replacement()
    {
        RecordingSecretStore secrets = new(SecretStoreReadResult.Missing());
        FileEncryptionKeyProvider provider = new(secrets, encryptedBlobsExist: static () => true);

        EncryptedBlobKeyException error =
            await Assert.ThrowsAsync<EncryptedBlobKeyException>(
                () => provider.GetForWriteAsync().AsTask());

        Assert.Contains("restore", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, secrets.SaveCount);
    }

    [Fact]
    public async Task Rotate_retains_prior_key_for_reads_until_explicit_retirement()
    {
        string secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        RecordingSecretStore secrets = new(SecretStoreReadResult.Ok(secret));
        FileEncryptionKeyProvider provider = new(secrets);
        FileEncryptionKeyMaterial prior = await provider.GetForWriteAsync();

        FileEncryptionKeyMaterial current = await provider.RotateAsync();

        Assert.NotEqual(prior.KeyId, current.KeyId);
        Assert.Equal(current.KeyId, (await provider.GetForWriteAsync()).KeyId);
        Assert.Equal(prior.KeyId, (await provider.GetForReadAsync(prior.KeyId)).KeyId);
        Assert.Contains(prior.KeyId, await provider.GetActiveKeyIdsAsync());
        Assert.Contains(current.KeyId, await provider.GetActiveKeyIdsAsync());

        FileEncryptionKeyProvider restored = new(secrets);
        Assert.Equal(current.KeyId, (await restored.GetForWriteAsync()).KeyId);
        Assert.Equal(prior.KeyId, (await restored.GetForReadAsync(prior.KeyId)).KeyId);

        await restored.RetireAsync(prior.KeyId);

        await Assert.ThrowsAsync<EncryptedBlobKeyException>(
            () => restored.GetForReadAsync(prior.KeyId).AsTask());
        Assert.Equal([current.KeyId], await restored.GetActiveKeyIdsAsync());
    }

    [Fact]
    public async Task Active_write_key_cannot_be_retired()
    {
        string secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        FileEncryptionKeyProvider provider = new(
            new RecordingSecretStore(SecretStoreReadResult.Ok(secret)));
        FileEncryptionKeyMaterial current = await provider.GetForWriteAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.RetireAsync(current.KeyId));
    }

    private sealed class RecordingSecretStore(SecretStoreReadResult readResult) : ISecretStore
    {
        public int SaveCount { get; private set; }

        public string? SavedSecret { get; private set; }

        public Task<string?> GetApiKeyAsync() => Task.FromResult<string?>(null);

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(SecretStoreReadResult.Missing());

        public Task SaveApiKeyAsync(string apiKey) => Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() =>
            Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) =>
            Task.CompletedTask;

        public Task<SecretStoreReadResult> GetFileEncryptionSecretReadResultAsync()
        {
            if (SavedSecret is not null)
            {
                return Task.FromResult(SecretStoreReadResult.Ok(SavedSecret));
            }

            return Task.FromResult(readResult);
        }

        public Task SaveFileEncryptionSecretAsync(string encryptionSecret)
        {
            SaveCount++;
            SavedSecret = encryptionSecret;
            return Task.CompletedTask;
        }
    }
}
