using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
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

        // The Campaign holds its own head for the same key, so its cleanup removes a head and the
        // key's dependency epoch moves. A pin bound to that epoch would still be a row and no longer
        // be a pin.
        await harness.SetAsync(CovenantScope.Campaign, CampaignOne, Key, "Build from tools.", Token);

        await ExecuteAsync(harness, $"DELETE FROM \"Campaigns\" WHERE \"Id\" = '{CampaignOne:D}';");

        CovenantCleanupOutcome cleaned = await harness.RunCleanupAsync(Token);

        Assert.Equal(1L, cleaned.HeadsRemoved);

        Assert.Equal(
            1,
            await ScalarAsync(
                harness,
                "SELECT COUNT(*) FROM covenant_curation_heads WHERE CampaignId IS NULL AND IsPinned = 1;"));

        await using ICovenantSnapshotReadLease read =
            (await harness.Gate.AcquireReadAsync(CovenantOperationScope.Global, Token)).Value;

        Result<CovenantLaneHeadProbe> probe = await harness.Fixture.Store.ProbeLaneHeadAsync(
            CanonicalCampaignContext.GlobalOnly,
            CovenantLane.Confirmed,
            Key,
            read,
            Token);

        Assert.True(probe.IsSuccess, probe.IsFailure ? probe.Error.Message : string.Empty);

        Assert.True(probe.Value.IsPinned);

    }

    /// <summary>
    /// Covenant detail reports each lane's own curation at the key's binding epoch, in the scope it
    /// was asked about.
    /// </summary>
    /// <remarks>
    /// A Campaign mask over a Global key is the case where the Campaign holds no entry and no head for
    /// the key, so the curation has to be reported without one. The lanes are read separately: a
    /// detail that handed one lane's row to both would report the operator's Proposed pin as a
    /// Confirmed one, or a Confirmed mask on a lane nobody masked.
    /// </remarks>
    [Fact]
    public async Task Detail_reports_each_lanes_curation_at_the_binding_epoch()
    {

        const string PinnedKey = "detail.pinned";

        const string MaskedKey = "detail.masked";

        await using CovenantServiceHarness harness = await CovenantServiceHarness.StartAsync(Token);

        await harness.SetAsync(CovenantScope.Global, null, PinnedKey, "Build from the root.", Token);

        Assert.True((await harness.CurateAsync(CovenantCurationKind.Pin, CovenantScope.Global, null, PinnedKey, Token)).IsSuccess);

        CovenantDetail pinned = await ReadDetailAsync(harness, CovenantScope.Global, null, PinnedKey);

        Assert.NotNull(pinned.ConfirmedHead);

        Assert.Equal(new CovenantCurationStateDto(true, false, 1), pinned.ConfirmedCuration);

        Assert.Equal(CovenantCurationStateDto.None, pinned.ProposedCuration);

        await harness.SetAsync(CovenantScope.Global, null, MaskedKey, "Build from tools.", Token);

        await harness.AddCampaignAsync(CampaignOne, Token);

        Assert.True((await harness.CurateAsync(CovenantCurationKind.Mask, CovenantScope.Campaign, CampaignOne, MaskedKey, Token)).IsSuccess);

        CovenantDetail masked = await ReadDetailAsync(harness, CovenantScope.Campaign, CampaignOne, MaskedKey);

        Assert.Null(masked.EntryId);

        Assert.Null(masked.ConfirmedHead);

        Assert.Equal(new CovenantCurationStateDto(false, true, 1), masked.ConfirmedCuration);

        Assert.Equal(CovenantCurationStateDto.None, masked.ProposedCuration);

        // The mask belongs to the Campaign, so the Global key it suppresses there reports none.
        CovenantDetail global = await ReadDetailAsync(harness, CovenantScope.Global, null, MaskedKey);

        Assert.Equal(CovenantCurationStateDto.None, global.ConfirmedCuration);

        Assert.Equal(CovenantCurationStateDto.None, global.ProposedCuration);

        // A second lane curated in the same scope is reported on its own lane, beside the first.
        Assert.True(
            (await harness.CurateAsync(
                CovenantCurationKind.Pin,
                CovenantScope.Campaign,
                CampaignOne,
                MaskedKey,
                Token,
                lane: CovenantLane.Proposed)).IsSuccess);

        CovenantDetail both = await ReadDetailAsync(harness, CovenantScope.Campaign, CampaignOne, MaskedKey);

        Assert.Equal(new CovenantCurationStateDto(false, true, 1), both.ConfirmedCuration);

        Assert.Equal(new CovenantCurationStateDto(true, false, 1), both.ProposedCuration);

    }

    /// <summary>
    /// A pin recorded before the key had any head is still reported once the first operator set
    /// creates the key.
    /// </summary>
    /// <remarks>
    /// The pin is recorded under the binding epoch a missing key row reads as, and the first head
    /// creates the row at that binding epoch while its dependency epoch moves. A detail that joined
    /// the dependency epoch would report the pin as gone the moment there was something to pin.
    /// </remarks>
    [Fact]
    public async Task Detail_reports_a_keyless_pin_after_the_first_operator_set()
    {

        const string KeylessKey = "detail.keyless";

        await using CovenantServiceHarness harness = await CovenantServiceHarness.StartAsync(Token);

        Assert.True((await harness.CurateAsync(CovenantCurationKind.Pin, CovenantScope.Global, null, KeylessKey, Token)).IsSuccess);

        await harness.SetAsync(CovenantScope.Global, null, KeylessKey, "Build from the root.", Token);

        CovenantDetail detail = await ReadDetailAsync(harness, CovenantScope.Global, null, KeylessKey);

        Assert.NotNull(detail.ConfirmedHead);

        Assert.Equal(new CovenantCurationStateDto(true, false, 1), detail.ConfirmedCuration);

        Assert.Equal(CovenantCurationStateDto.None, detail.ProposedCuration);

    }

    /// <summary>
    /// On a key that existed before the binding epoch did, detail reads the key row's own nonzero
    /// binding epoch, and the pin is still reported after a later write moves the dependency epoch.
    /// </summary>
    /// <remarks>
    /// Every key created from canonical version 6 on binds epoch 0, so the two tests above cannot tell
    /// a detail that reads the key row from one that assumes zero. On an upgraded installation nonzero
    /// is the ordinary case.
    /// </remarks>
    [Fact]
    public async Task Detail_reports_curation_of_a_key_with_a_nonzero_binding_epoch()
    {

        const string UpgradedKey = "detail.upgraded";

        await using CovenantServiceHarness harness = await CovenantServiceHarness.StartAsync(Token);

        await harness.SeedUpgradedKeyAsync(UpgradedKey, epoch: 3, Token);

        await harness.SetAsync(CovenantScope.Global, null, UpgradedKey, "Build from the root.", Token);

        Assert.True((await harness.CurateAsync(CovenantCurationKind.Pin, CovenantScope.Global, null, UpgradedKey, Token)).IsSuccess);

        await harness.CorrectAsync(CovenantScope.Global, null, UpgradedKey, "Build from tools.", Token);

        CovenantDetail detail = await ReadDetailAsync(harness, CovenantScope.Global, null, UpgradedKey);

        // The correction moved the dependency epoch past the binding epoch the pin was recorded under.
        Assert.Equal(5, detail.KeyEpoch);

        Assert.Equal(new CovenantCurationStateDto(true, false, 1), detail.ConfirmedCuration);

        Assert.Equal(CovenantCurationStateDto.None, detail.ProposedCuration);

    }

    /// <summary>
    /// The three tables are protected Covenant content, so every surface that counts protected state
    /// counts them. The inspector's list is the canonical content list itself, which the retention
    /// inventory also reads directly.
    /// </summary>
    [Fact]
    public void The_protected_state_inventory_names_the_curation_tables()
    {

        Assert.Contains("covenant_curation_versions", BackupRestoreProtectedStateInspector.CanonicalContentTables);

        Assert.Contains("covenant_curation_heads", BackupRestoreProtectedStateInspector.CanonicalContentTables);

        Assert.Contains("covenant_curation_receipts", BackupRestoreProtectedStateInspector.CanonicalContentTables);

    }

    /// <summary>Reads one scoped key's detail through the production store, under the lease a route takes.</summary>
    private static async Task<CovenantDetail> ReadDetailAsync(
        CovenantServiceHarness harness,
        CovenantScope scope,
        Guid? campaignId,
        string key)
    {

        await using ICovenantSnapshotReadLease read = await harness.AcquireReadAsync(scope, campaignId, Token);

        Result<CovenantDetail> detail = await harness.Fixture.Store.ReadDetailAsync(
            new CovenantDetailQuery(
                campaignId is { } campaign ? CovenantOperationScope.ForCampaign(campaign) : CovenantOperationScope.Global,
                key),
            read,
            Token);

        Assert.True(detail.IsSuccess, detail.IsFailure ? detail.Error.Message : string.Empty);

        return detail.Value;

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
