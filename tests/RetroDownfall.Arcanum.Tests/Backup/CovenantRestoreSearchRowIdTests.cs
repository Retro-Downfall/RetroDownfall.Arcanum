using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Backup;

/// <summary>
/// A restore that keeps Covenant entries keeps their heads and the search row IDs they hold, so the next
/// key it creates has to be given a row ID none of them holds.
/// </summary>
/// <remarks>
/// <c>covenant_heads.SearchRowId</c> is unique, and the allocator hands out <c>NextSearchRowId</c>. A
/// restore that restarted that counter at 1 beside restored heads holding row IDs from 1 upward made
/// every new key collide on the unique index, roll back, and fail the same way again.
/// </remarks>
[Collection("ApiHost")]
public sealed class CovenantRestoreSearchRowIdTests
{
    private static CancellationToken Token => CancellationToken.None;

    /// <summary>
    /// The restore sets the counter past every row ID it kept, and two new keys created through the
    /// operator route afterwards each get a row ID of their own.
    /// </summary>
    [SkippableFact]
    public async Task A_restore_that_keeps_covenant_entries_lets_new_keys_take_row_ids_past_every_restored_head()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        await CovenantAvailabilityRepublicationTests.RestoreUnprojectedArchiveAsync(harness);

        await using (SqliteConnection live = await harness.OpenLiveDatabaseAsync(GrimoireFixture.TestGrimoireSecret))
        {
            long highest = await ScalarAsync(live, "SELECT MAX(SearchRowId) FROM covenant_heads;");

            long next = await ScalarAsync(live, "SELECT NextSearchRowId FROM covenant_state WHERE StateKey = 1;");

            Assert.True(next > highest, $"The restore left the search row counter at {next}, at or below a restored head's {highest}.");
        }

        SqliteConnection.ClearAllPools();

        await CreateTwoNewKeysAsync(harness);
    }

    /// <summary>
    /// An archive taken from an installation an earlier build restored carries that restore's counter of 1
    /// itself. Restoring it again still carries the counter past every head it keeps, so the archived
    /// counter cannot be trusted on its own.
    /// </summary>
    [SkippableFact]
    public async Task A_restore_of_an_archive_whose_own_counter_was_restarted_still_carries_it_past_every_head()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        await CovenantAvailabilityRepublicationTests.RestoreUnprojectedArchiveAsync(
            harness,
            () => RestartCounterAsEarlierRestoreDidAsync(harness));

        await using (SqliteConnection live = await harness.OpenLiveDatabaseAsync(GrimoireFixture.TestGrimoireSecret))
        {
            long highest = await ScalarAsync(live, "SELECT MAX(SearchRowId) FROM covenant_heads;");

            long next = await ScalarAsync(live, "SELECT NextSearchRowId FROM covenant_state WHERE StateKey = 1;");

            Assert.True(next > highest, $"The restore left the search row counter at {next}, at or below a restored head's {highest}.");
        }

        SqliteConnection.ClearAllPools();

        await CreateTwoNewKeysAsync(harness);
    }

    /// <summary>
    /// An installation restored by an earlier build already holds a counter at or below its restored
    /// heads. The first new key heals it in the allocating statement, the healed counter persists, and a
    /// second new key follows it.
    /// </summary>
    [SkippableFact]
    public async Task An_installation_an_earlier_restore_left_with_a_low_counter_heals_on_its_next_new_key()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        await CovenantAvailabilityRepublicationTests.RestoreUnprojectedArchiveAsync(harness);

        await RestartCounterAsEarlierRestoreDidAsync(harness);

        SqliteConnection.ClearAllPools();

        await CreateTwoNewKeysAsync(harness);
    }

    /// <summary>
    /// A restore never moves the search row counter backwards. An archive whose own counter is well ahead of
    /// its heads, as one is after the source erased a later entry, keeps that counter, because the counter
    /// reserved every row ID the archive's projection rows were given.
    /// </summary>
    [SkippableFact]
    public async Task A_restore_never_moves_the_search_row_counter_below_the_archived_counter()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        const long Archived = 20;

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        await CovenantAvailabilityRepublicationTests.RestoreUnprojectedArchiveAsync(
            harness,
            () => AdvanceArchivedCounterAsync(harness, Archived));

        await using (SqliteConnection live = await harness.OpenLiveDatabaseAsync(GrimoireFixture.TestGrimoireSecret))
        {
            long highest = await ScalarAsync(live, "SELECT MAX(SearchRowId) FROM covenant_heads;");

            long next = await ScalarAsync(live, "SELECT NextSearchRowId FROM covenant_state WHERE StateKey = 1;");

            Assert.True(next >= Archived, $"The restore moved the search row counter back to {next}, below the archived {Archived}.");

            Assert.True(next > highest, $"The restore left the search row counter at {next}, at or below a restored head's {highest}.");
        }

        SqliteConnection.ClearAllPools();
    }

    /// <summary>
    /// An installation an earlier build restored holds a counter at or below its restored heads, so an entry
    /// erased there can leave a pending absent delta whose row ID is above every head. The next new key must
    /// not take that row ID: the delta still names it, and the row ID is never reused within a dataset.
    /// </summary>
    [SkippableFact]
    public async Task A_new_key_never_takes_a_row_id_a_pending_absent_delta_still_holds()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        await CovenantAvailabilityRepublicationTests.RestoreUnprojectedArchiveAsync(harness);

        await RestartCounterAsEarlierRestoreDidAsync(harness);

        SqliteConnection.ClearAllPools();

        await using ArcanumWebApplicationFactory host = CovenantAvailabilityRepublicationTests.Host(harness.Credentials, harness.Profile);

        using HttpClient client = host.CreateAuthenticatedClient();

        MemoryErasureRouteDriver driver = new(client);

        long erased = await HostScalarAsync(
            host,
            $"SELECT SearchRowId FROM covenant_heads WHERE NormalizedKey = '{CovenantAvailabilityRepublicationTests.HarborKey}';");

        Assert.Equal(await HostScalarAsync(host, "SELECT MAX(SearchRowId) FROM covenant_heads;"), erased);

        _ = await driver.EraseCovenantAsync(CovenantScope.Global, null, CovenantAvailabilityRepublicationTests.HarborKey);

        // The erased head is gone, and its absent delta now names a row ID above every head's.
        Assert.Equal(
            1L,
            await HostScalarAsync(
                host,
                $"SELECT COUNT(*) FROM covenant_search_outbox WHERE SearchRowId = {erased} AND DesiredVersionId IS NULL;"));

        Assert.True(await HostScalarAsync(host, "SELECT MAX(SearchRowId) FROM covenant_heads;") < erased);

        _ = await driver.SetCovenantAsync(CovenantScope.Global, null, "restore.first-new-key", "A key authored after the restore.");

        long created = await HostScalarAsync(host, "SELECT SearchRowId FROM covenant_heads WHERE NormalizedKey = 'restore.first-new-key';");

        Assert.True(created > erased, $"The new key took row ID {created}, which a pending absent delta still holds (at {erased}).");

        Assert.Equal(
            0L,
            await HostScalarAsync(
                host,
                """
                SELECT COUNT(*)
                FROM covenant_search_outbox o
                JOIN covenant_heads h ON h.SearchRowId = o.SearchRowId
                WHERE o.DesiredVersionId IS NULL;
                """));
    }

    /// <summary>
    /// Leaves the stopped source installation with its search row counter ahead of its heads, which is the
    /// state it reaches after erasing a later entry. The counter only moves forward within a generation.
    /// </summary>
    private static async Task AdvanceArchivedCounterAsync(MemoryErasureRestoreHarness harness, long counter)
    {
        await using SqliteConnection live = await BackupRestoreDatabaseWorker.OpenAsync(
            harness.DatabasePath,
            GrimoireFixture.TestGrimoireSecret,
            readOnly: false,
            Token);

        await using (SqliteCommand advance = live.CreateCommand())
        {
            advance.CommandText = "UPDATE covenant_state SET NextSearchRowId = $counter WHERE StateKey = 1;";

            _ = advance.Parameters.AddWithValue("$counter", counter);

            Assert.Equal(1, await advance.ExecuteNonQueryAsync(Token));
        }

        Assert.True(
            await ScalarAsync(live, "SELECT NextSearchRowId FROM covenant_state WHERE StateKey = 1;")
            > await ScalarAsync(live, "SELECT MAX(SearchRowId) FROM covenant_heads;") + 1);
    }

    /// <summary>
    /// Leaves the stopped installation as an earlier build's restore did: a new dataset generation and the
    /// search row counter restarted at 1, beside heads still holding row IDs from 1 upward. The generation
    /// moves with the counter because the counter may restart only when the generation does.
    /// </summary>
    private static async Task RestartCounterAsEarlierRestoreDidAsync(MemoryErasureRestoreHarness harness)
    {
        await using SqliteConnection live = await BackupRestoreDatabaseWorker.OpenAsync(
            harness.DatabasePath,
            GrimoireFixture.TestGrimoireSecret,
            readOnly: false,
            Token);

        await using (SqliteCommand restart = live.CreateCommand())
        {
            restart.CommandText = "UPDATE covenant_state SET DatasetGeneration = randomblob(16), NextSearchRowId = 1 WHERE StateKey = 1;";

            Assert.Equal(1, await restart.ExecuteNonQueryAsync(Token));
        }

        Assert.True(
            await ScalarAsync(live, "SELECT NextSearchRowId FROM covenant_state WHERE StateKey = 1;")
            <= await ScalarAsync(live, "SELECT MAX(SearchRowId) FROM covenant_heads;"));
    }

    /// <summary>
    /// Starts the restored installation, creates two new keys through the operator set route, and requires
    /// every head to hold its own row ID with the persisted counter past all of them.
    /// </summary>
    private static async Task CreateTwoNewKeysAsync(MemoryErasureRestoreHarness harness)
    {
        await using ArcanumWebApplicationFactory host = CovenantAvailabilityRepublicationTests.Host(harness.Credentials, harness.Profile);

        using HttpClient client = host.CreateAuthenticatedClient();

        MemoryErasureRouteDriver driver = new(client);

        long restored = await HostScalarAsync(host, "SELECT COUNT(*) FROM covenant_heads;");

        _ = await driver.SetCovenantAsync(CovenantScope.Global, null, "restore.first-new-key", "A key authored after the restore.");

        Assert.True(
            await HostScalarAsync(host, "SELECT NextSearchRowId FROM covenant_state WHERE StateKey = 1;")
            > await HostScalarAsync(host, "SELECT MAX(SearchRowId) FROM covenant_heads;"),
            "The counter was not carried past every head the first new key left.");

        _ = await driver.SetCovenantAsync(CovenantScope.Global, null, "restore.second-new-key", "A second key authored after the restore.");

        Assert.Equal(restored + 2, await HostScalarAsync(host, "SELECT COUNT(*) FROM covenant_heads;"));

        Assert.Equal(restored + 2, await HostScalarAsync(host, "SELECT COUNT(DISTINCT SearchRowId) FROM covenant_heads;"));

        Assert.True(
            await HostScalarAsync(host, "SELECT NextSearchRowId FROM covenant_state WHERE StateKey = 1;")
            > await HostScalarAsync(host, "SELECT MAX(SearchRowId) FROM covenant_heads;"));
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        return Convert.ToInt64(await command.ExecuteScalarAsync(Token), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<long> HostScalarAsync(ArcanumWebApplicationFactory host, string sql)
    {
        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();

        SqliteConnection connection = await scope.ServiceProvider
            .GetRequiredService<ICovenantConnectionSource>()
            .GetOpenConnectionAsync(Token);

        return await ScalarAsync(connection, sql);
    }
}
