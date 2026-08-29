using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Tests.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// Curation state belongs to the same lifecycle as the entries it curates.
/// </summary>
/// <remarks>
/// A mask that outlived the Campaign it applied to would suppress a Global preference for a Campaign
/// identity that no longer exists, and nothing would ever remove it — the mask names a scoped key
/// rather than an entry, so no entry deletion reaches it. That is the failure this suite exists for.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class CovenantCurationLifecycleTests
{

    private static CancellationToken Token => CancellationToken.None;

    private const string Key = "preference.builds";

    private static readonly Guid CampaignOne = CovenantOperationGateFixture.CampaignOne;

    private static readonly Guid CampaignTwo = new("B0000000-0000-4000-8000-000000000002");

    [Fact]
    public async Task Campaign_cleanup_removes_the_curation_rows_that_Campaign_owned()
    {

        await using CovenantServiceHarness harness = await CovenantServiceHarness.StartAsync(Token, withOwnerCleanup: true);

        await harness.AddCampaignAsync(CampaignOne, Token);

        await harness.AddCampaignAsync(CampaignTwo, Token);

        await harness.SetAsync(CovenantScope.Global, null, Key, "Build from the root.", Token);

        _ = await harness.CurateAsync(CovenantCurationKind.Mask, CovenantScope.Campaign, CampaignOne, Key, Token);

        _ = await harness.CurateAsync(CovenantCurationKind.Mask, CovenantScope.Campaign, CampaignTwo, Key, Token);

        Assert.Equal(2, await ScalarAsync(harness, "SELECT COUNT(*) FROM covenant_curation_heads;"));

        await ExecuteAsync(harness, $"DELETE FROM \"Campaigns\" WHERE \"Id\" = '{CampaignOne:D}';");

        await harness.RunCleanupAsync(Token);

        Assert.Equal(
            0,
            await ScalarAsync(
                harness,
                $"SELECT COUNT(*) FROM covenant_curation_heads WHERE CampaignId = '{CampaignOne:D}';"));

        Assert.Equal(
            0,
            await ScalarAsync(
                harness,
                $"SELECT COUNT(*) FROM covenant_curation_versions WHERE CampaignId = '{CampaignOne:D}';"));

        Assert.Equal(
            0,
            await ScalarAsync(
                harness,
                $"SELECT COUNT(*) FROM covenant_curation_receipts WHERE CampaignId = '{CampaignOne:D}';"));

        // The other Campaign's mask is untouched, so cleanup removed exactly what it named.
        Assert.Equal(
            1,
            await ScalarAsync(
                harness,
                $"SELECT COUNT(*) FROM covenant_curation_heads WHERE CampaignId = '{CampaignTwo:D}';"));

    }

    /// <summary>
    /// A Global curation row survives a Campaign cleanup, because it belongs to no Campaign.
    /// </summary>
    [Fact]
    public async Task A_Global_pin_survives_a_Campaign_cleanup()
    {

        await using CovenantServiceHarness harness = await CovenantServiceHarness.StartAsync(Token, withOwnerCleanup: true);

        await harness.AddCampaignAsync(CampaignOne, Token);

        await harness.SetAsync(CovenantScope.Global, null, Key, "Build from the root.", Token);

        _ = await harness.CurateAsync(CovenantCurationKind.Pin, CovenantScope.Global, null, Key, Token);

        await ExecuteAsync(harness, $"DELETE FROM \"Campaigns\" WHERE \"Id\" = '{CampaignOne:D}';");

        await harness.RunCleanupAsync(Token);

        Assert.Equal(
            1,
            await ScalarAsync(
                harness,
                "SELECT COUNT(*) FROM covenant_curation_heads WHERE CampaignId IS NULL AND IsPinned = 1;"));

    }

    /// <summary>
    /// The three tables are protected Covenant content, so every surface that counts protected state
    /// counts them. The retention inventory derives its list from this one rather than restating it.
    /// </summary>
    [Fact]
    public void The_protected_state_inventory_names_the_curation_tables()
    {

        Assert.Contains("covenant_curation_versions", BackupRestoreProtectedStateInspector.CanonicalContentTables);

        Assert.Contains("covenant_curation_heads", BackupRestoreProtectedStateInspector.CanonicalContentTables);

        Assert.Contains("covenant_curation_receipts", BackupRestoreProtectedStateInspector.CanonicalContentTables);

    }

    private static async Task<long> ScalarAsync(CovenantServiceHarness harness, string sql)
    {

        await using SqliteCommand command = harness.Fixture.Connection.CreateCommand();

        command.CommandText = sql;

        return Convert.ToInt64(
            await command.ExecuteScalarAsync(Token),
            System.Globalization.CultureInfo.InvariantCulture);

    }

    private static async Task ExecuteAsync(CovenantServiceHarness harness, string sql)
    {

        await using SqliteCommand command = harness.Fixture.Connection.CreateCommand();

        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync(Token);

    }

}
