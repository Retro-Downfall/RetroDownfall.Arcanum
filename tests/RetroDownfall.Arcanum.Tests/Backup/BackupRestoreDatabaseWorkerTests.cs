using System.Text.Json.Nodes;

using System.Text.RegularExpressions;

using Microsoft.Data.Sqlite;

using SQLitePCL;

using RetroDownfall.Arcanum.Core.Backup;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Backup;

/// <summary>
/// Focused coverage for the staged-database mutations a restore performs, exercised against a plain
/// SQLite database rather than the whole restore pipeline.
/// </summary>
public sealed class BackupRestoreDatabaseWorkerTests
{

    /// <summary>
    /// These tests construct SQLite connections directly, so the provider has to be installed first.
    /// Without this the class only passes when some earlier test in the run happened to initialize
    /// it, which makes a filtered run fail for a reason that has nothing to do with the worker.
    /// </summary>
    static BackupRestoreDatabaseWorkerTests() => SqliteNativeRuntime.Instance.Initialize();

    [Fact]
    public async Task Sanctum_allow_lists_tolerate_non_string_entries_alongside_real_paths()
    {

        await using SqliteConnection connection = await OpenCampaignsAsync(
            """
            {"allowedPaths":["/home/old/campaigns/alpha",42,true,{"x":1},[1],null,"/home/old/other"]}
            """);

        BackupPathRemapper remapper = Remapper(
            new BackupPathMapping(
                BackupPathMappingKind.CampaignRoot,
                "/home/old/campaigns",
                "/Users/new/campaigns"));

        BackupRestoreRemapOutcome outcome = await BackupRestoreDatabaseWorker.RemapAsync(
            connection,
            remapper,
            CancellationToken.None);

        Assert.Equal(1, outcome.MatchesByKind[BackupPathMappingKind.CampaignRoot]);

        Assert.Equal("/home/old/other", Assert.Single(outcome.UnmappedNonportablePaths));

        JsonArray allowed = await ReadAllowedPathsAsync(connection);

        Assert.Equal(7, allowed.Count);

        Assert.Equal("/Users/new/campaigns/alpha", allowed[0]!.GetValue<string>());

        Assert.Equal(42, allowed[1]!.GetValue<int>());

        Assert.True(allowed[2]!.GetValue<bool>());

        Assert.Equal("/home/old/other", allowed[6]!.GetValue<string>());

    }

    [Fact]
    public async Task A_sanctum_allow_list_that_is_entirely_non_string_leaves_the_row_untouched()
    {

        await using SqliteConnection connection = await OpenCampaignsAsync(
            """
            {"allowedPaths":[1,2,3]}
            """);

        BackupRestoreRemapOutcome outcome = await BackupRestoreDatabaseWorker.RemapAsync(
            connection,
            Remapper(
                new BackupPathMapping(
                    BackupPathMappingKind.CampaignRoot,
                    "/home/old/campaigns",
                    "/Users/new/campaigns")),
            CancellationToken.None);

        Assert.Empty(outcome.UnmappedNonportablePaths);

        JsonArray allowed = await ReadAllowedPathsAsync(connection);

        Assert.Equal(3, allowed.Count);

    }

    /// <summary>
    /// Reconciling the vector mirror costs one pass over each table rather than a scan of the base
    /// table for every mirror row.
    /// </summary>
    /// <remarks>
    /// A restore runs this under the maintenance lock, over tables that are as large as the Grimoire
    /// is, so the shape of the work is a property worth pinning rather than an implementation detail.
    /// Comparing normalised identities inside the statement — <c>lower(replace(key, '-', ''))</c> on
    /// the base side — makes the base table's primary key unusable, so a correlated sweep degrades to
    /// one full scan per mirror row.
    ///
    /// <para>Asserted over the statements production actually executed, captured through SQLite's own
    /// trace hook and then planned back: no single statement may read more than one table. A
    /// source-level assertion about the SQL text would pass the day someone wrote a different join
    /// with the same cost.</para>
    ///
    /// <para>The behaviour is asserted in the same case, because a sweep that issued no statements at
    /// all would satisfy every plan assertion here. The kept identity is deliberately spelled
    /// uppercase-dashed in the base table and lowercase dash-free in the mirror — the two spellings
    /// these two families actually use — so a rewrite that dropped the normalisation would delete a
    /// mirror row whose vector is still live.</para>
    /// </remarks>
    [Fact]
    public async Task The_vector_mirror_sweep_never_scans_the_base_table_once_per_mirror_row()
    {

        await using SqliteConnection connection = await OpenEmbeddingsAsync();

        List<string> executed = [];

        bool capturing = true;

        raw.sqlite3_trace(
            connection.Handle,
            (_, statement) =>
            {

                if (capturing)
                {

                    executed.Add(statement);

                }

            },
            null);

        long removed = await BackupRestoreDatabaseWorker.DropMismatchedEmbeddingsAsync(
            connection,
            1536,
            CancellationToken.None);

        capturing = false;

        Assert.Equal(1, removed);

        Assert.Equal([KeptIdentity.ToUpperInvariant()], await IdentitiesAsync(connection, "entry_embeddings"));

        // The mirror row for the surviving vector stays, spelled the way the mirror spells one; the
        // one whose base row was just dropped and the one that never had a base row are both gone.
        Assert.Equal(
            [KeptIdentity.Replace("-", string.Empty).ToLowerInvariant()],
            await IdentitiesAsync(connection, "entry_embeddings_vec"));

        Assert.NotEmpty(executed);

        foreach (string statement in executed)
        {

            IReadOnlyList<string> reads = PlannedTableReads(connection, statement);

            Assert.True(
                reads.Count <= 1,
                $"One statement reads {reads.Count} tables — {string.Join(" then ", reads)} — so the "
                + $"mirror sweep costs a pass over the base table per mirror row: {statement}");

        }

    }

    /// <summary>
    /// The sweep skips a legacy virtual mirror, which this runtime has no module to open, and still
    /// sweeps a plain one in the same restore.
    /// </summary>
    /// <remarks>
    /// An FTS5 virtual table stands in for the <c>vec0</c> mirror, because it records the same
    /// <c>CREATE VIRTUAL TABLE</c> text, which is all that classifying a mirror reads. The stand-in can
    /// be read and deleted from, so every one of its rows still being there afterwards, orphan
    /// included, is what shows no statement reached it.
    /// </remarks>
    [Fact]
    public async Task The_vector_mirror_sweep_skips_a_legacy_virtual_mirror_and_still_sweeps_a_plain_one()
    {

        await using SqliteConnection connection = await OpenEmbeddingsAsync(legacyVirtualMirror: true);

        await using (SqliteCommand attachments = connection.CreateCommand())
        {

            attachments.CommandText = """
                CREATE TABLE "session_attachment_embeddings" (
                    "ChunkId" TEXT NOT NULL PRIMARY KEY,
                    "Embedding" BLOB NOT NULL,
                    "Dim" INTEGER NOT NULL);

                CREATE TABLE "session_attachment_embeddings_vec" (
                    "ChunkId" TEXT NOT NULL PRIMARY KEY,
                    "Embedding" BLOB NOT NULL);

                INSERT INTO "session_attachment_embeddings" VALUES ('kept-chunk', X'00', 1536);

                INSERT INTO "session_attachment_embeddings_vec" VALUES ('kept-chunk', X'00');

                INSERT INTO "session_attachment_embeddings_vec" VALUES ('orphaned-chunk', X'00');
                """;

            _ = await attachments.ExecuteNonQueryAsync();

        }

        long removed = await BackupRestoreDatabaseWorker.DropMismatchedEmbeddingsAsync(
            connection,
            1536,
            CancellationToken.None);

        Assert.Equal(1, removed);

        Assert.Equal(
            [
                KeptIdentity.Replace("-", string.Empty).ToLowerInvariant(),
                DroppedIdentity.Replace("-", string.Empty).ToLowerInvariant(),
                OrphanedIdentity.Replace("-", string.Empty).ToLowerInvariant(),
            ],
            await IdentitiesAsync(connection, "entry_embeddings_vec"));

        await using SqliteCommand read = connection.CreateCommand();

        read.CommandText = "SELECT \"ChunkId\" FROM \"session_attachment_embeddings_vec\";";

        Assert.Equal("kept-chunk", Assert.IsType<string>(await read.ExecuteScalarAsync()));

    }

    [Theory]
    [InlineData("entry_embeddings", "entry_embeddings_vec", "EntryId")]
    [InlineData("session_attachment_embeddings", "session_attachment_embeddings_vec", "ChunkId")]
    [InlineData("workspace_file_embeddings", "workspace_file_embeddings_vec", "ChunkId")]
    [InlineData("saga_memory_embeddings", "saga_memory_embeddings_vec", "MemoryId")]
    [InlineData("tapestry_node_embeddings", "tapestry_node_embeddings_vec", "NodeId")]
    public async Task Dropping_a_mismatched_vector_sweeps_its_mirror_and_preexisting_orphans(
        string table,
        string mirror,
        string key)
    {
        await using SqliteConnection connection = await OpenEmbeddingsAsync(table: table, mirrorTable: mirror, key: key);

        long removed = await BackupRestoreDatabaseWorker.DropMismatchedEmbeddingsAsync(
            connection,
            1536,
            CancellationToken.None);

        // Only the mismatched base vector counts as work to rebuild: its mirror and an older orphan
        // are projections, not two additional embeddings.
        Assert.Equal(1, removed);

        Assert.Equal(["A1A1A1A1-B2B2-4C3C-8D4D-5E5E6F6F7071"], await IdentitiesAsync(connection, table, key));

        Assert.Equal(["a1a1a1a1b2b24c3c8d4d5e5e6f6f7071"], await IdentitiesAsync(connection, mirror, key));

        Assert.Equal(0, await BackupRestoreDatabaseWorker.DropMismatchedEmbeddingsAsync(connection, 1536, CancellationToken.None));

        Assert.Equal(["A1A1A1A1-B2B2-4C3C-8D4D-5E5E6F6F7071"], await IdentitiesAsync(connection, table, key));

        Assert.Equal(["a1a1a1a1b2b24c3c8d4d5e5e6f6f7071"], await IdentitiesAsync(connection, mirror, key));
    }

    [Theory]
    [InlineData("entry_embeddings", "entry_embeddings_vec", "EntryId")]
    [InlineData("session_attachment_embeddings", "session_attachment_embeddings_vec", "ChunkId")]
    [InlineData("workspace_file_embeddings", "workspace_file_embeddings_vec", "ChunkId")]
    [InlineData("saga_memory_embeddings", "saga_memory_embeddings_vec", "MemoryId")]
    [InlineData("tapestry_node_embeddings", "tapestry_node_embeddings_vec", "NodeId")]
    public async Task A_missing_mirror_does_not_prevent_dropping_mismatched_base_vectors(
        string table,
        string mirror,
        string key)
    {
        await using SqliteConnection connection = await OpenEmbeddingsAsync(table: table, mirrorTable: mirror, key: key);

        await using (SqliteCommand drop = connection.CreateCommand())
        {
            drop.CommandText = $"DROP TABLE \"{mirror}\";";

            _ = await drop.ExecuteNonQueryAsync();
        }

        Assert.Equal(1, await BackupRestoreDatabaseWorker.DropMismatchedEmbeddingsAsync(connection, 1536, CancellationToken.None));

        Assert.Equal(["A1A1A1A1-B2B2-4C3C-8D4D-5E5E6F6F7071"], await IdentitiesAsync(connection, table, key));

        Assert.Equal(0, await BackupRestoreDatabaseWorker.DropMismatchedEmbeddingsAsync(connection, 1536, CancellationToken.None));
    }

    [Theory]
    [InlineData("entry_embeddings", "entry_embeddings_vec", "EntryId")]
    [InlineData("session_attachment_embeddings", "session_attachment_embeddings_vec", "ChunkId")]
    [InlineData("workspace_file_embeddings", "workspace_file_embeddings_vec", "ChunkId")]
    [InlineData("saga_memory_embeddings", "saga_memory_embeddings_vec", "MemoryId")]
    [InlineData("tapestry_node_embeddings", "tapestry_node_embeddings_vec", "NodeId")]
    public async Task An_unavailable_vec0_mirror_does_not_prevent_base_cleanup_or_other_mirror_sweeps(
        string table,
        string mirror,
        string key)
    {
        await using SqliteConnection connection = await OpenEmbeddingsAsync(table: table, mirrorTable: mirror, key: key);

        string reachableTable = table == "entry_embeddings" ? "session_attachment_embeddings" : "entry_embeddings";

        string reachableMirror = reachableTable + "_vec";

        string reachableKey = table == "entry_embeddings" ? "ChunkId" : "EntryId";

        await SeedEmbeddingsAsync(connection, reachableTable, reachableMirror, reachableKey);

        await InstallUnavailableVectorMirrorAsync(connection, mirror, key);

        await AssertUnavailableVectorMirrorAsync(connection, mirror);

        Assert.Equal(2, await BackupRestoreDatabaseWorker.DropMismatchedEmbeddingsAsync(connection, 1536, CancellationToken.None));

        Assert.Equal(["A1A1A1A1-B2B2-4C3C-8D4D-5E5E6F6F7071"], await IdentitiesAsync(connection, table, key));

        Assert.Equal(["A1A1A1A1-B2B2-4C3C-8D4D-5E5E6F6F7071"], await IdentitiesAsync(connection, reachableTable, reachableKey));

        Assert.Equal(["a1a1a1a1b2b24c3c8d4d5e5e6f6f7071"], await IdentitiesAsync(connection, reachableMirror, reachableKey));

        Assert.Equal(0, await BackupRestoreDatabaseWorker.DropMismatchedEmbeddingsAsync(connection, 1536, CancellationToken.None));

        await AssertUnavailableVectorMirrorAsync(connection, mirror);
    }

    /// <summary>
    /// Models an imported snapshot's real unavailable-module schema without adding a vec0 extension.
    /// This catalog surgery is confined to a disposable in-memory test database.
    /// </summary>
    private static async Task InstallUnavailableVectorMirrorAsync(SqliteConnection connection, string mirror, string key)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = $"""
            DROP TABLE "{mirror}";

            PRAGMA writable_schema = ON;

            INSERT INTO sqlite_schema (type, name, tbl_name, rootpage, sql)
            VALUES ('table', $mirror, $mirror, 0, $definition);

            PRAGMA writable_schema = RESET;
            """;

        _ = command.Parameters.AddWithValue("$mirror", mirror);

        _ = command.Parameters.AddWithValue("$definition", $"CREATE VIRTUAL TABLE \"{mirror}\" USING vec0(\"{key}\" TEXT PRIMARY KEY, Embedding FLOAT[768])");

        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertUnavailableVectorMirrorAsync(SqliteConnection connection, string mirror)
    {
        await using SqliteCommand read = connection.CreateCommand();

        read.CommandText = $"SELECT * FROM \"{mirror}\";";

        SqliteException error = await Assert.ThrowsAsync<SqliteException>(async () =>
        {
            await using SqliteDataReader reader = await read.ExecuteReaderAsync();
        });

        Assert.Equal(1, error.SqliteErrorCode);

        Assert.Contains("no such module: vec0", error.Message, StringComparison.Ordinal);
    }

    private const string KeptIdentity = "a1a1a1a1-b2b2-4c3c-8d4d-5e5e6f6f7071";

    private const string DroppedIdentity = "b2b2b2b2-c3c3-4d4d-8e5e-6f6f70708182";

    private const string OrphanedIdentity = "c3c3c3c3-d4d4-4e5e-8f6f-707081819293";

    /// <summary>
    /// A base embedding table and its mirror, at the two widths and the three states that matter.
    /// </summary>
    private static async Task<SqliteConnection> OpenEmbeddingsAsync(
        bool legacyVirtualMirror = false,
        string table = "entry_embeddings",
        string mirrorTable = "entry_embeddings_vec",
        string key = "EntryId")
    {

        SqliteConnection connection = new("Data Source=:memory:");

        await connection.OpenAsync();

        await SeedEmbeddingsAsync(connection, table, mirrorTable, key, legacyVirtualMirror);

        return connection;

    }

    private static async Task SeedEmbeddingsAsync(
        SqliteConnection connection,
        string table,
        string mirrorTable,
        string key,
        bool legacyVirtualMirror = false)
    {
        await using SqliteCommand create = connection.CreateCommand();

        string mirror = legacyVirtualMirror
            ? $"""CREATE VIRTUAL TABLE "{mirrorTable}" USING fts5("{key}", "Embedding");"""
            : $"""
              CREATE TABLE "{mirrorTable}" (
                  "{key}" TEXT NOT NULL PRIMARY KEY,
                  "Embedding" BLOB NOT NULL);
              """;

        create.CommandText = $"""
            CREATE TABLE "{table}" (
                "{key}" TEXT NOT NULL PRIMARY KEY,
                "Embedding" BLOB NOT NULL,
                "Dim" INTEGER NOT NULL);

            {mirror}

            INSERT INTO "{table}" VALUES ('{KeptIdentity.ToUpperInvariant()}', X'00', 1536);

            INSERT INTO "{table}" VALUES ('{DroppedIdentity.ToUpperInvariant()}', X'00', 768);

            INSERT INTO "{mirrorTable}"
            VALUES ('{KeptIdentity.Replace("-", string.Empty).ToLowerInvariant()}', X'00');

            INSERT INTO "{mirrorTable}"
            VALUES ('{DroppedIdentity.Replace("-", string.Empty).ToLowerInvariant()}', X'00');

            INSERT INTO "{mirrorTable}"
            VALUES ('{OrphanedIdentity.Replace("-", string.Empty).ToLowerInvariant()}', X'00');
            """;

        _ = await create.ExecuteNonQueryAsync();

    }

    private static async Task<string[]> IdentitiesAsync(SqliteConnection connection, string table, string key = "EntryId")
    {

        await using SqliteCommand read = connection.CreateCommand();

        read.CommandText = $"SELECT \"{key}\" FROM \"{table}\" ORDER BY \"{key}\";";

        List<string> identities = [];

        await using SqliteDataReader reader = await read.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {

            identities.Add(reader.GetString(0));

        }

        return [.. identities];

    }

    /// <summary>
    /// Every table read one statement's query plan performs.
    /// </summary>
    /// <remarks>
    /// Counted from the plan's own <c>SCAN</c> / <c>SEARCH</c> steps rather than by looking for table
    /// names in it: SQLite reports the alias where a statement gives one, so a correlated sweep over
    /// <c>FROM "entry_embeddings" base</c> plans as <c>SCAN base</c> and a name-matching test reads it
    /// as touching nothing at all. One step per table read is the shape that matters, whatever the
    /// tables are called.
    ///
    /// <para>Unbound parameters are given a value first: the provider refuses to prepare a statement
    /// whose parameters have none, and the plan does not depend on what they hold.</para>
    /// </remarks>
    private static IReadOnlyList<string> PlannedTableReads(
        SqliteConnection connection,
        string statement)
    {

        using SqliteCommand explain = connection.CreateCommand();

        explain.CommandText = "EXPLAIN QUERY PLAN " + statement;

        foreach (Match parameter in Regex.Matches(statement, @"\$\w+"))
        {

            _ = explain.Parameters.AddWithValue(parameter.Value, 1);

        }

        List<string> reads = [];

        using SqliteDataReader reader = explain.ExecuteReader();

        while (reader.Read())
        {

            string detail = reader.GetString(reader.GetOrdinal("detail"));

            if (detail.StartsWith("SCAN ", StringComparison.Ordinal)
                || detail.StartsWith("SEARCH ", StringComparison.Ordinal))
            {

                reads.Add(detail);

            }

        }

        return reads;

    }

    private static BackupPathRemapper Remapper(params BackupPathMapping[] mappings)
    {

        BackupPathRemapValidation validation = BackupPathRemapper.Create(mappings);

        Assert.Empty(validation.Issues);

        return Assert.IsType<BackupPathRemapper>(validation.Remapper);

    }

    private static async Task<SqliteConnection> OpenCampaignsAsync(string sanctumConfigJson)
    {

        SqliteConnection connection = new("Data Source=:memory:");

        await connection.OpenAsync();

        await using (SqliteCommand create = connection.CreateCommand())
        {

            create.CommandText = """
                CREATE TABLE "Campaigns" (
                    "Id" TEXT NOT NULL PRIMARY KEY,
                    "Path" TEXT NULL,
                    "SanctumConfigJson" TEXT NULL);
                """;

            _ = await create.ExecuteNonQueryAsync();

        }

        await using (SqliteCommand insert = connection.CreateCommand())
        {

            insert.CommandText = """
                INSERT INTO "Campaigns" ("Id", "Path", "SanctumConfigJson") VALUES ('c1', NULL, $json);
                """;

            _ = insert.Parameters.AddWithValue("$json", sanctumConfigJson);

            _ = await insert.ExecuteNonQueryAsync();

        }

        return connection;

    }

    private static async Task<JsonArray> ReadAllowedPathsAsync(SqliteConnection connection)
    {

        await using SqliteCommand read = connection.CreateCommand();

        read.CommandText = """
            SELECT "SanctumConfigJson" FROM "Campaigns" WHERE "Id" = 'c1';
            """;

        string json = Assert.IsType<string>(await read.ExecuteScalarAsync());

        JsonNode node = Assert.IsType<JsonObject>(JsonNode.Parse(json));

        return Assert.IsType<JsonArray>(node["allowedPaths"]);

    }

}
