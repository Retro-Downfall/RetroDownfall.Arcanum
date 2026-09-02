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

    private const string KeptIdentity = "a1a1a1a1-b2b2-4c3c-8d4d-5e5e6f6f7071";

    private const string DroppedIdentity = "b2b2b2b2-c3c3-4d4d-8e5e-6f6f70708182";

    private const string OrphanedIdentity = "c3c3c3c3-d4d4-4e5e-8f6f-707081819293";

    /// <summary>
    /// A base embedding table and its mirror, at the two widths and the three states that matter.
    /// </summary>
    private static async Task<SqliteConnection> OpenEmbeddingsAsync()
    {

        SqliteConnection connection = new("Data Source=:memory:");

        await connection.OpenAsync();

        await using SqliteCommand create = connection.CreateCommand();

        create.CommandText = $"""
            CREATE TABLE "entry_embeddings" (
                "EntryId" TEXT NOT NULL PRIMARY KEY,
                "Embedding" BLOB NOT NULL,
                "Dim" INTEGER NOT NULL);

            CREATE TABLE "entry_embeddings_vec" (
                "EntryId" TEXT NOT NULL PRIMARY KEY,
                "Embedding" BLOB NOT NULL);

            INSERT INTO "entry_embeddings" VALUES ('{KeptIdentity.ToUpperInvariant()}', X'00', 1536);

            INSERT INTO "entry_embeddings" VALUES ('{DroppedIdentity.ToUpperInvariant()}', X'00', 768);

            INSERT INTO "entry_embeddings_vec"
            VALUES ('{KeptIdentity.Replace("-", string.Empty).ToLowerInvariant()}', X'00');

            INSERT INTO "entry_embeddings_vec"
            VALUES ('{DroppedIdentity.Replace("-", string.Empty).ToLowerInvariant()}', X'00');

            INSERT INTO "entry_embeddings_vec"
            VALUES ('{OrphanedIdentity.Replace("-", string.Empty).ToLowerInvariant()}', X'00');
            """;

        _ = await create.ExecuteNonQueryAsync();

        return connection;

    }

    private static async Task<string[]> IdentitiesAsync(SqliteConnection connection, string table)
    {

        await using SqliteCommand read = connection.CreateCommand();

        read.CommandText = $"SELECT \"EntryId\" FROM \"{table}\" ORDER BY \"EntryId\";";

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
