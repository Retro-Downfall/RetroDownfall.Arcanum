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

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            SELECT Id FROM saga_memories
            WHERE ($cursor IS NULL OR Id > $cursor)
                AND NOT EXISTS (SELECT 1 FROM long_rest_sources source WHERE source.MemoryId = saga_memories.Id)
            ORDER BY Id LIMIT $limit;
            """;

        _ = command.Parameters.AddWithValue("$cursor", (object?)cursor ?? DBNull.Value);

        _ = command.Parameters.AddWithValue("$limit", MaxRowsPerBatch);

        List<string> identities = [];

        await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                identities.Add(reader.GetString(0));
            }
        }

        command.Parameters.Clear();

        command.CommandText = "INSERT INTO long_rest_sources (MemoryId, MutationEpoch) VALUES ($memory, 1) ON CONFLICT(MemoryId) DO NOTHING;";

        SqliteParameter memory = command.Parameters.Add("$memory", SqliteType.Text);

        foreach (string identity in identities)
        {
            memory.Value = identity;

            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return new GrimoireSchemaBackfillBatch(
            identities.Count == 0 ? cursor : identities[^1], identities.Count, IsComplete: identities.Count < MaxRowsPerBatch);
    }
}
