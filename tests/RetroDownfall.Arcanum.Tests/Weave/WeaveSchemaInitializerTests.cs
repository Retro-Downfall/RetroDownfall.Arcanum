using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Weave;

namespace RetroDownfall.Arcanum.Tests.Weave;

public sealed class WeaveSchemaInitializerTests
{

    [Fact]
    public async Task EnsureSchemaAsync_DoesNotUpgradeExistingWorkspaceChunkTable()
    {

        await using SqliteConnection connection = new("Data Source=:memory:");

        await connection.OpenAsync();

        await using (SqliteCommand create = connection.CreateCommand())
        {

            create.CommandText =
                """
                CREATE TABLE workspace_file_chunks (
                    ChunkId TEXT PRIMARY KEY,
                    WorkspacePath TEXT NOT NULL,
                    RelativePath TEXT NOT NULL,
                    ChunkIndex INTEGER NOT NULL,
                    Content TEXT NOT NULL,
                    CharOffset INTEGER NOT NULL,
                    CharLength INTEGER NOT NULL,
                    FileLastWriteTime TEXT NOT NULL,
                    IndexedAt TEXT NOT NULL
                );
                """;

            _ = await create.ExecuteNonQueryAsync();

        }

        await WeaveSchemaInitializer.EnsureSchemaAsync(
            connection,
            configuredDimensions: 768,
            availability: new WeaveIndexAvailability(),
            logger: null,
            CancellationToken.None);

        List<string> columns = [];

        await using SqliteCommand inspect = connection.CreateCommand();

        inspect.CommandText = """PRAGMA table_info("workspace_file_chunks");""";

        await using SqliteDataReader reader = await inspect.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {

            columns.Add(reader.GetString(1));

        }

        Assert.DoesNotContain("StartLine", columns);
        Assert.DoesNotContain("EndLine", columns);

    }

}
