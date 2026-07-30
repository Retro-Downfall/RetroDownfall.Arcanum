namespace RetroDownfall.Arcanum.Core.Security;

public interface ISecretStore
{

    Task<string?> GetApiKeyAsync();

    Task<SecretStoreReadResult> GetApiKeyReadResultAsync();

    Task SaveApiKeyAsync(string apiKey);

    Task<string?> GetGrimoireEncryptionSecretAsync();

    Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret);

    Task<SecretStoreReadResult> GetFileEncryptionSecretReadResultAsync() =>
        Task.FromResult(SecretStoreReadResult.Missing());

    Task SaveFileEncryptionSecretAsync(string encryptionSecret) =>
        throw new NotSupportedException(
            "This secret store does not support the dedicated file-encryption secret.");

}
