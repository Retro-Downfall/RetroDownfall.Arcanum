using Microsoft.Data.Sqlite;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Schema;

/// <summary>
/// Recovers the installation-local facts a tier initializer needs from the database that already
/// holds them.
/// </summary>
/// <remarks>
/// Two callers need this and neither holds the installation lock and master key the bootstrap builds
/// its context from: a restore converging a staged snapshot, and a background pass converging a tier
/// after a sweep drained. Both are working on a database whose authority row exists, so reading it
/// back is the only answer that does not invent a new identity. A restore is not a key rotation, and
/// neither is finishing a version run.
///
/// <para>A row that fails the shape check is reported as absent rather than propagated. Feeding a
/// malformed identity into an install transaction would abort the Core tier, when the caller's
/// remaining content is still perfectly recoverable.</para>
/// </remarks>
internal static class GrimoireSchemaInitializationContextReader
{

    internal static async Task<GrimoireSchemaInitializationContext?> TryReadAsync(
        SqliteConnection connection,
        DateTimeOffset installedAtUtc,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(connection);

        if (!await TableExistsAsync(connection, "covenant_authority_state", cancellationToken)
            .ConfigureAwait(false))
        {

            return null;

        }

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            SELECT InstallationIdentity,
                   AuthorityEpoch,
                   CurrentMasterKeyVersion,
                   CurrentMasterKeyFingerprint,
                   RecoveryEnvelopeEpoch
            FROM covenant_authority_state
            WHERE StateKey = 1;
            """;

        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            return null;

        }

        if (reader.IsDBNull(0) || reader.GetValue(3) is not byte[] fingerprint)
        {

            return null;

        }

        string installationIdentity = reader.GetString(0);

        long authorityEpoch = reader.GetInt64(1);

        long masterKeyVersion = reader.GetInt64(2);

        long recoveryEnvelopeEpoch = reader.GetInt64(4);

        bool usable = installationIdentity.Length is > 0 and <= 128
            && authorityEpoch > 0
            && masterKeyVersion is > 0 and <= uint.MaxValue
            && fingerprint.Length == 32
            && recoveryEnvelopeEpoch > 0;

        return usable
            ? new GrimoireSchemaInitializationContext(
                installationIdentity,
                authorityEpoch,
                (uint)masterKeyVersion,
                fingerprint,
                recoveryEnvelopeEpoch,
                installedAtUtc)
            : null;

    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string name,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """SELECT 1 FROM sqlite_master WHERE "type" = 'table' AND "name" = $name LIMIT 1;""";

        _ = command.Parameters.AddWithValue("$name", name);

        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return result is not null && result != DBNull.Value;

    }

}
