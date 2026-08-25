using System.Collections.Immutable;
using System.Globalization;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// What one bounded cleanup batch actually removed.
/// </summary>
public sealed record CovenantCleanupOutcome(
    int CampaignsCleaned,
    int SessionsCleaned,
    long AppliedCampaignSequence,
    long AppliedSessionSequence,
    long HeadsRemoved,
    bool SearchSequenceAdvanced);

/// <summary>
/// Applies core owner deletions to the encrypted canonical tier, one bounded batch at a time.
/// </summary>
/// <remarks>
/// The core deletion already committed; this worker is catching up. That ordering is what makes the
/// optional tier failure-isolated, and it is why every step here has to be idempotent: the same
/// event may be seen again after a crash between the deletes and the cursor advance.
///
/// <para>Event identity is rechecked inside the immediate transaction, so a batch chosen against one
/// journal state cannot be applied against another.</para>
/// </remarks>
internal sealed class CovenantCleanupWorker(
    ICovenantSqliteConnectionInitializer initializer,
    CovenantOwnerDeletionReader reader)
{

    internal const int DefaultBatchSize = 64;

    internal CovenantCleanupWorker()
        : this(CovenantSqliteConnectionInitializer.Instance, new CovenantOwnerDeletionReader())
    {
    }

    /// <summary>
    /// Applies one bounded batch of owner deletions to the canonical tier.
    /// </summary>
    /// <remarks>
    /// The bound is stated by the caller rather than defaulted. A default nobody overrides is a
    /// decision that looks made and is not, and this bound decides how much of a deletion backlog one
    /// pass drains — which is the difference between catching up and never finishing.
    /// <see cref="DefaultBatchSize"/> is the value a caller with no reason to choose otherwise states.
    /// </remarks>
    public async ValueTask<Result<CovenantCleanupOutcome>> RunBatchAsync(
        CovenantCleanupLease cleanupLease,
        CovenantMutationTransaction transaction,
        CancellationToken cancellationToken,
        int maxEvents)
    {

        ArgumentNullException.ThrowIfNull(cleanupLease);

        ArgumentNullException.ThrowIfNull(transaction);

        Result revalidated = await cleanupLease.RevalidateAsync(cancellationToken).ConfigureAwait(false);

        if (revalidated.IsFailure)
        {

            return revalidated.Error;

        }

        using CovenantSqliteAuthorizationScope authorization = initializer.Authorize(
            transaction.Connection,
            CovenantSqliteAuthorizationKind.OwnerCleanup);

        Guid? expectedGeneration = cleanupLease.Snapshot.DatasetGeneration;

        Result<Guid> generation = await ReadDatasetGenerationAsync(transaction, cancellationToken)
            .ConfigureAwait(false);

        if (generation.IsFailure)
        {

            return generation.Error;

        }

        if (expectedGeneration is { } captured && captured != generation.Value)
        {

            return new Error(
                ErrorCodes.Covenant.StaleSnapshot,
                "The Covenant dataset generation changed before this cleanup batch could apply.");

        }

        Result<CovenantCleanupCursor> cursor = await reader.ReadCursorAsync(transaction, cancellationToken)
            .ConfigureAwait(false);

        if (cursor.IsFailure)
        {

            return cursor.Error;

        }

        Result<ImmutableArray<CovenantOwnerDeletionEvent>> pending = await reader
            .ReadPendingAsync(cursor.Value, maxEvents, transaction, cancellationToken)
            .ConfigureAwait(false);

        if (pending.IsFailure)
        {

            return pending.Error;

        }

        long campaignCursor = cursor.Value.AppliedCampaignSequence;

        long sessionCursor = cursor.Value.AppliedSessionSequence;

        int campaigns = 0;

        int sessions = 0;

        long headsRemoved = 0;

        // The batch advances the canonical search sequence exactly once, so every delta it emits
        // shares that one sequence and they have to share one ordinal space too. Restarting the
        // ordinal per owner collided on covenant_search_outbox's (SearchSequence, Ordinal) primary
        // key the moment a batch held two head-bearing Campaigns: the insert threw, the whole
        // transaction rolled back with the cursor unmoved, and the next batch read the same events
        // and failed identically, so the journal could never drain again.
        int outboxOrdinal = 0;

        foreach (CovenantOwnerDeletionEvent owner in pending.Value)
        {

            cancellationToken.ThrowIfCancellationRequested();

            if (owner.Kind == CovenantOwnerKind.Campaign)
            {

                int removed = await CleanCampaignAsync(
                        transaction,
                        owner.OwnerId,
                        outboxOrdinal,
                        cancellationToken)
                    .ConfigureAwait(false);

                headsRemoved = checked(headsRemoved + removed);

                outboxOrdinal = checked(outboxOrdinal + removed);

                campaigns = checked(campaigns + 1);

                campaignCursor = Math.Max(campaignCursor, owner.Sequence);

            }
            else
            {

                await CleanSessionAsync(transaction, owner.OwnerId, cancellationToken).ConfigureAwait(false);

                sessions = checked(sessions + 1);

                sessionCursor = Math.Max(sessionCursor, owner.Sequence);

            }

        }

        // A deletion that removed no head produced no projection delta, so it must not advance the
        // search sequence: the accelerator would then see work it can never find.
        bool advanced = headsRemoved > 0;

        if (advanced)
        {

            await AdvanceSearchSequenceAsync(transaction, cancellationToken).ConfigureAwait(false);

        }

        Result cursorAdvanced = await reader
            .AdvanceCursorAsync(campaignCursor, sessionCursor, transaction, cancellationToken)
            .ConfigureAwait(false);

        return cursorAdvanced.IsFailure
            ? cursorAdvanced.Error
            : new CovenantCleanupOutcome(
                campaigns,
                sessions,
                campaignCursor,
                sessionCursor,
                headsRemoved,
                advanced);

    }

    /// <summary>
    /// Removes one Campaign's Covenant rows and returns how many heads it emitted deltas for.
    /// </summary>
    /// <param name="firstOrdinal">
    /// Where this owner's deltas start in the batch-wide ordinal space. The caller advances it by the
    /// returned count, so one batch produces ordinals 0..N-1 under its single search sequence.
    /// </param>
    private static async ValueTask<int> CleanCampaignAsync(
        CovenantMutationTransaction transaction,
        Guid campaignId,
        int firstOrdinal,
        CancellationToken cancellationToken)
    {

        string campaign = campaignId.ToString("D");

        ImmutableArray<HeadIdentity> heads = await ReadCampaignHeadsAsync(
                transaction,
                campaign,
                firstOrdinal,
                cancellationToken)
            .ConfigureAwait(false);

        // The key epoch is advanced by covenant_heads_key_epoch_delete, not here. Bumping it again
        // would double-count one deletion and make a legitimate preflight comparison fail twice over.

        foreach (HeadIdentity head in heads)
        {

            await ExecuteAsync(
                    transaction,
                    """
                    INSERT INTO covenant_search_outbox (SearchSequence, Ordinal, SearchRowId, EntryId, LaneCode, DesiredVersionId)
                    SELECT CanonicalSearchSequence + 1, $ordinal, $row, $entry, $lane, NULL
                    FROM covenant_state WHERE StateKey = 1;
                    """,
                    cancellationToken,
                    ("$ordinal", head.Ordinal),
                    ("$row", head.SearchRowId),
                    ("$entry", head.EntryId),
                    ("$lane", head.LaneCode))
                .ConfigureAwait(false);

        }

        // Children before parents: provenance, heads, versions, entries. A different order would
        // trip the composite foreign keys the canonical tier relies on.
        await ExecuteAsync(
                transaction,
                """
                DELETE FROM covenant_version_attachment_provenance
                WHERE VersionId IN (
                    SELECT v.VersionId FROM covenant_versions v
                    JOIN covenant_entries e ON e.EntryId = v.EntryId
                    WHERE e.CampaignId = $campaign);
                """,
                cancellationToken,
                ("$campaign", campaign))
            .ConfigureAwait(false);

        await ExecuteAsync(
                transaction,
                "DELETE FROM covenant_heads WHERE CampaignId = $campaign;",
                cancellationToken,
                ("$campaign", campaign))
            .ConfigureAwait(false);

        await ExecuteAsync(
                transaction,
                """
                DELETE FROM covenant_versions
                WHERE EntryId IN (SELECT EntryId FROM covenant_entries WHERE CampaignId = $campaign);
                """,
                cancellationToken,
                ("$campaign", campaign))
            .ConfigureAwait(false);

        await ExecuteAsync(
                transaction,
                "DELETE FROM covenant_entries WHERE CampaignId = $campaign;",
                cancellationToken,
                ("$campaign", campaign))
            .ConfigureAwait(false);

        await ExecuteAsync(
                transaction,
                "DELETE FROM covenant_turn_receipts WHERE CampaignId = $campaign;",
                cancellationToken,
                ("$campaign", campaign))
            .ConfigureAwait(false);

        await ExecuteAsync(
                transaction,
                "DELETE FROM covenant_mutation_receipts WHERE CampaignId = $campaign;",
                cancellationToken,
                ("$campaign", campaign))
            .ConfigureAwait(false);

        return heads.Length;

    }

    private static async ValueTask CleanSessionAsync(
        CovenantMutationTransaction transaction,
        Guid sessionId,
        CancellationToken cancellationToken)
    {

        string session = sessionId.ToString("D");

        await ExecuteAsync(
                transaction,
                "DELETE FROM covenant_turn_receipts WHERE SessionId = $session;",
                cancellationToken,
                ("$session", session))
            .ConfigureAwait(false);

        await ExecuteAsync(
                transaction,
                "DELETE FROM covenant_turn_receipt_aggregate WHERE SessionId = $session;",
                cancellationToken,
                ("$session", session))
            .ConfigureAwait(false);

    }

    private static async ValueTask<ImmutableArray<HeadIdentity>> ReadCampaignHeadsAsync(
        CovenantMutationTransaction transaction,
        string campaignId,
        int firstOrdinal,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = transaction.CreateCommand();

        command.CommandText = """
            SELECT EntryId, LaneCode, SearchRowId, NormalizedKey
            FROM covenant_heads
            WHERE CampaignId = $campaign
            ORDER BY SearchRowId;
            """;

        _ = command.Parameters.AddWithValue("$campaign", campaignId);

        List<HeadIdentity> heads = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            heads.Add(
                new HeadIdentity(
                    checked(firstOrdinal + heads.Count),
                    reader.GetString(0),
                    reader.GetInt32(1),
                    reader.GetInt64(2),
                    reader.GetString(3)));

        }

        return [.. heads];

    }

    private static async ValueTask<Result<Guid>> ReadDatasetGenerationAsync(
        CovenantMutationTransaction transaction,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = transaction.CreateCommand();

        command.CommandText = "SELECT DatasetGeneration FROM covenant_state WHERE StateKey = 1;";

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is byte[] bytes
            ? new Guid(bytes)
            : new Error(ErrorCodes.Covenant.Unavailable, "The Covenant canonical tier has no state row.");

    }

    private static async ValueTask AdvanceSearchSequenceAsync(
        CovenantMutationTransaction transaction,
        CancellationToken cancellationToken) =>
        await ExecuteAsync(
                transaction,
                """
                UPDATE covenant_state
                SET CanonicalSearchSequence = CanonicalSearchSequence + 1, UpdatedAtUtc = $updated
                WHERE StateKey = 1;
                """,
                cancellationToken,
                ("$updated", NowIso()))
            .ConfigureAwait(false);

    private static async ValueTask<int> ExecuteAsync(
        CovenantMutationTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {

        await using SqliteCommand command = transaction.CreateCommand();

        command.CommandText = sql;

        foreach ((string name, object value) in parameters)
        {

            _ = command.Parameters.AddWithValue(name, value);

        }

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    private static string NowIso() =>
        DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

    /// <summary>
    /// One head this batch will remove, with its position in the batch-wide outbox ordinal space.
    /// </summary>
    private readonly record struct HeadIdentity(
        int Ordinal,
        string EntryId,
        int LaneCode,
        long SearchRowId,
        string NormalizedKey);

}
