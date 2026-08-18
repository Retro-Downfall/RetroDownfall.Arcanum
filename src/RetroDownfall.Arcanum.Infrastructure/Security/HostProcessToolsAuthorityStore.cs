using System.Globalization;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

/// <summary>
/// The durable half of the host-process-tools transition, over the always-present core authority row.
/// </summary>
/// <remarks>
/// Every mutation is a compare-and-swap against the exact state, epoch, and transition the caller
/// observed. That is what makes a resumed transition safe: the second attempt either finds the world
/// it expects and advances, or finds it changed and refuses, and never blends the two.
///
/// <para>The inventory counts Covenant canonical rows and protected artifact labels separately
/// because they fail independently — a damaged canonical tier can be absent while protected assistant
/// artifacts remain — and a missing optional table counts as zero rather than as an error, so a
/// legitimately Covenant-free installation is not blocked from a transition it is entitled to
/// (§10.12).</para>
/// </remarks>
internal sealed class HostProcessToolsAuthorityStore(SqliteConnection connection)
    : IHostProcessToolsAuthorityStore
{

    private readonly SqliteConnection _connection =
        connection ?? throw new ArgumentNullException(nameof(connection));

    public async Task<Result<HostProcessToolsAuthorityRow>> ReadAsync(CancellationToken cancellationToken)
    {

        Result<HostProcessToolsAuthorityRow?> row = await TryReadAsync(cancellationToken).ConfigureAwait(false);

        if (row.IsFailure)
        {

            return row.Error;

        }

        return row.Value is { } present
            ? present
            : new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "The installation has no durable authority row.");

    }

    public async Task<Result<HostProcessToolsAuthorityRow?>> TryReadAsync(CancellationToken cancellationToken)
    {

        if (!await TableExistsAsync("covenant_authority_state", cancellationToken).ConfigureAwait(false))
        {

            return Result<HostProcessToolsAuthorityRow?>.Success(null);

        }

        await using SqliteCommand command = _connection.CreateCommand();

        command.CommandText = """
            SELECT InstallationIdentity,
                   AuthorityEpoch,
                   CurrentMasterKeyVersion,
                   CurrentMasterKeyFingerprint,
                   RecoveryEnvelopeEpoch,
                   HostToolsStateCode,
                   TransitionId,
                   TaintTimeMasterVersion,
                   TaintFingerprint
            FROM covenant_authority_state
            WHERE StateKey = 1;
            """;

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            return Result<HostProcessToolsAuthorityRow?>.Success(null);

        }

        try
        {

            // The digest columns are declared BLOB, which in a non-STRICT table is SQLite's "no
            // affinity": a TEXT value is stored as TEXT and still satisfies length(...) = 32, so the
            // CHECK constraint cannot be relied on to guarantee the shape. Match on it the way the
            // sibling readers of this same column do, rather than casting and hoping.
            if (reader.GetValue(3) is not byte[] fingerprint)
            {

                return MalformedRow("CurrentMasterKeyFingerprint");

            }

            byte[]? taintFingerprint = null;

            if (!reader.IsDBNull(8))
            {

                if (reader.GetValue(8) is not byte[] taint)
                {

                    return MalformedRow("TaintFingerprint");

                }

                taintFingerprint = taint;

            }

            return new HostProcessToolsAuthorityRow(
                reader.GetString(0),
                reader.GetInt64(1),
                checked((uint)reader.GetInt64(2)),
                new CovenantDigest(fingerprint),
                reader.GetInt64(4),
                (CovenantHostToolsState)reader.GetInt64(5),
                reader.IsDBNull(6) ? null : Guid.Parse(reader.GetString(6)),
                reader.IsDBNull(7) ? null : checked((uint)reader.GetInt64(7)),
                taintFingerprint is null ? null : new CovenantDigest(taintFingerprint));

        }
        catch (Exception exception) when (
            exception is FormatException or OverflowException or ArgumentException
                or InvalidCastException)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "The durable authority row is malformed.");

        }

    }

    public async Task<Result<HostProcessToolsProtectedInventory>> InventoryProtectedStateAsync(
        CancellationToken cancellationToken)
    {

        try
        {

            long canonical = await CountIfPresentAsync("covenant_versions", cancellationToken)
                .ConfigureAwait(false)
                + await CountIfPresentAsync("covenant_entries", cancellationToken).ConfigureAwait(false);

            long protectedArtifacts = await CountIfPresentAsync("artifact_sensitivity", cancellationToken)
                .ConfigureAwait(false);

            return new HostProcessToolsProtectedInventory(canonical, protectedArtifacts);

        }
        catch (SqliteException)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "The protected-state inventory could not be completed.");

        }

    }

    public Task<Result> CommitPendingAsync(
        HostProcessToolsAuthorityRow expected,
        Guid transitionId,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(expected);

        // The taint pins the key in force right now. A later rotation advances the current version
        // and leaves these two columns exactly where the escape happened.
        return CompareAndSwapAsync(
            expected,
            """
            UPDATE covenant_authority_state
            SET HostToolsStateCode = 2,
                TransitionId = $transitionId,
                TaintTimeMasterVersion = CurrentMasterKeyVersion,
                TaintFingerprint = CurrentMasterKeyFingerprint,
                UpdatedAtUtc = $updatedAtUtc
            WHERE StateKey = 1
              AND HostToolsStateCode = 1
              AND AuthorityEpoch = $expectedEpoch
              AND InstallationIdentity = $installationIdentity;
            """,
            command => command.Parameters.AddWithValue("$transitionId", Format(transitionId)),
            cancellationToken);

    }

    public Task<Result> CommitTaintedAsync(
        HostProcessToolsAuthorityRow expected,
        Guid transitionId,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(expected);

        // Both epochs advance in the same statement as the state. A reader that saw the new epoch
        // beside the old state could still mint authority for an installation that just lost it.
        return CompareAndSwapAsync(
            expected,
            """
            UPDATE covenant_authority_state
            SET HostToolsStateCode = 3,
                AuthorityEpoch = AuthorityEpoch + 1,
                RecoveryEnvelopeEpoch = RecoveryEnvelopeEpoch + 1,
                UpdatedAtUtc = $updatedAtUtc
            WHERE StateKey = 1
              AND HostToolsStateCode = 2
              AND AuthorityEpoch = $expectedEpoch
              AND InstallationIdentity = $installationIdentity
              AND TransitionId = $transitionId;
            """,
            command => command.Parameters.AddWithValue("$transitionId", Format(transitionId)),
            cancellationToken);

    }

    public Task<Result> CompensateToCleanAsync(
        HostProcessToolsAuthorityRow expected,
        Guid transitionId,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(expected);

        return CompareAndSwapAsync(
            expected,
            """
            UPDATE covenant_authority_state
            SET HostToolsStateCode = 1,
                TransitionId = NULL,
                TaintTimeMasterVersion = NULL,
                TaintFingerprint = NULL,
                UpdatedAtUtc = $updatedAtUtc
            WHERE StateKey = 1
              AND HostToolsStateCode = 2
              AND AuthorityEpoch = $expectedEpoch
              AND InstallationIdentity = $installationIdentity
              AND TransitionId = $transitionId;
            """,
            command => command.Parameters.AddWithValue("$transitionId", Format(transitionId)),
            cancellationToken);

    }

    private async Task<Result> CompareAndSwapAsync(
        HostProcessToolsAuthorityRow expected,
        string sql,
        Action<SqliteCommand> bind,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = _connection.CreateCommand();

        command.CommandText = sql;

        _ = command.Parameters.AddWithValue("$expectedEpoch", expected.AuthorityEpoch);

        _ = command.Parameters.AddWithValue("$installationIdentity", expected.InstallationIdentity);

        _ = command.Parameters.AddWithValue(
            "$updatedAtUtc",
            DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));

        bind(command);

        try
        {

            int affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            return affected == 1
                ? Result.Success()
                : Result.Failure(new Error(
                    ErrorCodes.Covenant.RevisionConflict,
                    "The durable authority row changed before this transition could advance it."));

        }
        catch (SqliteException)
        {

            return new Error(
                ErrorCodes.Grimoire.WriteFailed,
                "The durable authority transition could not be written.");

        }

    }

    private async Task<bool> TableExistsAsync(string table, CancellationToken cancellationToken)
    {

        await using SqliteCommand exists = _connection.CreateCommand();

        exists.CommandText = """
            SELECT 1 FROM sqlite_master WHERE "type" = 'table' AND "name" = $name LIMIT 1;
            """;

        _ = exists.Parameters.AddWithValue("$name", table);

        return await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not (null or DBNull);

    }

    private async Task<long> CountIfPresentAsync(string table, CancellationToken cancellationToken)
    {

        if (!await TableExistsAsync(table, cancellationToken).ConfigureAwait(false))
        {

            return 0;

        }

        await using SqliteCommand count = _connection.CreateCommand();

        // The table name comes from this method's own closed literal set, never from a caller.
        count.CommandText = $"SELECT COUNT(*) FROM {table};";

        object? value = await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);

    }

    /// <summary>
    /// Names the offending column so the blocked disposition points the operator at one field rather
    /// than at the whole row.
    /// </summary>
    private static Error MalformedRow(string column) =>
        new(
            ErrorCodes.Covenant.IntegrityFailure,
            $"The durable authority row is malformed: {column} is not stored as a digest.");

    private static string Format(Guid value) => value.ToString().ToUpperInvariant();

}
