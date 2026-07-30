using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

public sealed class TestApiKeySecretStore(string apiKey) : ISecretStore
{
    private string? _fileEncryptionSecret;

    public Task<string?> GetApiKeyAsync() =>
        Task.FromResult<string?>(apiKey);

    public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
        Task.FromResult(
            string.IsNullOrWhiteSpace(apiKey)
                ? SecretStoreReadResult.Missing()
                : SecretStoreReadResult.Ok(apiKey));

    public Task SaveApiKeyAsync(string apiKey) =>
        Task.CompletedTask;

    public Task<string?> GetGrimoireEncryptionSecretAsync() =>
        Task.FromResult<string?>(null);

    public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) =>
        Task.CompletedTask;

    public Task<SecretStoreReadResult> GetFileEncryptionSecretReadResultAsync() =>
        Task.FromResult(
            _fileEncryptionSecret is null
                ? SecretStoreReadResult.Missing()
                : SecretStoreReadResult.Ok(_fileEncryptionSecret));

    public Task SaveFileEncryptionSecretAsync(string encryptionSecret)
    {
        _fileEncryptionSecret = encryptionSecret;
        return Task.CompletedTask;
    }

}
