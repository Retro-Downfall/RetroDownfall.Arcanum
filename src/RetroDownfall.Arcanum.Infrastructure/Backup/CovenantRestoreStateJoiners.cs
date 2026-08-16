using System.Globalization;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

/// <summary>
/// Whether the destination has ever been exposed to unsandboxed host-process tools.
/// </summary>
/// <remarks>
/// Mirrors <c>covenant_authority_state.HostToolsStateCode</c>. Ordered so a join can take the maximum
/// and be monotonic by construction: taint moves in one direction only.
/// </remarks>
internal enum CovenantHostToolsState
{

    Clean = 1,

    PendingHostToolsTaint = 2,

    HostToolsTainted = 3,

}

/// <summary>
/// The authority facts a restore has to reconcile between an archive and the machine it lands on.
/// </summary>
internal sealed record CovenantAuthorityStateRow(
    string InstallationIdentity,
    long AuthorityEpoch,
    CovenantHostToolsState HostToolsState,
    long? TaintTimeMasterVersion,
    byte[]? TaintFingerprint,
    string? TransitionId);

/// <summary>
/// Merges archived authority into the destination without ever laundering a taint away.
/// </summary>
/// <remarks>
/// The join is monotonic in exactly one direction: a destination that has been exposed to
/// unsandboxed host tools stays exposed no matter what the archive says. Restoring an archive taken
/// on a clean machine cannot clear this machine's history, because the escape happened here and no
/// file copied in from elsewhere is evidence that it did not (§10.16).
///
/// <para>Source authority is not imported. The archive's installation identity, epoch, and transition
/// belong to a different installation; adopting them would let a restore claim an authority lineage
/// this machine never had.</para>
/// </remarks>
internal static class CovenantAuthorityStateJoiner
{

    /// <summary>
    /// Computes the row the staged database must carry before it becomes live.
    /// </summary>
    internal static Result<CovenantAuthorityStateRow> Join(
        CovenantAuthorityStateRow destination,
        CovenantAuthorityStateRow source,
        string stagedInstallationIdentity)
    {

        if (destination is null || source is null)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "An authority join requires both the destination and the archived row.");

        }

        if (string.IsNullOrWhiteSpace(stagedInstallationIdentity))
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "A restored installation requires an established identity of its own.");

        }

        // Maximum, not the archive's value. Clean < Pending < Tainted, so the destination's own
        // history can only ever be preserved or strengthened by what the archive carries.
        CovenantHostToolsState joined =
            (CovenantHostToolsState)Math.Max((int)destination.HostToolsState, (int)source.HostToolsState);

        bool destinationWins = destination.HostToolsState >= source.HostToolsState;

        return new CovenantAuthorityStateRow(
            stagedInstallationIdentity,

            // The epoch advances past both, because every read authority issued under either lineage
            // has to be invalidated by the replacement.
            checked(Math.Max(destination.AuthorityEpoch, source.AuthorityEpoch) + 1),
            joined,
            joined == CovenantHostToolsState.Clean
                ? null
                : destinationWins
                    ? destination.TaintTimeMasterVersion
                    : source.TaintTimeMasterVersion,
            joined == CovenantHostToolsState.Clean
                ? null
                : destinationWins
                    ? destination.TaintFingerprint
                    : source.TaintFingerprint,
            joined == CovenantHostToolsState.Clean
                ? null
                : destinationWins
                    ? destination.TransitionId
                    : source.TransitionId);

    }

    internal static async Task<CovenantAuthorityStateRow?> ReadAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            SELECT InstallationIdentity, AuthorityEpoch, HostToolsStateCode, TaintTimeMasterVersion,
                   TaintFingerprint, TransitionId
            FROM covenant_authority_state WHERE StateKey = 1;
            """;

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            return null;

        }

        return new CovenantAuthorityStateRow(
            reader.GetString(0),
            reader.GetInt64(1),
            (CovenantHostToolsState)reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetInt64(3),
            reader.IsDBNull(4) ? null : ReadBlob(reader, 4),
            reader.IsDBNull(5) ? null : reader.GetString(5));

    }

    private static byte[] ReadBlob(SqliteDataReader reader, int ordinal)
    {

        using System.IO.Stream stream = reader.GetStream(ordinal);

        using System.IO.MemoryStream buffer = new();

        stream.CopyTo(buffer);

        return buffer.ToArray();

    }

}

/// <summary>
/// Merges archived disclosure accounting into the destination's, delegating every value decision to
/// the Core algebra.
/// </summary>
/// <remarks>
/// This type owns reads and writes only. The merge itself is
/// <see cref="CovenantDisclosureStateAlgebra.JoinRestore"/>, because the join has to be a semilattice
/// — commutative, associative, idempotent — and a second implementation living next to the storage
/// would drift from the one the rest of the system reasons about.
///
/// <para>A joined bucket becomes a lower bound. Two installations' counts cannot be added without
/// double-counting the effects they share, and the journal is allowed to overstate but never to
/// understate what left (§10.13).</para>
/// </remarks>
internal static class CovenantDisclosureStateJoiner
{

    internal static async Task<Result<int>> JoinIntoStagedAsync(
        SqliteConnection staged,
        SqliteTransaction transaction,
        IReadOnlyList<CovenantDisclosureState> destinationBuckets,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {

        if (staged is null || transaction is null || destinationBuckets is null || timeProvider is null)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "A disclosure join requires the staged transaction and the destination buckets.");

        }

        int joined = 0;

        foreach (CovenantDisclosureState destination in destinationBuckets)
        {

            CovenantDisclosureState? archived = await ReadBucketAsync(
                staged,
                transaction,
                destination.Destination,
                destination.Revocability,
                cancellationToken).ConfigureAwait(false);

            CovenantDisclosureState result = archived is null
                ? destination
                : CovenantDisclosureStateAlgebra.JoinRestore(destination, archived);

            await WriteBucketAsync(staged, transaction, result, timeProvider, cancellationToken)
                .ConfigureAwait(false);

            joined++;

        }

        return joined;

    }

    internal static async Task<List<CovenantDisclosureState>> ReadAllAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {

        List<CovenantDisclosureState> buckets = [];

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            SELECT DestinationCode, RevocabilityCode, CountKindCode, EverOccurred, JoinedCount,
                   MaxDisclosedAtUtcTicks, EvidenceBloom
            FROM external_disclosure_state;
            """;

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            buckets.Add(Materialize(reader));

        }

        return buckets;

    }

    private static async Task<CovenantDisclosureState?> ReadBucketAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CovenantEgressDestination destination,
        CovenantDisclosureRevocability revocability,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            SELECT DestinationCode, RevocabilityCode, CountKindCode, EverOccurred, JoinedCount,
                   MaxDisclosedAtUtcTicks, EvidenceBloom
            FROM external_disclosure_state
            WHERE DestinationCode = $destination AND RevocabilityCode = $revocability;
            """;

        _ = command.Parameters.AddWithValue("$destination", (int)destination);

        _ = command.Parameters.AddWithValue("$revocability", (int)revocability);

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? Materialize(reader)
            : null;

    }

    private static async Task WriteBucketAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CovenantDisclosureState state,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            INSERT INTO external_disclosure_state (
                DestinationCode, RevocabilityCode, CountKindCode, EverOccurred, JoinedCount,
                MaxDisclosedAtUtcTicks, EvidenceBloom, UpdatedAtUtc)
            VALUES ($destination, $revocability, $countKind, $everOccurred, $count,
                    $maximum, $bloom, $now)
            ON CONFLICT (DestinationCode, RevocabilityCode) DO UPDATE SET
                CountKindCode = excluded.CountKindCode,
                EverOccurred = excluded.EverOccurred,
                JoinedCount = excluded.JoinedCount,
                MaxDisclosedAtUtcTicks = excluded.MaxDisclosedAtUtcTicks,
                EvidenceBloom = excluded.EvidenceBloom,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
            """;

        _ = command.Parameters.AddWithValue("$destination", (int)state.Destination);

        _ = command.Parameters.AddWithValue("$revocability", (int)state.Revocability);

        _ = command.Parameters.AddWithValue("$countKind", (int)state.CountKind);

        _ = command.Parameters.AddWithValue("$everOccurred", state.EverOccurred ? 1 : 0);

        _ = command.Parameters.AddWithValue("$count", checked((long)state.Count));

        _ = command.Parameters.AddWithValue("$maximum", state.MaximumTimestamp);

        _ = command.Parameters.AddWithValue("$bloom", state.EvidenceBloom.ToArray());

        _ = command.Parameters.AddWithValue(
            "$now",
            timeProvider.GetUtcNow().UtcDateTime.ToString(
                "yyyy-MM-ddTHH:mm:ss.fffffffZ",
                CultureInfo.InvariantCulture));

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    private static CovenantDisclosureState Materialize(SqliteDataReader reader)
    {

        using System.IO.Stream stream = reader.GetStream(6);

        using System.IO.MemoryStream buffer = new();

        stream.CopyTo(buffer);

        return new CovenantDisclosureState(
            (CovenantEgressDestination)reader.GetInt32(0),
            (CovenantDisclosureRevocability)reader.GetInt32(1),
            (CovenantDisclosureCountKind)reader.GetInt32(2),
            reader.GetInt32(3) != 0,
            checked((ulong)reader.GetInt64(4)),
            reader.GetInt64(5),
            buffer.ToArray());

    }

}
