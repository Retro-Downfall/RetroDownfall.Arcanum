using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;
using SQLitePCL;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class NativeMemorySearchReaderTests
{
    [Fact]
    public async Task Projection_storage_and_trigger_write_cost_are_reported_without_an_arbitrary_ratio_gate()
    {
        foreach (int version in new[] { 27, 28 })
        {
            using var file = EvolutionScratchDatabase.Create();
            await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);
            await GrimoireSchemaTestInstaller.InstallAsync(connection, version == 27
                ? CoreSchemaVersionTwentySevenFixture.ChainSet() : GrimoireSchemaVersionChains.Default, 64, CancellationToken.None);
            long beforeBytes = await StorageBytesAsync(connection);
            int beforeChanges = raw.sqlite3_total_changes(connection.Handle);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                WITH RECURSIVE n(v) AS (VALUES(1) UNION ALL SELECT v+1 FROM n WHERE v<1000)
                INSERT INTO workspace_file_chunks(ChunkId,WorkspacePath,RelativePath,ChunkIndex,Content,CharOffset,CharLength,FileLastWriteTime,IndexedAt)
                SELECT printf('%064d',v),'/fake/workspace','same.cs',v,@content,0,192,'2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z' FROM n;
                """;
            _ = command.Parameters.AddWithValue("@content", "commontoken " + new string('x', 180));
            _ = await command.ExecuteNonQueryAsync();
            long afterBytes = await StorageBytesAsync(connection);
            int changed = raw.sqlite3_total_changes(connection.Handle) - beforeChanges;
            long projectionPayload = 0;
            if (version == 28)
            {
                command.CommandText = "SELECT SUM(octet_length(Content)+octet_length(AuxiliaryOne)+octet_length(AuxiliaryTwo)) FROM memory_search_documents";
                projectionPayload = (long)(await command.ExecuteScalarAsync())!;
                Assert.Equal(199000, projectionPayload);
            }
            output.WriteLine($"Measured Core{version}: source rows=1000; source content bytes=192000; aggregate database page growth={afterBytes - beforeBytes}; sqlite3_total_changes delta={changed}; duplicate normalized searchable-field bytes={projectionPayload}. Includes source/revision/index effects; no isolated FTS allocation or timing claim.");
        }
    }

    private static async Task<long> StorageBytesAsync(SqliteConnection connection)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT page_count*page_size FROM pragma_page_count(),pragma_page_size()";
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
