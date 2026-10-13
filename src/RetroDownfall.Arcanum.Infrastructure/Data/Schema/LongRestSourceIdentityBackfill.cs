using Microsoft.Data.Sqlite;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Schema;

/// <summary>Assigns stable insertion identities in the same transaction as each bounded cursor.</summary>
internal sealed class LongRestSourceIdentityBackfill : IGrimoireSchemaBackfill
{
    public string Name => "long-rest-source-identities";

    public int MaxRowsPerBatch => 128;

    public async Task<GrimoireSchemaBackfillBatch> AdvanceBatchAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? cursor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(transaction);

        List<string> identities = [];

        await using (SqliteCommand select = connection.CreateCommand())
        {
            select.Transaction = transaction;

            select.CommandText = """
                SELECT Id FROM saga_memories
                WHERE ($cursor IS NULL OR Id > $cursor)
                    AND NOT EXISTS (SELECT 1 FROM long_rest_sources source WHERE source.MemoryId = saga_memories.Id)
                ORDER BY Id LIMIT $limit;
                """;

            _ = select.Parameters.AddWithValue("$cursor", (object?)cursor ?? DBNull.Value);

            _ = select.Parameters.AddWithValue("$limit", MaxRowsPerBatch);

            await using SqliteDataReader reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                identities.Add(reader.GetString(0));
            }
        }

        // A second command instead of clearing the first one's parameters keeps each command's
        // parameter set fixed for its whole life, which is the shape the hosted inventory classifies.
        await using SqliteCommand insert = connection.CreateCommand();

        insert.Transaction = transaction;

        insert.CommandText = "INSERT INTO long_rest_sources (MemoryId, MutationEpoch) VALUES ($memory, 1) ON CONFLICT(MemoryId) DO NOTHING;";

        SqliteParameter memory = insert.Parameters.Add("$memory", SqliteType.Text);

        foreach (string identity in identities)
        {
            memory.Value = identity;

            _ = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return new GrimoireSchemaBackfillBatch(
            identities.Count == 0 ? cursor : identities[^1], identities.Count, IsComplete: identities.Count < MaxRowsPerBatch);
    }
}
