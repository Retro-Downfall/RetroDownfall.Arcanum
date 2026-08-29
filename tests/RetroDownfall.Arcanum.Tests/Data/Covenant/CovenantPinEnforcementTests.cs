using System.Data;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
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

    private static async Task PinAsync(
        CovenantServiceHarness harness,
        CovenantScope scope,
        Guid? campaignId,
        CovenantLane lane)
    {

        Result<CovenantCurationResultDto> pinned = await harness.CurateAsync(
            CovenantCurationKind.Pin,
            scope,
            campaignId,
            Key,
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
        CovenantLane lane)
    {

        await using ICovenantSnapshotReadLease read =
            (await harness.Gate.AcquireReadAsync(CovenantOperationScope.ForCampaign(CampaignOne), Token)).Value;

        Result<CovenantLaneHeadProbe> probe = await harness.Fixture.Store.ProbeLaneHeadAsync(
            CovenantCanonicalFixture.CampaignContext(CampaignOne),
            lane,
            Key,
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
