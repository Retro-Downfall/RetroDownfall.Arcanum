namespace RetroDownfall.Arcanum.Core.Security;

public interface ISecretStore
{

    Task<string?> GetApiKeyAsync();

    Task<SecretStoreReadResult> GetApiKeyReadResultAsync();

    Task SaveApiKeyAsync(string apiKey);

    Task<string?> GetGrimoireEncryptionSecretAsync();

    async Task<SecretStoreReadResult> GetGrimoireEncryptionSecretReadResultAsync()
    {

        string? value = await GetGrimoireEncryptionSecretAsync().ConfigureAwait(false);

        return value is null
            ? SecretStoreReadResult.Missing()
            : SecretStoreReadResult.Ok(value);

    }

    Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret);

    Task<SecretStoreReadResult> GetFileEncryptionSecretReadResultAsync() =>
        Task.FromResult(SecretStoreReadResult.Missing());

    Task SaveFileEncryptionSecretAsync(string encryptionSecret) =>
        throw new NotSupportedException(
            "This secret store does not support the dedicated file-encryption secret.");

}
