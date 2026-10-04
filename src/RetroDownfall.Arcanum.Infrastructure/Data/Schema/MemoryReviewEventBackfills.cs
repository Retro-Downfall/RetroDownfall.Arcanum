using Microsoft.Data.Sqlite;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Schema;

internal sealed class AnnalReviewEventBackfill : IGrimoireSchemaBackfill
{
    public string Name => "annal-review-current-heads";

    public int MaxRowsPerBatch => 200;

    public async Task<GrimoireSchemaBackfillBatch> AdvanceBatchAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? cursor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO annal_review_events
                (VersionId, ClaimId, SubjectStoreCode, SubjectId, OperationCode, OriginCode,
                 ScopeKindCode, CampaignId, SourceSessionId)
            SELECT version.VersionId, claim.ClaimId, claim.SubjectStoreCode, claim.SubjectId,
                   version.OperationCode, version.OriginCode, version.ScopeKindCode,
                   version.CampaignId, version.SourceSessionId
            FROM annal_heads AS head
            JOIN annal_versions AS version ON version.VersionId = head.CurrentVersionId
            JOIN annal_claims AS claim ON claim.ClaimId = head.ClaimId
            WHERE NOT EXISTS (
                SELECT 1 FROM annal_review_events AS review WHERE review.VersionId = version.VersionId)
            ORDER BY version.Sequence
            LIMIT $limit;
            """;
        _ = command.Parameters.AddWithValue("$limit", MaxRowsPerBatch);

        int written = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return new GrimoireSchemaBackfillBatch(
            NextCursor: null,
            written,
            IsComplete: written == 0);
    }
}

internal sealed class CovenantReviewEventBackfill : IGrimoireSchemaBackfill
{
    public string Name => "covenant-review-current-heads";

    public int MaxRowsPerBatch => 200;

    public async Task<GrimoireSchemaBackfillBatch> AdvanceBatchAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? cursor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO covenant_review_events
                (DatasetGeneration, VersionId, EntryId, LaneCode, OperationCode, OriginCode,
                 ScopeCode, CampaignId, SourceTurnId, SourceToolCallId)
            SELECT state.DatasetGeneration, version.VersionId, version.EntryId, version.LaneCode,
                   version.OperationCode, version.OriginCode, entry.ScopeCode, entry.CampaignId,
                   version.SourceTurnId, version.SourceToolCallId
            FROM covenant_heads AS head
            JOIN covenant_versions AS version ON version.VersionId = head.CurrentVersionId
            JOIN covenant_entries AS entry ON entry.EntryId = head.EntryId
            CROSS JOIN covenant_state AS state
            WHERE state.StateKey = 1
              AND NOT EXISTS (
                  SELECT 1 FROM covenant_review_events AS review WHERE review.VersionId = version.VersionId)
            ORDER BY version.CreatedAtUtc, version.VersionId
            LIMIT $limit;
            """;
        _ = command.Parameters.AddWithValue("$limit", MaxRowsPerBatch);

        int written = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return new GrimoireSchemaBackfillBatch(
            NextCursor: null,
            written,
            IsComplete: written == 0);
    }
}
