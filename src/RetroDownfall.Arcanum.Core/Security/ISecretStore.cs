namespace RetroDownfall.Arcanum.Core.Security;

public interface ISecretStore
{

    Task<string?> GetApiKeyAsync();

    Task<SecretStoreReadResult> GetApiKeyReadResultAsync();

    Task SaveApiKeyAsync(string apiKey);

    Task<string?> GetGrimoireEncryptionSecretAsync();

    Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret);

}
