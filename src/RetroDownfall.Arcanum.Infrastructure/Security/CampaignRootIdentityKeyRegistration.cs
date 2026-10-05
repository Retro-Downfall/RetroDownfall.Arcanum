using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

/// <summary>
/// The Campaign-root registration flow's access to the root-identity key: counts the registered-root
/// evidence on the caller's Grimoire connection and lets the key be created only when there is none.
/// </summary>
/// <remarks>
/// The evidence is the registry and marker store that every Campaign identity is resolved against: a
/// <c>campaign_path_identities</c> row (a registered root, matched by <c>CampaignPathIdentityReader</c>
/// to the identities <see cref="Tower.PhysicalCampaignRootOpener"/> derives) or a
/// <c>campaign_path_marker_intents</c> row (a marker written for one). Either proves a key existed, so a
/// missing key is a lost one, and the call fails closed with
/// <see cref="ErrorCodes.Campaign.RootIdentityKeyLost"/> naming the recovery instead of minting a key
/// that would orphan every registration (§10.12). The split is the erasure keyring's: the provider
/// cannot read the Grimoire, so the caller that can brings the evidence.
///
/// <para>The provider is a process-wide synchronous singleton and the evidence lives behind the
/// asynchronous connection admission, so the count is taken here, on a connection the registration flow
/// already holds, rather than by the provider. Nothing in this release registers a Campaign root
/// (§10.22.6); this is the entry point that flow calls first.</para>
/// </remarks>
internal static class CampaignRootIdentityKeyRegistration
{
    internal static Error KeyLostError { get; } = new(
        ErrorCodes.Campaign.RootIdentityKeyLost,
        "Campaign roots are registered, but this installation's Campaign root-identity key is missing from the OS "
        + "credential store, so no replacement key is created: a new key would orphan every registered root. "
        + "Restore the 'campaign-root-identity-key' credential and restart the host, or reset the installation.");

    internal static Error KeyUnavailableError { get; } = new(
        ErrorCodes.Campaign.RootIdentityKeyUnavailable,
        "The Campaign root-identity key could not be read or created in the OS credential store, so no Campaign "
        + "root can be registered until it can be.");

    /// <summary>
    /// Makes sure the key exists before a Campaign root is registered, creating it only when no root is
    /// registered and no marker intent is recorded.
    /// </summary>
    /// <exception cref="InvalidOperationException">The connection is inside a transaction.</exception>
    internal static async Task<Result> EnsureKeyForRegistrationAsync(
        SqliteConnection connection,
        ICampaignRootIdentityKeyCreator creator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(creator);

        // The key is read from, and may be written to, the OS credential store, which can sit behind a
        // prompt indefinitely; no SQLite transaction is held across that, exactly as the erasure key is not.
        if (connection.State == System.Data.ConnectionState.Open
            && SQLitePCL.raw.sqlite3_get_autocommit(connection.Handle) == 0)
        {
            throw new InvalidOperationException(
                "The Campaign root-identity key is read from the OS credential store, which is never done inside "
                + "a SQLite transaction; ensure the key before the registration's transaction begins.");
        }

        bool registeredRootsExist = await AnyRegisteredRootEvidenceAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        return creator.OpenOrCreateRootIdentityKey(registeredRootsExist) switch
        {
            CampaignRootIdentityKeyState.Present or CampaignRootIdentityKeyState.Created => Result.Success(),
            CampaignRootIdentityKeyState.Lost => Result.Failure(KeyLostError),
            _ => Result.Failure(KeyUnavailableError),
        };
    }

    /// <summary>
    /// Whether any Campaign root is registered or any marker intent recorded, on this connection.
    /// </summary>
    internal static async Task<bool> AnyRegisteredRootEvidenceAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            SELECT EXISTS (SELECT 1 FROM campaign_path_identities)
                OR EXISTS (SELECT 1 FROM campaign_path_marker_intents);
            """;

        object? scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return scalar is long found && found != 0;
    }
}
