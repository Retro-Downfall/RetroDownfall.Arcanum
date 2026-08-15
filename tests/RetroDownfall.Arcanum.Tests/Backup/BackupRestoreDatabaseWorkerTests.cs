using System.Text.Json.Nodes;

using Microsoft.Data.Sqlite;

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
