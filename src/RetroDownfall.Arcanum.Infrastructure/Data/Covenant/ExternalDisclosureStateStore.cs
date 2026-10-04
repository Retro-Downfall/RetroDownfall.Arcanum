using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// Reads and writes the persisted <c>external_disclosure_state</c> buckets on a caller-owned
/// connection, and nothing else.
/// </summary>
/// <remarks>
/// The live disclosure fold and the restore join are the two producers of a bucket, and both decide
/// its value through <see cref="CovenantDisclosureStateAlgebra"/>. One store between them and the table
/// keeps a second row encoding from appearing next to either: the empty state has exactly one shape,
/// and two writers that each spelled it out would be two chances to spell it differently.
///
/// <para>A persisted bucket is not the whole answer. Receipts in a subject's unfolded tail are counted
/// nowhere here; a reader that has to say what left this installation reads through
/// <see cref="ExternalDisclosureStateReader"/> instead (§10.13).</para>
/// </remarks>
internal static class ExternalDisclosureStateStore
{
    internal static async Task<List<CovenantDisclosureState>> ReadAllAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        List<CovenantDisclosureState> buckets = [];

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            SELECT DestinationCode, RevocabilityCode, CountKindCode, EverOccurred, JoinedCount,
                   MaxDisclosedAtUtcTicks, EvidenceBloom
            FROM external_disclosure_state
            ORDER BY DestinationCode, RevocabilityCode;
            """;

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            buckets.Add(Materialize(reader));
        }

        return buckets;
    }

    internal static async Task<CovenantDisclosureState?> ReadAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CovenantEgressDestination destination,
        CovenantDisclosureRevocability revocability,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

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

    /// <summary>
    /// Replaces one bucket with <paramref name="state"/>, stamping it with
    /// <paramref name="updatedAtUtc"/>.
    /// </summary>
    /// <remarks>
    /// The store formats the instant, not its callers, so the one persisted-instant writer for this
    /// table is the one the central codec's review inventory names.
    /// </remarks>
    internal static async Task WriteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CovenantDisclosureState state,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(transaction);

        ArgumentNullException.ThrowIfNull(state);

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

        _ = command.Parameters.AddWithValue("$now", UtcInstantText.Format(updatedAtUtc));

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds a bucket from a row whose first seven columns are the table's own, in table order.
    /// </summary>
    /// <remarks>
    /// Every value goes through the <see cref="CovenantDisclosureState"/> constructor, so a malformed
    /// code or shape throws <see cref="ArgumentException"/> rather than becoming a plausible bucket.
    /// The Bloom is read as one value rather than streamed: it is 32 bytes by CHECK, and a stream is a
    /// handle the hosted-producer inventory has to account for as an external effect.
    /// </remarks>
    internal static CovenantDisclosureState Materialize(SqliteDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return new CovenantDisclosureState(
            (CovenantEgressDestination)reader.GetInt32(0),
            (CovenantDisclosureRevocability)reader.GetInt32(1),
            (CovenantDisclosureCountKind)reader.GetInt32(2),
            reader.GetInt32(3) != 0,
            checked((ulong)reader.GetInt64(4)),
            reader.GetInt64(5),
            (byte[])reader.GetValue(6));
    }
}
