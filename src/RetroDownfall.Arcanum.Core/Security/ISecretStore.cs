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

    /// <summary>
    /// True while this process's startup read of the master API key (the host's key bootstrap) was
    /// answered from the encrypted mirror because OS key storage failed (a locked keychain at boot),
    /// and no OS read has answered since — an answer that the store holds nothing counts. A runtime
    /// read served from the mirror never sets it, and a save clears it only once the save has
    /// committed. Peeks still fail closed in that state; only the request path may keep
    /// authenticating the key this process adopted at startup (DESIGN §11.2 item 4).
    /// </summary>
    bool ServesMasterApiKeyFromMirrorDuringOsFailure => false;

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

    /// <summary>
    /// Removes the master API key. A rolled-back restore is the only caller: it returns a machine
    /// that had no key before the restore to having none. A store that cannot remove one throws
    /// rather than leaving the restored key authenticating callers.
    /// </summary>
    Task DeleteApiKeyAsync() =>
        throw new NotSupportedException("This secret store cannot remove the master API key.");

    /// <summary>
    /// Removes the Grimoire encryption secret, for the same rollback and on the same terms as
    /// <see cref="DeleteApiKeyAsync"/>.
    /// </summary>
    Task DeleteGrimoireEncryptionSecretAsync() =>
        throw new NotSupportedException("This secret store cannot remove the Grimoire encryption secret.");

    /// <summary>
    /// Removes the dedicated file-encryption secret, for the same rollback and on the same terms as
    /// <see cref="DeleteApiKeyAsync"/>.
    /// </summary>
    Task DeleteFileEncryptionSecretAsync() =>
        throw new NotSupportedException(
            "This secret store cannot remove the dedicated file-encryption secret.");
}
