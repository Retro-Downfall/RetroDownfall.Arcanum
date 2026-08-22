namespace RetroDownfall.Arcanum.Core.Security;

/// <summary>
/// Secure storage for one inference provider's API key. Implementations must never expose
/// credential values through configuration, diagnostics, logs, or structured output; only
/// presence/status and fixed recovery guidance may leave the store.
/// </summary>
public interface IProviderCredentialStore
{

    /// <summary>
    /// Reads the stored credential for <paramref name="providerName"/>. A present-but-undecryptable
    /// credential reports <see cref="SecretStoreReadStatus.Corrupted"/> and never silently
    /// regenerates a replacement.
    /// </summary>
    Task<SecretStoreReadResult> GetApiKeyReadResultAsync(
        string providerName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a stored provider credential without migrating, repairing, or persisting credential
    /// state. The default preserves compatibility for stores whose ordinary read is already pure;
    /// stores whose ordinary read can mutate state must override it.
    /// </summary>
    Task<SecretStoreReadResult> PeekApiKeyReadResultAsync(
        string providerName,
        CancellationToken cancellationToken = default) =>
        GetApiKeyReadResultAsync(providerName, cancellationToken);

    Task SaveApiKeyAsync(
        string providerName,
        string apiKey,
        CancellationToken cancellationToken = default);

    Task DeleteApiKeyAsync(
        string providerName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reports whether a usable credential is stored, without returning it. A corrupt credential is
    /// not usable and therefore reports <see langword="false"/>.
    /// </summary>
    async Task<bool> HasApiKeyAsync(
        string providerName,
        CancellationToken cancellationToken = default)
    {

        SecretStoreReadResult result = await GetApiKeyReadResultAsync(
                providerName,
                cancellationToken)
            .ConfigureAwait(false);

        return result.Status == SecretStoreReadStatus.Ok
            && !string.IsNullOrWhiteSpace(result.Value);

    }

}
