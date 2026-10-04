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
    /// A curation head recorded against an earlier binding epoch of the key is inert, and detail
    /// reports the lane as uncurated until the operator curates the key the installation has now.
    /// </summary>
    /// <remarks>
    /// An upgraded installation can hold such a head. Before canonical version 6, curation was
    /// recorded under the key's moving epoch, and the upgrade binds each key at the epoch it had then,
    /// so a head recorded under any earlier epoch stays where it is and applies to nothing. A detail
    /// that reported it would tell the operator the agent may not write a lane it may, and name a
    /// curation revision the next change is refused for, because the curation preflight reads the
    /// binding epoch and finds revision zero.
    /// </remarks>
    [Fact]
    public async Task Detail_reports_a_head_at_a_stale_binding_epoch_as_uncurated()
    {

        const string StaleKey = "detail.stale";

        await using CovenantServiceHarness harness = await CovenantServiceHarness.StartAsync(Token);

        await harness.AddCampaignAsync(CampaignOne, Token);

        await harness.SeedUpgradedKeyAsync(StaleKey, epoch: 5, Token);

        await harness.SetAsync(CovenantScope.Campaign, CampaignOne, StaleKey, "Build from the root.", Token);

        await ExecuteAsync(harness, StalePinSql(CampaignOne, StaleKey, staleEpoch: 2));

        Assert.Equal(
            1,
            await ScalarAsync(harness, $"SELECT COUNT(*) FROM covenant_curation_heads WHERE NormalizedKey = '{StaleKey}' AND KeyEpoch = 2 AND IsPinned = 1;"));

        CovenantDetail stale = await ReadDetailAsync(harness, CovenantScope.Campaign, CampaignOne, StaleKey);

        Assert.NotNull(stale.ConfirmedHead);

        Assert.Equal(CovenantCurationStateDto.None, stale.ConfirmedCuration);

        Assert.Equal(CovenantCurationStateDto.None, stale.ProposedCuration);

        // The revision detail reported is the one the production preflight expects.
        Assert.True((await harness.CurateAsync(CovenantCurationKind.Pin, CovenantScope.Campaign, CampaignOne, StaleKey, Token)).IsSuccess);

        CovenantDetail live = await ReadDetailAsync(harness, CovenantScope.Campaign, CampaignOne, StaleKey);

        Assert.Equal(new CovenantCurationStateDto(true, false, 1), live.ConfirmedCuration);

    }

    /// <summary>
    /// A lane curated back to nothing keeps its curation revision, which the next change must name.
    /// </summary>
    /// <remarks>
    /// Only a lane with no curation row reads as <see cref="CovenantCurationStateDto.None"/>. An unpin
    /// appends a version, so the lane reads unpinned and unmasked at revision 2, and a curation change
    /// that expected zero would be refused.
    /// </remarks>
    [Fact]
    public async Task Detail_reports_a_lane_curated_back_to_nothing_at_its_curation_revision()
    {

        const string UnpinnedKey = "detail.unpinned";

        await using CovenantServiceHarness harness = await CovenantServiceHarness.StartAsync(Token);

        await harness.SetAsync(CovenantScope.Global, null, UnpinnedKey, "Build from the root.", Token);

        Assert.True((await harness.CurateAsync(CovenantCurationKind.Pin, CovenantScope.Global, null, UnpinnedKey, Token)).IsSuccess);

        Assert.True(
            (await harness.CurateAsync(
                CovenantCurationKind.Unpin,
                CovenantScope.Global,
                null,
                UnpinnedKey,
                Token,
                expectedRevision: 1)).IsSuccess);

        CovenantDetail detail = await ReadDetailAsync(harness, CovenantScope.Global, null, UnpinnedKey);

        Assert.Equal(new CovenantCurationStateDto(false, false, 2), detail.ConfirmedCuration);

        Assert.NotEqual(CovenantCurationStateDto.None, detail.ConfirmedCuration);

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

    /// <summary>
    /// One Campaign Confirmed pin, its version and its head, recorded under an earlier epoch of the key:
    /// the inert curation an upgraded installation keeps.
    /// </summary>
    /// <remarks>
    /// Raw, because no version 6 writer records curation at any epoch but the key's binding epoch.
    /// </remarks>
    private static string StalePinSql(Guid campaignId, string key, long staleEpoch) =>
        $"""
        INSERT INTO covenant_curation_versions (
            CurationVersionId, ScopeCode, CampaignId, NormalizedKey, LaneCode, KeyEpoch, CurationKindCode,
            Revision, PredecessorVersionId, MutationId, RequestIdempotencyDigest, AuthorizationDigest,
            FinalMutationDigest, CreatedAtUtc)
        VALUES (
            'stale-pin', 2, '{campaignId:D}', '{key}', 1, {staleEpoch}, 1,
            1, NULL, 'stale-pin-mutation', randomblob(32), randomblob(32),
            randomblob(32), '2026-01-01T00:00:00.0000000Z');
        INSERT INTO covenant_curation_heads (
            ScopeCode, CampaignId, NormalizedKey, LaneCode, KeyEpoch, IsPinned, IsMasked, CurrentVersionId,
            CurrentRevision, UpdatedAtUtc)
        VALUES (2, '{campaignId:D}', '{key}', 1, {staleEpoch}, 1, 0, 'stale-pin', 1, '2026-01-01T00:00:00.0000000Z');
        """;

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
