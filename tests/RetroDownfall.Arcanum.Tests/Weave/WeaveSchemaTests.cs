using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Weave;

/// <summary>
/// The Weave's durable tables are declared in the same <c>Data/Schema/</c> tree as every other
/// Grimoire object and installed by <see cref="GrimoireSchemaInstaller"/> (DESIGN §5.4.4, §21.2).
/// </summary>
public sealed class WeaveSchemaTests
{

    [Fact]

    public async Task InstallAsync_CreatesVersionedSessionAttachmentIndexTables()
    {

        await using SqliteConnection connection = new("Data Source=:memory:");

        await connection.OpenAsync();

        _ = await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            embeddingDimensions: 768,
            CancellationToken.None);

        string[] expectedTables =
        [

            "session_attachment_chunks",

            "session_attachment_embeddings",

            "session_attachment_index_state",

        ];

        foreach (string table in expectedTables)
        {

            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name";

            command.Parameters.AddWithValue("@name", table);

            Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);

        }

        string[] expectedChunkColumns =
        [

            "ChunkId",

            "GenerationId",

            "SessionId",

            "AttachmentId",

            "LogicalKey",

            "Version",

            "OriginalFileName",

            "MimeType",

            "ContentSha256",

            "ChunkIndex",

            "CharacterStart",

            "CharacterEnd",

            "StartLine",

            "EndLine",

            "Content",

            "EmbeddingDimension",

            "ExtractedAt",

            "IndexedAt",

            "RetrievalScope",

        ];

        List<string> actualColumns = [];

        await using SqliteCommand inspect = connection.CreateCommand();

        inspect.CommandText = "PRAGMA table_info('session_attachment_chunks')";

        await using SqliteDataReader reader = await inspect.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {

            actualColumns.Add(reader.GetString(1));

        }

        Assert.Equal(expectedChunkColumns, actualColumns);

        string[] expectedStateColumns =
        [

            "AttachmentId",

            "Status",

            "ContentSha256",

            "AttemptCount",

            "FailureReason",

            "ExtractedAt",

            "IndexedAt",

            "PublishedGenerationId",

            "PendingGenerationId",

            "NextChunkIndex",

            "PendingEmbeddingDimension",

            "PendingPipelineFingerprint",

            "PendingExtractedAt",

            "UpdatedAt",

        ];

        List<string> actualStateColumns = [];

        await using SqliteCommand inspectState = connection.CreateCommand();

        inspectState.CommandText = "PRAGMA table_info('session_attachment_index_state')";

        await using SqliteDataReader stateReader = await inspectState.ExecuteReaderAsync();

        while (await stateReader.ReadAsync())
        {

            actualStateColumns.Add(stateReader.GetString(1));

        }

        Assert.Equal(expectedStateColumns, actualStateColumns);

    }

    /// <summary>
    /// The installer installs, it never migrates. An existing Weave table with an older shape and no
    /// metadata row proving what wrote it makes the Core tier refuse outright, and the table is left
    /// exactly as it was found rather than upgraded in place (README, "Local Grimoire reinstall").
    /// </summary>
    /// <remarks>
    /// This used to be a silent no-op: every statement was <c>IF NOT EXISTS</c>, so the install
    /// simply stepped over the foreign table and carried on. Refusing is the stronger answer.
    /// Continuing meant the process ran against a database it could not account for, and the only
    /// evidence was a shape difference nobody was looking at.
    /// </remarks>
    [Fact]
    public async Task InstallAsync_DoesNotUpgradeExistingWorkspaceChunkTable()
    {

        await using SqliteConnection connection = await GrimoireSchemaTestInstaller.OpenAsync(
            "Data Source=:memory:",
            CancellationToken.None);

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

        GrimoireSchemaRefusedException refused =
            await Assert.ThrowsAsync<GrimoireSchemaRefusedException>(() =>
                GrimoireSchemaTestInstaller.InstallAsync(
                    connection,
                    embeddingDimensions: 768,
                    CancellationToken.None));

        Assert.Equal(GrimoireSchemaTierHealth.MetadataMissing, refused.Health);

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
