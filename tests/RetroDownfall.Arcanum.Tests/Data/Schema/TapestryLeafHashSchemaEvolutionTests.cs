using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

/// <summary>
/// Core version 15 adds <c>tapestry_leaf_hashes</c>, the per-leaf content hashes the Tapestry fingerprints a scope
/// from, and one insert, one update and one delete trigger on each corpus table that drop a hash whenever the row
/// it describes changes. A fresh installation and a version-14 installation that has years of chunks and entries
/// must end with the same objects, and on both a stored hash must never outlive the text it was computed from.
/// </summary>
public sealed class TapestryLeafHashSchemaEvolutionTests
{
    private const string SessionId = "AAAAAAAA-1111-4111-8111-AAAAAAAAAAAA";

    private static readonly string[] TriggerNames =
    [
        .. new[] { "workspace_file_chunks", "session_attachment_chunks", "Entries" }
            .SelectMany(static table => new[] { "insert", "update", "delete" }
                .Select(operation => $"{table}_drop_tapestry_leaf_hash_{operation}")),
    ];

    [Fact]
    public async Task Version_fifteen_adds_the_table_and_its_triggers_without_touching_existing_rows()
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        await InstallAsync(connection, CoreSchemaVersionFourteenFixture.ChainSet(), 14);

        // A version-14 installation has none of it, and has chunks the Tapestry has never hashed.
        Assert.Empty(await ObjectNamesAsync(connection));

        await InsertWorkspaceChunkAsync(connection, "chunk-1", "body");

        await InstallAsync(connection, GrimoireSchemaVersionChains.Default, GrimoireSchemaVersionChains.CoreSchemaVersion);

        string[] expected = [.. TriggerNames.Append("tapestry_leaf_hashes").Order(StringComparer.Ordinal)];

        Assert.Equal(expected, await ObjectNamesAsync(connection));

        // Nothing is backfilled: the Tapestry stores a hash the first time it needs one.
        Assert.Equal(0, await ScalarAsync(connection, "SELECT COUNT(*) FROM tapestry_leaf_hashes"));

        Assert.Equal(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM workspace_file_chunks"));
    }

    [Fact]
    public async Task An_evolved_installation_and_a_fresh_one_hold_the_same_definitions()
    {
        using EvolutionScratchDatabase evolvedFile = EvolutionScratchDatabase.Create();

        using EvolutionScratchDatabase freshFile = EvolutionScratchDatabase.Create();

        await using SqliteConnection evolved = await evolvedFile.OpenAsync(CancellationToken.None);

        await using SqliteConnection fresh = await freshFile.OpenAsync(CancellationToken.None);

        await InstallAsync(evolved, CoreSchemaVersionFourteenFixture.ChainSet(), 14);

        await InstallAsync(evolved, GrimoireSchemaVersionChains.Default, GrimoireSchemaVersionChains.CoreSchemaVersion);

        await InstallAsync(fresh, GrimoireSchemaVersionChains.Default, GrimoireSchemaVersionChains.CoreSchemaVersion);

        Assert.Equal(await DefinitionsAsync(fresh), await DefinitionsAsync(evolved));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_stored_hash_does_not_outlive_the_workspace_chunk_it_describes(bool evolved)
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        await InstallAsync(connection, evolved);

        await AssertHashFollowsTheRowAsync(
            connection,
            "WorkspaceFileChunk",
            "workspace_file_chunks",
            "ChunkId",
            "IndexedAt = '2026-02-02T00:00:00Z'",
            id => InsertWorkspaceChunkAsync(connection, id, "body"),
            id => InsertWorkspaceChunkAsync(connection, id, "replacement", replace: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_stored_hash_does_not_outlive_the_attachment_chunk_it_describes(bool evolved)
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        await InstallAsync(connection, evolved);

        await ExecuteAsync(connection, "INSERT INTO \"Sessions\" (\"Id\", \"Status\", \"CreatedAt\", \"UpdatedAt\") VALUES ('" + SessionId + "', 'active', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z')");

        await AssertHashFollowsTheRowAsync(
            connection,
            "SessionAttachmentChunk",
            "session_attachment_chunks",
            "ChunkId",
            "IndexedAt = '2026-02-02T00:00:00Z'",
            id => InsertAttachmentChunkAsync(connection, id, "body"),
            id => InsertAttachmentChunkAsync(connection, id, "replacement", replace: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_stored_hash_does_not_outlive_the_entry_it_describes(bool evolved)
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        await InstallAsync(connection, evolved);

        await ExecuteAsync(connection, "INSERT INTO \"Sessions\" (\"Id\", \"Status\", \"CreatedAt\", \"UpdatedAt\") VALUES ('" + SessionId + "', 'active', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z')");

        await AssertHashFollowsTheRowAsync(
            connection,
            "Entry",
            "Entries",
            "Id",
            "IsPinned = 1",
            id => InsertEntryAsync(connection, id, "body"),
            id => InsertEntryAsync(connection, id, "replacement", replace: true));
    }

    /// <summary>
    /// A hash stored beside a row follows that row: it survives a write that leaves the text alone, and it is
    /// dropped by every write that changes it, whichever statement makes it.
    /// </summary>
    private static async Task AssertHashFollowsTheRowAsync(
        SqliteConnection connection,
        string sourceKind,
        string table,
        string idColumn,
        string unrelatedAssignment,
        Func<string, Task> insert,
        Func<string, Task> replace)
    {
        // A row's id is the same spelling in every table here: the canonical uppercase one an Entry requires.
        string id = "BBBBBBBB-2222-4222-8222-BBBBBBBBBBBB";

        // A hash stored for an id before any row of that id exists is dropped by the row's insert.
        await StoreHashAsync(connection, sourceKind, id);

        await insert(id);

        Assert.Equal(0, await HashCountAsync(connection, sourceKind, id));

        await StoreHashAsync(connection, sourceKind, id);

        await ExecuteAsync(connection, $"UPDATE {table} SET Content = Content, {unrelatedAssignment} WHERE {idColumn} = '{id}'");

        Assert.Equal(1, await HashCountAsync(connection, sourceKind, id));

        await ExecuteAsync(connection, $"UPDATE {table} SET Content = 'an edited body' WHERE {idColumn} = '{id}'");

        Assert.Equal(0, await HashCountAsync(connection, sourceKind, id));

        await StoreHashAsync(connection, sourceKind, id);

        await replace(id);

        Assert.Equal(0, await HashCountAsync(connection, sourceKind, id));

        await StoreHashAsync(connection, sourceKind, id);

        await ExecuteAsync(connection, $"DELETE FROM {table} WHERE {idColumn} = '{id}'");

        Assert.Equal(0, await HashCountAsync(connection, sourceKind, id));
    }

    private static Task InstallAsync(SqliteConnection connection, bool evolved) =>
        evolved
            ? InstallEvolvedAsync(connection)
            : InstallAsync(connection, GrimoireSchemaVersionChains.Default, GrimoireSchemaVersionChains.CoreSchemaVersion);

    private static async Task InstallEvolvedAsync(SqliteConnection connection)
    {
        await InstallAsync(connection, CoreSchemaVersionFourteenFixture.ChainSet(), 14);

        await InstallAsync(connection, GrimoireSchemaVersionChains.Default, GrimoireSchemaVersionChains.CoreSchemaVersion);
    }

    private static async Task InstallAsync(SqliteConnection connection, GrimoireSchemaVersionChainSet chains, int version)
    {
        GrimoireSchemaInstallResult result = await GrimoireSchemaTestInstaller.InstallAsync(
            connection, chains, 1536, CancellationToken.None);

        Assert.Equal(GrimoireSchemaTierHealth.Healthy, result.Core.Health);

        Assert.Equal(version, result.Core.SchemaVersion);
    }

    private static Task InsertWorkspaceChunkAsync(SqliteConnection connection, string id, string content, bool replace = false) =>
        ExecuteAsync(
            connection,
            $"""
            {(replace ? "INSERT OR REPLACE" : "INSERT")} INTO workspace_file_chunks
                (ChunkId, WorkspacePath, RelativePath, ChunkIndex, Content, CharOffset, CharLength,
                 StartLine, EndLine, FileLastWriteTime, IndexedAt)
            VALUES ('{id}', '/repo', 'a.cs', 0, '{content}', 0, 10, 1, 3, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z')
            """);

    private static async Task InsertAttachmentChunkAsync(SqliteConnection connection, string id, string content, bool replace = false)
    {
        await ExecuteAsync(
            connection,
            $"""
            INSERT OR IGNORE INTO "SessionAttachments"
                ("Id", "SessionId", "EntryId", "PendingTurnId", "State", "LogicalKey", "OriginalFileName", "Version",
                 "RelativePath", "ContentSha256", "MimeType", "ByteLength", "Kind", "CreatedAt")
            VALUES ('CCCCCCCC-3333-4333-8333-CCCCCCCCCCCC', '{SessionId}', NULL, NULL, 'Bound', 'notes', 'notes.md', 1,
                    'notes.md', 'ATTACHMENT-HASH', 'text/plain', 16, 'Text', '2026-01-01T00:00:00Z')
            """);

        await ExecuteAsync(
            connection,
            $"""
            {(replace ? "INSERT OR REPLACE" : "INSERT")} INTO session_attachment_chunks
                (ChunkId, GenerationId, SessionId, AttachmentId, LogicalKey, Version, OriginalFileName, MimeType,
                 ContentSha256, ChunkIndex, CharacterStart, CharacterEnd, StartLine, EndLine, Content,
                 EmbeddingDimension, ExtractedAt, IndexedAt, RetrievalScope)
            VALUES ('{id}', 'generation-1', '{SessionId.ToLowerInvariant()}', 'CCCCCCCC-3333-4333-8333-CCCCCCCCCCCC',
                    'notes', 1, 'notes.md', 'text/plain', 'ATTACHMENT-HASH', 0, 0, 16, 1, 1, '{content}', 8,
                    '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z', 'Latest')
            """);
    }

    private static Task InsertEntryAsync(SqliteConnection connection, string id, string content, bool replace = false) =>
        ExecuteAsync(
            connection,
            $"""
            {(replace ? "INSERT OR REPLACE" : "INSERT")} INTO "Entries"
                ("Id", "SessionId", "Role", "Content", "ModelUsed", "CreatedAt", "Sequence")
            VALUES ('{id}', '{SessionId}', 1, '{content}', 'model', '2026-01-01T00:00:00Z', 1)
            """);

    private static Task StoreHashAsync(SqliteConnection connection, string sourceKind, string id) =>
        ExecuteAsync(
            connection,
            $"""
            INSERT OR REPLACE INTO tapestry_leaf_hashes (SourceKind, SourceId, ContentSha256, IsBlank)
            VALUES ('{sourceKind}', '{id}', '{new string('a', 64)}', 0)
            """);

    private static async Task<long> HashCountAsync(SqliteConnection connection, string sourceKind, string id) =>
        await ScalarAsync(
            connection,
            $"SELECT COUNT(*) FROM tapestry_leaf_hashes WHERE SourceKind = '{sourceKind}' AND SourceId = '{id}'");

    /// <summary>The names of the objects this version adds, as the database holds them.</summary>
    private static async Task<string[]> ObjectNamesAsync(SqliteConnection connection)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT name FROM sqlite_master
            WHERE name = 'tapestry_leaf_hashes' OR name LIKE '%\_drop\_tapestry\_leaf\_hash\_%' ESCAPE '\'
            ORDER BY name
            """;

        List<string> names = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return [.. names];
    }

    /// <summary>Each object's stored DDL, which is what the installer compares evolved and fresh installations by.</summary>
    private static async Task<string[]> DefinitionsAsync(SqliteConnection connection)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT name || ' => ' || sql FROM sqlite_master
            WHERE name = 'tapestry_leaf_hashes' OR name LIKE '%\_drop\_tapestry\_leaf\_hash\_%' ESCAPE '\'
            ORDER BY name
            """;

        List<string> definitions = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            definitions.Add(reader.GetString(0));
        }

        return [.. definitions];
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
