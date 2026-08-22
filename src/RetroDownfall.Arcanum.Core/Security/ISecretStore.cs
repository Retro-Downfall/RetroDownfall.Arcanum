namespace RetroDownfall.Arcanum.Core.Security;

public interface ISecretStore
{

    Task<string?> GetApiKeyAsync();

    Task<SecretStoreReadResult> GetApiKeyReadResultAsync();

    /// <summary>
    /// Reads the master API key without migrating, repairing, or persisting credential state.
    /// The default preserves compatibility for stores whose ordinary read is already pure; stores
    /// whose ordinary read can mutate state must override it.
    /// </summary>
    Task<SecretStoreReadResult> PeekApiKeyReadResultAsync() =>
        GetApiKeyReadResultAsync();

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

    /// <summary>
    /// Reads the dedicated file-encryption key without migrating, repairing, or persisting
    /// credential state. The default preserves compatibility for stores whose ordinary read is
    /// already pure; stores whose ordinary read can mutate state must override it.
    /// </summary>
    Task<SecretStoreReadResult> PeekFileEncryptionSecretReadResultAsync() =>
        GetFileEncryptionSecretReadResultAsync();

    Task SaveFileEncryptionSecretAsync(string encryptionSecret) =>
        throw new NotSupportedException(
            "This secret store does not support the dedicated file-encryption secret.");

}
