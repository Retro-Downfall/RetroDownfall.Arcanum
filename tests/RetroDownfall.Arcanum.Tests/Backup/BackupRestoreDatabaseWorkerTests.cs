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

    [Theory]
    [InlineData("entry_embeddings", "EntryId")]
    [InlineData("session_attachment_embeddings", "ChunkId")]
    [InlineData("workspace_file_embeddings", "ChunkId")]
    [InlineData("saga_memory_embeddings", "MemoryId")]
    [InlineData("tapestry_node_embeddings", "NodeId")]
    public async Task Dropping_mismatched_durable_vectors_preserves_matching_rows_and_is_idempotent(
        string table,
        string key)
    {
        await using SqliteConnection connection = new("Data Source=:memory:");

        await connection.OpenAsync();

        await using (SqliteCommand seed = connection.CreateCommand())
        {
            seed.CommandText = $"""
                CREATE TABLE "{table}" ("{key}" TEXT PRIMARY KEY, Embedding BLOB NOT NULL, Dim INTEGER NOT NULL);
                INSERT INTO "{table}" VALUES ('kept', X'0000803F', 1536), ('removed', X'0000003F', 768);
                """;

            await seed.ExecuteNonQueryAsync();
        }

        Assert.Equal(1, await BackupRestoreDatabaseWorker.DropMismatchedEmbeddingsAsync(
            connection, 1536, CancellationToken.None));

        Assert.Equal(["kept"], await IdentitiesAsync(connection, table, key));

        await using SqliteCommand read = connection.CreateCommand();

        read.CommandText = $"SELECT Embedding FROM \"{table}\";";

        Assert.Equal(new byte[] { 0, 0, 128, 63 }, Assert.IsType<byte[]>(await read.ExecuteScalarAsync()));

        Assert.Equal(0, await BackupRestoreDatabaseWorker.DropMismatchedEmbeddingsAsync(
            connection, 1536, CancellationToken.None));
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
