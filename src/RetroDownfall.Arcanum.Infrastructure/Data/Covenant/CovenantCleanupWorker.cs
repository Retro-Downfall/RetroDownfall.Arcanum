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

    public async ValueTask<Result<CovenantCleanupOutcome>> RunBatchAsync(
        CovenantCleanupLease cleanupLease,
        CovenantMutationTransaction transaction,
        CancellationToken cancellationToken,
        int maxEvents = DefaultBatchSize)
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
                "Covenant.StaleSnapshot",
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

        foreach (CovenantOwnerDeletionEvent owner in pending.Value)
        {

            cancellationToken.ThrowIfCancellationRequested();

            if (owner.Kind == CovenantOwnerKind.Campaign)
            {

                headsRemoved = checked(
                    headsRemoved + await CleanCampaignAsync(transaction, owner.OwnerId, cancellationToken)
                        .ConfigureAwait(false));

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

    private static async ValueTask<long> CleanCampaignAsync(
        CovenantMutationTransaction transaction,
        Guid campaignId,
        CancellationToken cancellationToken)
    {

        string campaign = campaignId.ToString("D");

        ImmutableArray<HeadIdentity> heads = await ReadCampaignHeadsAsync(transaction, campaign, cancellationToken)
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
                    heads.Count,
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
            : new Error("Covenant.Unavailable", "The Covenant canonical tier has no state row.");

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

    private readonly record struct HeadIdentity(
        int Ordinal,
        string EntryId,
        int LaneCode,
        long SearchRowId,
        string NormalizedKey);

}
