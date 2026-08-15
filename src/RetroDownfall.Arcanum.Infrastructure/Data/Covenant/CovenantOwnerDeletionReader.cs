using System.Collections.Immutable;
using System.Globalization;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// Which core owner an ordered deletion event belongs to.
/// </summary>
public enum CovenantOwnerKind : byte
{

    Campaign = 1,

    Session = 2,

}

/// <summary>
/// One entry in the core owner-deletion journal, as the Covenant cleanup worker sees it.
/// </summary>
public sealed record CovenantOwnerDeletionEvent(long Sequence, CovenantOwnerKind Kind, Guid OwnerId);

/// <summary>
/// This capability family's position in that journal.
/// </summary>
/// <remarks>
/// The two cursors advance independently because the family may owe cleanup for one owner kind and
/// not the other; coupling them would stall the whole journal behind the slower kind.
/// </remarks>
public sealed record CovenantCleanupCursor(
    long AppliedCampaignSequence,
    long AppliedSessionSequence,
    bool FullSweepRequired);

/// <summary>
/// Reads the core owner-deletion journal on the Covenant family's behalf.
/// </summary>
/// <remarks>
/// Read-only, and deliberately the only Covenant component that touches the journal. Covenant never
/// appends an owner event: the core Campaign and Session delete triggers are the sole producer, so
/// deletion commits even when every Covenant object has been dropped.
/// </remarks>
internal sealed class CovenantOwnerDeletionReader
{

    private const int CovenantFamilyCode = (int)GrimoireSchemaFamily.Covenant;

    public async ValueTask<Result<CovenantCleanupCursor>> ReadCursorAsync(
        CovenantMutationTransaction transaction,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(transaction);

        await using SqliteCommand command = transaction.CreateCommand();

        command.CommandText = """
            SELECT AppliedCampaignSequence, AppliedSessionSequence, FullSweepRequired
            FROM capability_cleanup_state
            WHERE CapabilityFamilyCode = $family;
            """;

        _ = command.Parameters.AddWithValue("$family", CovenantFamilyCode);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new CovenantCleanupCursor(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2) != 0)
            : new Error(
                "Covenant.Unavailable",
                "The Covenant family has no cleanup cursor, so it cannot consume owner deletions.");

    }

    /// <summary>
    /// The next bounded, ordered batch of unapplied events, coalesced to one entry per owner.
    /// </summary>
    /// <remarks>
    /// Coalescing keeps the highest sequence for a repeated owner. An owner cannot be deleted twice,
    /// but a restored database can carry the same owner ID at two sequences, and cleaning it once at
    /// the later sequence is both correct and cheaper.
    /// </remarks>
    public async ValueTask<Result<ImmutableArray<CovenantOwnerDeletionEvent>>> ReadPendingAsync(
        CovenantCleanupCursor cursor,
        int maxEvents,
        CovenantMutationTransaction transaction,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(cursor);

        ArgumentNullException.ThrowIfNull(transaction);

        if (maxEvents < 1)
        {

            throw new ArgumentOutOfRangeException(nameof(maxEvents));

        }

        await using SqliteCommand command = transaction.CreateCommand();

        command.CommandText = """
            SELECT MAX(Sequence) AS Sequence, OwnerKindCode, OwnerId
            FROM owner_deletion_events
            WHERE (OwnerKindCode = 1 AND Sequence > $campaignCursor)
               OR (OwnerKindCode = 2 AND Sequence > $sessionCursor)
            GROUP BY OwnerKindCode, OwnerId
            ORDER BY Sequence
            LIMIT $limit;
            """;

        _ = command.Parameters.AddWithValue("$campaignCursor", cursor.AppliedCampaignSequence);

        _ = command.Parameters.AddWithValue("$sessionCursor", cursor.AppliedSessionSequence);

        _ = command.Parameters.AddWithValue("$limit", maxEvents);

        List<CovenantOwnerDeletionEvent> events = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            events.Add(
                new CovenantOwnerDeletionEvent(
                    reader.GetInt64(0),
                    (CovenantOwnerKind)reader.GetInt32(1),
                    Guid.Parse(reader.GetString(2), CultureInfo.InvariantCulture)));

        }

        return Result<ImmutableArray<CovenantOwnerDeletionEvent>>.Success([.. events]);

    }

    public async ValueTask<Result> AdvanceCursorAsync(
        long appliedCampaignSequence,
        long appliedSessionSequence,
        CovenantMutationTransaction transaction,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(transaction);

        await using SqliteCommand command = transaction.CreateCommand();

        // The cursors only move forward. A rewind would replay owner deletions the journal has
        // already retired, against rows that a later owner may since have created.
        command.CommandText = """
            UPDATE capability_cleanup_state
            SET AppliedCampaignSequence = MAX(AppliedCampaignSequence, $campaign),
                AppliedSessionSequence = MAX(AppliedSessionSequence, $session),
                UpdatedAtUtc = $updated
            WHERE CapabilityFamilyCode = $family;
            """;

        _ = command.Parameters.AddWithValue("$campaign", appliedCampaignSequence);

        _ = command.Parameters.AddWithValue("$session", appliedSessionSequence);

        _ = command.Parameters.AddWithValue(
            "$updated",
            DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture));

        _ = command.Parameters.AddWithValue("$family", CovenantFamilyCode);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1
            ? Result.Success()
            : new Error("Covenant.Unavailable", "The Covenant cleanup cursor row is missing.");

    }

}
