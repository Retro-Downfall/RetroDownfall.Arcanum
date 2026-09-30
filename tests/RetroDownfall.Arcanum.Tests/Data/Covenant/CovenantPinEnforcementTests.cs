using System.Data;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// What a pin refuses, and what it deliberately does not.
/// </summary>
/// <remarks>
/// The Covenant is the one retention class with no time rule, so a pin has no sweep to exempt an entry
/// from. What it does is refuse <em>agent</em> authorship of the head it marks: an agent proposal that
/// would supersede it, and an approved retirement that would tombstone it. The operator's own verbs
/// still work, because a pin an operator has to fight is a pin they stop using.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class CovenantPinEnforcementTests
{

    private static CancellationToken Token => CancellationToken.None;

    private const string Key = "preference.builds";

    private static readonly Guid CampaignOne = CovenantOperationGateFixture.CampaignOne;

    [Fact]
    public async Task An_agent_proposal_against_a_pinned_Campaign_head_is_refused_by_the_write_authority()
    {

        await using CovenantServiceHarness harness = await CovenantServiceHarness.StartAsync(Token);

        await harness.AddCampaignAsync(CampaignOne, Token);

        await harness.SetAsync(CovenantScope.Campaign, CampaignOne, Key, "Build from the root.", Token);

        await PinAsync(harness, CovenantScope.Campaign, CampaignOne, CovenantLane.Proposed);

        // The live epoch, read the way a staging handler reads it. Writing a head advances it, so an
        // assumed zero would be refused as stale before the pin was ever consulted.
        long keyEpoch = (await ProbeAsync(harness, CovenantLane.Proposed)).KeyEpoch;

        Result<IReadOnlyList<CovenantMutationReceipt>> refused = await ApplyAgentAsync(
            harness,
            CovenantMutationFixture.AgentPropose(
                CampaignOne,
                Key,
                "The model suggests building from tools.",
                expectedRevision: 0,
                keyEpoch));

        Assert.True(refused.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, refused.Error.Code);

    }

    [Fact]
    public async Task An_approved_agent_retirement_of_a_pinned_head_is_refused()
    {

        await using CovenantServiceHarness harness = await CovenantServiceHarness.StartAsync(Token);

        await harness.AddCampaignAsync(CampaignOne, Token);

        await harness.SetAsync(CovenantScope.Campaign, CampaignOne, Key, "Build from the root.", Token);

        await PinAsync(harness, CovenantScope.Campaign, CampaignOne, CovenantLane.Confirmed);

        CovenantLaneHeadProbe head = await ProbeAsync(harness, CovenantLane.Confirmed);

        Result<IReadOnlyList<CovenantMutationReceipt>> refused = await ApplyAgentAsync(
            harness,
            CovenantMutationFixture.AgentRetire(
                CovenantOperationScope.ForCampaign(CampaignOne),
                Key,
                CovenantLane.Confirmed,
                head.LaneRevision,
                head.KeyEpoch));

        Assert.True(refused.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, refused.Error.Code);

    }

    /// <summary>
    /// The operator is not fighting their own pin. A pin that blocked them would be a pin they stop
    /// using, and an unused pin protects nothing.
    /// </summary>
    [Fact]
    public async Task The_operators_own_write_to_a_pinned_head_still_succeeds()
    {

        await using CovenantServiceHarness harness = await CovenantServiceHarness.StartAsync(Token);

        await harness.AddCampaignAsync(CampaignOne, Token);

        await harness.SetAsync(CovenantScope.Campaign, CampaignOne, Key, "Build from the root.", Token);

        await PinAsync(harness, CovenantScope.Campaign, CampaignOne, CovenantLane.Confirmed);

        // Reaches the same write authority the agent path was refused by, through the operator's path.
        await harness.RetireAsync(CovenantScope.Campaign, CampaignOne, Key, 1, Token);

        Assert.Equal(
            (int)CovenantOperation.Retire,
            await ScalarAsync(harness, "SELECT CurrentOperationCode FROM covenant_heads WHERE LaneCode = 1;"));

    }

    /// <summary>
    /// The staging probe reports the pin so a model is refused before it stages, and the turn keeps its
    /// answer instead of losing it to a write authority that runs inside the transaction carrying it.
    /// </summary>
    [Fact]
    public async Task The_staging_head_probe_reports_the_pin_so_a_turn_can_refuse_early()
    {

        await using CovenantServiceHarness harness = await CovenantServiceHarness.StartAsync(Token);

        await harness.AddCampaignAsync(CampaignOne, Token);

        await harness.SetAsync(CovenantScope.Campaign, CampaignOne, Key, "Build from the root.", Token);

        Assert.False((await ProbeAsync(harness, CovenantLane.Confirmed)).IsPinned);

        await PinAsync(harness, CovenantScope.Campaign, CampaignOne, CovenantLane.Confirmed);

        Assert.True((await ProbeAsync(harness, CovenantLane.Confirmed)).IsPinned);

    }

    /// <summary>
    /// The honest limit, asserted rather than implied.
    /// </summary>
    /// <remarks>
    /// Agent staging requires a canonical Campaign binding by the capability's own constructor, so the
    /// Proposed lane and agent retirement are Campaign-scoped by construction and a Global pin binds no
    /// agent path that exists. It is recorded and reported, and the surfaces that will consult it are
    /// the bulk-action and erasure ones. This test states that rather than pretending enforcement it
    /// cannot reach.
    /// </remarks>
    [Fact]
    public async Task A_Global_pin_is_recorded_and_reported_although_no_agent_path_can_reach_a_Global_head()
    {

        await using CovenantServiceHarness harness = await CovenantServiceHarness.StartAsync(Token);

        await harness.SetAsync(CovenantScope.Global, null, Key, "Build from the root.", Token);

        await PinAsync(harness, CovenantScope.Global, null, CovenantLane.Confirmed);

        Assert.Equal(
            1,
            await ScalarAsync(
                harness,
                "SELECT COUNT(*) FROM covenant_curation_heads WHERE CampaignId IS NULL AND IsPinned = 1;"));

        // An agent capability requires a Campaign binding, so there is no Global agent mutation for the
        // pin to refuse. Stated here so the pin's reach is written down rather than assumed.
        Assert.Throws<ArgumentException>(() => CovenantMutationFixture.AgentRetire(
            CovenantOperationScope.Global,
            Key,
            CovenantLane.Proposed,
            expectedRevision: 1,
            expectedKeyEpoch: 0));

    }

    /// <summary>
    /// A pin binds the key's binding epoch, which no ordinary write moves. Bound to the dependency
    /// epoch instead, the operator's own next write to the other lane would silently lift it.
    /// </summary>
    [Theory]
    [InlineData("correct")]
    [InlineData("retire")]
    public async Task A_Proposed_pin_survives_the_operators_own_later_write(string verb)
    {

        await using CovenantServiceHarness harness = await CovenantServiceHarness.StartAsync(Token);

        await harness.AddCampaignAsync(CampaignOne, Token);

        await harness.SetAsync(CovenantScope.Campaign, CampaignOne, Key, "Build from the root.", Token);

        await PinAsync(harness, CovenantScope.Campaign, CampaignOne, CovenantLane.Proposed);

        if (verb == "correct")
        {

            await harness.CorrectAsync(CovenantScope.Campaign, CampaignOne, Key, "Build from tools.", Token);

        }
        else
        {

            await harness.RetireAsync(CovenantScope.Campaign, CampaignOne, Key, 1, Token);

        }

        await AssertStillPinnedAgainstTheAgentAsync(harness, Key);

    }

    /// <summary>
    /// The key epoch row is keyed by normalized key alone, so a Global write of the same key advances
    /// the dependency epoch a Campaign pin would otherwise have been bound to.
    /// </summary>
    [Fact]
    public async Task A_Campaign_pin_survives_a_Global_write_of_the_same_key()
    {

        await using CovenantServiceHarness harness = await CovenantServiceHarness.StartAsync(Token);

        await harness.AddCampaignAsync(CampaignOne, Token);

        await PinAsync(harness, CovenantScope.Campaign, CampaignOne, CovenantLane.Proposed);

        await harness.SetAsync(CovenantScope.Global, null, Key, "Build from the root.", Token);

        await AssertStillPinnedAgainstTheAgentAsync(harness, Key);

    }

    /// <summary>
    /// A pin recorded before the key has any head binds epoch 0, and the first head creates the key's
    /// epoch row at binding epoch 0 too, so the pin is still the pin once there is something to pin.
    /// </summary>
    [Fact]
    public async Task A_keyless_pin_survives_the_first_operator_set()
    {

        const string FreshKey = "fresh.key";

        await using CovenantServiceHarness harness = await CovenantServiceHarness.StartAsync(Token);

        await harness.AddCampaignAsync(CampaignOne, Token);

        await PinAsync(harness, CovenantScope.Campaign, CampaignOne, CovenantLane.Proposed, FreshKey);

        // No epoch row exists yet, so the pin is recorded under the epoch a missing row reads as.
        Assert.Equal(0, await ScalarAsync(harness, "SELECT COUNT(*) FROM covenant_key_epochs;"));

        Assert.Equal(0, await ScalarAsync(harness, "SELECT KeyEpoch FROM covenant_curation_heads;"));

        await harness.SetAsync(CovenantScope.Campaign, CampaignOne, FreshKey, "Build from the root.", Token);

        await AssertStillPinnedAgainstTheAgentAsync(harness, FreshKey);

    }

    /// <summary>
    /// A key that existed before the binding epoch did carries a nonzero one: the key epoch it had at
    /// the upgrade. Every read of a pin has to join on the key row's own value, because a read that
    /// assumed zero would lift every pin an upgraded installation already held.
    /// </summary>
    [Fact]
    public async Task A_pin_on_a_key_with_a_nonzero_binding_epoch_is_recorded_under_it_and_survives_a_later_write()
    {

        await using CovenantServiceHarness harness = await CovenantServiceHarness.StartAsync(Token);

        await harness.AddCampaignAsync(CampaignOne, Token);

        await harness.SeedUpgradedKeyAsync(Key, epoch: 3, Token);

        await harness.SetAsync(CovenantScope.Campaign, CampaignOne, Key, "Build from the root.", Token);

        await PinAsync(harness, CovenantScope.Campaign, CampaignOne, CovenantLane.Proposed);

        Assert.Equal(3, await ScalarAsync(harness, "SELECT KeyEpoch FROM covenant_curation_heads;"));

        await harness.CorrectAsync(CovenantScope.Campaign, CampaignOne, Key, "Build from tools.", Token);

        Assert.Equal(
            5,
            await ScalarAsync(harness, $"SELECT KeyEpoch FROM covenant_key_epochs WHERE NormalizedKey = '{Key}';"));

        Assert.Equal(
            3,
            await ScalarAsync(
                harness,
                $"SELECT IncarnationEpoch FROM covenant_key_epochs WHERE NormalizedKey = '{Key}';"));

        await AssertStillPinnedAgainstTheAgentAsync(harness, Key);

    }

    [Fact]
    public async Task A_Global_pin_is_still_reported_after_a_Campaign_writes_the_same_key()
    {

        await using CovenantServiceHarness harness = await CovenantServiceHarness.StartAsync(Token);

        await harness.AddCampaignAsync(CampaignOne, Token);

        await harness.SetAsync(CovenantScope.Global, null, Key, "Build from the root.", Token);

        await PinAsync(harness, CovenantScope.Global, null, CovenantLane.Confirmed);

        await harness.SetAsync(CovenantScope.Campaign, CampaignOne, Key, "Build from tools.", Token);

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
    /// The two places a pin is enforced, asked in order: the staging probe that refuses a model early,
    /// and the write authority no mutation gets past.
    /// </summary>
    private static async Task AssertStillPinnedAgainstTheAgentAsync(CovenantServiceHarness harness, string key)
    {

        CovenantLaneHeadProbe probe = await ProbeAsync(harness, CovenantLane.Proposed, key);

        Assert.True(probe.IsPinned);

        Result<IReadOnlyList<CovenantMutationReceipt>> refused = await ApplyAgentAsync(
            harness,
            CovenantMutationFixture.AgentPropose(
                CampaignOne,
                key,
                "The model suggests building from tools.",
                expectedRevision: 0,
                probe.KeyEpoch));

        Assert.True(refused.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, refused.Error.Code);

    }

    private static async Task PinAsync(
        CovenantServiceHarness harness,
        CovenantScope scope,
        Guid? campaignId,
        CovenantLane lane,
        string key = Key)
    {

        Result<CovenantCurationResultDto> pinned = await harness.CurateAsync(
            CovenantCurationKind.Pin,
            scope,
            campaignId,
            key,
            Token,
            lane: lane);

        Assert.True(pinned.IsSuccess, pinned.IsFailure ? pinned.Error.Message : string.Empty);

    }

    private static async Task<Result<IReadOnlyList<CovenantMutationReceipt>>> ApplyAgentAsync(
        CovenantServiceHarness harness,
        CovenantMutationIntent intent)
    {

        await using SqliteTransaction transaction = (SqliteTransaction)await harness.Fixture.Connection
            .BeginTransactionAsync(IsolationLevel.Serializable, Token);

        Result<IReadOnlyList<CovenantMutationReceipt>> applied = await new CovenantMutationKernel()
            .ApplyBatchAsync(
                new CovenantMutationBatch(
                    await harness.Fixture.ReadDatasetGenerationAsync(Token),
                    1,
                    null,
                    CovenantMutationFixture.CommitTime,
                    [intent]),
                new CovenantMutationTransaction(harness.Fixture.Connection, transaction),
                Token);

        await transaction.RollbackAsync(Token);

        return applied;

    }

    private static async Task<CovenantLaneHeadProbe> ProbeAsync(
        CovenantServiceHarness harness,
        CovenantLane lane,
        string key = Key)
    {

        await using ICovenantSnapshotReadLease read =
            (await harness.Gate.AcquireReadAsync(CovenantOperationScope.ForCampaign(CampaignOne), Token)).Value;

        Result<CovenantLaneHeadProbe> probe = await harness.Fixture.Store.ProbeLaneHeadAsync(
            CovenantCanonicalFixture.CampaignContext(CampaignOne),
            lane,
            key,
            read,
            Token);

        Assert.True(probe.IsSuccess, probe.IsFailure ? probe.Error.Message : string.Empty);

        return probe.Value;

    }

    private static async Task<long> ScalarAsync(CovenantServiceHarness harness, string sql)
    {

        await using SqliteCommand command = harness.Fixture.Connection.CreateCommand();

        command.CommandText = sql;

        return Convert.ToInt64(
            await command.ExecuteScalarAsync(Token),
            System.Globalization.CultureInfo.InvariantCulture);

    }

}
