using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Weave;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Schema;

/// <summary>
/// Gives every Saga memory written before version 2 the explicit scope its owning Session's binding
/// already states.
/// </summary>
/// <remarks>
/// The sweep reads <c>session_campaign_bindings</c> and never <c>Sessions.CampaignId</c>. The binding
/// is the canonical authority a Session carries; the navigation column on <c>Sessions</c> is legacy and
/// is what the binding was introduced to stop anyone reading as authority.
///
/// <para>The decision itself belongs to <see cref="SagaMemoryScopeClassifier"/>, which the live writer
/// also uses. Two copies of it would be two ideas of what a missing binding means, and a memory would
/// end up retrievable in one Campaign after an upgrade and in none after a fresh write.</para>
///
/// <para>There is no cursor. The batch's own predicate is <c>ScopeKindCode = 0</c> and every row it
/// reads is classified in the same transaction, so the corpus shrinks by exactly the work that
/// committed. A crash between the work and its commit leaves those rows unclassified and the next pass
/// selects them again, which is the resumability the interface asks for, reached without a position
/// that could be advanced past uncommitted work.</para>
/// </remarks>
internal sealed class SagaMemoryCampaignScopeBackfill : IGrimoireSchemaBackfill
{

    /// <summary>Recorded in the transition journal, so a resumed run can prove it is this sweep.</summary>
    public string Name => "saga-memory-campaign-scope";

    /// <summary>
    /// Small enough that one batch's transaction never holds the database while an operator is waiting
    /// for a turn, and large enough that an ordinary store drains in a pass or two.
    /// </summary>
    public int MaxRowsPerBatch => 200;

    public async Task<GrimoireSchemaBackfillBatch> AdvanceBatchAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? cursor,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(transaction);

        List<(string Id, SagaMemoryScopeKind Kind, string? CampaignId)> classified =
            await ReadBatchAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

        if (classified.Count == 0)
        {

            return new GrimoireSchemaBackfillBatch(NextCursor: null, RowsProcessed: 0, IsComplete: true);

        }

        await using SqliteCommand update = connection.CreateCommand();

        update.Transaction = transaction;

        // The ScopeKindCode = 0 guard makes a re-run of a batch whose commit was lost a no-op for any
        // row another pass has since classified, rather than a second, possibly different, answer.
        update.CommandText = """
            UPDATE saga_memories
            SET ScopeKindCode = $scopeKindCode, CampaignId = $campaignId
            WHERE Id = $id AND ScopeKindCode = 0;
            """;

        SqliteParameter scopeKindCode = update.Parameters.Add("$scopeKindCode", SqliteType.Integer);

        SqliteParameter campaignId = update.Parameters.Add("$campaignId", SqliteType.Text);

        SqliteParameter id = update.Parameters.Add("$id", SqliteType.Text);

        foreach ((string memoryId, SagaMemoryScopeKind kind, string? owner) in classified)
        {

            scopeKindCode.Value = (long)kind;

            campaignId.Value = (object?)owner ?? DBNull.Value;

            id.Value = memoryId;

            _ = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        }

        return new GrimoireSchemaBackfillBatch(
            NextCursor: null,
            classified.Count,
            IsComplete: false);

    }

    /// <summary>
    /// Reads one bounded page of unclassified memories with the binding each one's Session carries, and
    /// decides every row's scope before the first write.
    /// </summary>
    /// <remarks>
    /// The read completes before the update starts. The selecting query filters on the very column the
    /// update writes, and writing to a table an open cursor is still filtering on is the case SQLite
    /// leaves undefined - the same reason the Session binding backfill materializes its work first.
    /// </remarks>
    private async Task<List<(string Id, SagaMemoryScopeKind Kind, string? CampaignId)>> ReadBatchAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand read = connection.CreateCommand();

        read.Transaction = transaction;

        read.CommandText = """
            SELECT memory.Id, memory.SessionId, binding.BindingKindCode, binding.CampaignId
            FROM saga_memories AS memory
            LEFT JOIN session_campaign_bindings AS binding ON binding.SessionId = memory.SessionId
            WHERE memory.ScopeKindCode = 0
            ORDER BY memory.Id
            LIMIT $limit;
            """;

        _ = read.Parameters.AddWithValue("$limit", MaxRowsPerBatch);

        List<(string Id, SagaMemoryScopeKind Kind, string? CampaignId)> classified = [];

        await using SqliteDataReader reader =
            await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            (SagaMemoryScopeKind kind, string? owner) = SagaMemoryScopeClassifier.Classify(
                hasSession: !reader.IsDBNull(1),
                bindingKindCode: reader.IsDBNull(2) ? null : reader.GetInt64(2),
                boundCampaignId: reader.IsDBNull(3) ? null : reader.GetString(3));

            classified.Add((reader.GetString(0), kind, owner));

        }

        return classified;

    }

}
