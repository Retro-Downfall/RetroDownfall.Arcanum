using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// Canonical Covenant quota boundaries, checked exactly at the limit and one past it.
/// </summary>
public sealed class CovenantQuotaTests
{

    private static readonly Guid CampaignOne = CovenantOperationGateFixture.CampaignOne;

    private static CancellationToken Token => CancellationToken.None;

    [Fact]
    public void The_pinned_ceilings_match_the_approved_contract()
    {

        Assert.Equal(1_023, CovenantLimits.MaxSetVersionsPerEntryLane);

        Assert.Equal(1_024, CovenantLimits.MaxVersionsPerEntryLane);

        Assert.Equal(256, CovenantLimits.MaxStableEntriesPerScope);

        Assert.Equal(64, CovenantLimits.MaxVersionSources);

        Assert.Equal(16_640, CovenantLimits.MaxMutationReceiptsPerScope);

        Assert.Equal(16 * 1_024 * 1_024, CovenantLimits.MaxCanonicalBytesPerScope);

        Assert.Equal(65_536, CovenantLimits.MaxPendingSearchOutboxRows);

        Assert.Equal(1_024, CovenantLimits.MaxTurnReceiptsPerSession);

        Assert.Equal(16_384, CovenantLimits.MaxPublicTurnClaimsPerSession);

        Assert.Equal(1_048_576, CovenantLimits.MaxPublicTurnClaimsInstallationWide);

        Assert.Equal(16_384, CovenantLimits.MaxAssistantFinalizationGuardsPerSession);

        Assert.Equal(1_048_576, CovenantLimits.MaxAssistantFinalizationGuardsInstallationWide);

    }

    [Fact]
    public async Task An_empty_scope_reports_zeroed_counters()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        CovenantQuotaSnapshot snapshot = await CheckAsync(fixture, CovenantOperationScope.Global, Nothing);

        Assert.Equal(0, snapshot.ActiveEntriesInScope);

        Assert.Equal(0, snapshot.VersionsInScope);

        Assert.Equal(0, snapshot.CanonicalBytesInScope);

        Assert.Equal(0, snapshot.PendingOutboxRows);

    }

    [Fact]
    public async Task Counters_are_scoped_and_never_leak_between_campaigns()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        await fixture.AddCampaignAsync(CampaignOne, "one", Token);

        _ = await fixture.SeedHeadAsync(
            CovenantScope.Campaign,
            CampaignOne,
            "campaign.one",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Campaign content.",
            Token);

        _ = await fixture.SeedHeadAsync(
            CovenantScope.Global,
            null,
            "global.one",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Global content.",
            Token);

        CovenantQuotaSnapshot global = await CheckAsync(fixture, CovenantOperationScope.Global, Nothing);

        CovenantQuotaSnapshot campaign = await CheckAsync(
            fixture,
            CovenantOperationScope.ForCampaign(CampaignOne),
            Nothing);

        Assert.Equal(1, global.ActiveEntriesInScope);

        Assert.Equal(1, campaign.ActiveEntriesInScope);

        Assert.True(global.CanonicalBytesInScope > 0);

        Assert.True(campaign.CanonicalBytesInScope > 0);

        Assert.Equal(0, global.AgentVersionsInCampaign);

    }

    [Fact]
    public async Task Agent_versions_and_bytes_are_counted_separately()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        await fixture.AddCampaignAsync(CampaignOne, "one", Token);

        _ = await fixture.SeedHeadAsync(
            CovenantScope.Campaign,
            CampaignOne,
            "campaign.proposed",
            CovenantLane.Proposed,
            CovenantOperation.Set,
            "A proposal.",
            Token);

        _ = await fixture.SeedHeadAsync(
            CovenantScope.Campaign,
            CampaignOne,
            "campaign.confirmed",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Operator content.",
            Token);

        CovenantQuotaSnapshot snapshot = await CheckAsync(
            fixture,
            CovenantOperationScope.ForCampaign(CampaignOne),
            Nothing);

        Assert.Equal(2, snapshot.VersionsInScope);

        Assert.Equal(1, snapshot.AgentVersionsInCampaign);

        Assert.True(snapshot.AgentBytesInCampaign > 0);

        Assert.True(snapshot.AgentBytesInCampaign < snapshot.CanonicalBytesInScope);

    }

    [Fact]
    public async Task A_demand_at_the_ceiling_is_accepted_and_one_past_it_is_refused()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        CovenantQuotaDemand atLimit = Nothing with
        {

            NewEntries = CovenantLimits.MaxStableEntriesPerScope,

        };

        Result<CovenantQuotaSnapshot> accepted = await RunAsync(fixture, CovenantOperationScope.Global, atLimit);

        Assert.True(accepted.IsSuccess);

        Result<CovenantQuotaSnapshot> refused = await RunAsync(
            fixture,
            CovenantOperationScope.Global,
            Nothing with { NewEntries = CovenantLimits.MaxStableEntriesPerScope + 1 });

        Assert.Equal("Covenant.CapacityExceeded", refused.Error.Code);

    }

    [Fact]
    public async Task Every_bounded_resource_has_its_own_refusal()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        CovenantQuotaDemand[] overLimit =
        [
            Nothing with { NewVersions = CovenantLimits.MaxVersionsPerScope + 1 },

            Nothing with { NewSetVersions = CovenantLimits.MaxSetVersionsPerScope + 1 },

            Nothing with { NewCanonicalBytes = CovenantLimits.MaxCanonicalBytesPerScope + 1L },

            Nothing with { NewMutationReceipts = CovenantLimits.MaxMutationReceiptsPerScope + 1 },

            Nothing with { NewProvenanceRows = CovenantLimits.MaxAttachmentProvenanceRowsPerCampaign + 1 },

            Nothing with { NewOutboxRows = CovenantLimits.MaxPendingSearchOutboxRows + 1 },
        ];

        foreach (CovenantQuotaDemand demand in overLimit)
        {

            Result<CovenantQuotaSnapshot> refused = await RunAsync(fixture, CovenantOperationScope.Global, demand);

            Assert.Equal("Covenant.CapacityExceeded", refused.Error.Code);

        }

    }

    [Fact]
    public async Task Agent_ceilings_apply_only_to_agent_demand()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        await fixture.AddCampaignAsync(CampaignOne, "one", Token);

        CovenantOperationScope scope = CovenantOperationScope.ForCampaign(CampaignOne);

        Result<CovenantQuotaSnapshot> refusedVersions = await RunAsync(
            fixture,
            scope,
            Nothing with { NewAgentVersions = CovenantLimits.MaxAgentVersionsPerCampaign + 1 });

        Assert.Equal("Covenant.CapacityExceeded", refusedVersions.Error.Code);

        Result<CovenantQuotaSnapshot> refusedBytes = await RunAsync(
            fixture,
            scope,
            Nothing with { NewAgentBytes = CovenantLimits.MaxAgentBytesPerCampaign + 1L });

        Assert.Equal("Covenant.CapacityExceeded", refusedBytes.Error.Code);

        Result<CovenantQuotaSnapshot> allowed = await RunAsync(
            fixture,
            scope,
            Nothing with { NewAgentVersions = CovenantLimits.MaxAgentVersionsPerCampaign });

        Assert.True(allowed.IsSuccess);

    }

    [Fact]
    public async Task The_mutation_kernel_refuses_a_batch_that_would_exceed_a_ceiling()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        Guid generation = await fixture.ReadDatasetGenerationAsync(Token);

        // Fill the scope's stable-entry ceiling, then ask for one more.
        for (int index = 0; index < CovenantLimits.MaxStableEntriesPerScope; index++)
        {

            _ = await fixture.SeedHeadAsync(
                CovenantScope.Global,
                null,
                $"global.filler{index:0000}",
                CovenantLane.Confirmed,
                CovenantOperation.Set,
                $"Filler {index}.",
                Token);

        }

        Result<IReadOnlyList<CovenantMutationReceipt>> refused = await CovenantMutationFixture.ApplyAsync(
            fixture,
            CovenantMutationFixture.Batch(
                generation,
                CovenantMutationFixture.OperatorSet(
                    CovenantOperationScope.Global,
                    "global.overflow",
                    "One too many.",
                    0,
                    0)),
            Token);

        Assert.Equal("Covenant.CapacityExceeded", refused.Error.Code);

    }

    private static CovenantQuotaDemand Nothing => new(0, 0, 0, 0, 0, 0, 0, 0, 0);

    private static async Task<CovenantQuotaSnapshot> CheckAsync(
        CovenantCanonicalFixture fixture,
        CovenantOperationScope scope,
        CovenantQuotaDemand demand)
    {

        Result<CovenantQuotaSnapshot> result = await RunAsync(fixture, scope, demand);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        return result.Value;

    }

    private static Task<Result<CovenantQuotaSnapshot>> RunAsync(
        CovenantCanonicalFixture fixture,
        CovenantOperationScope scope,
        CovenantQuotaDemand demand) =>
        CovenantCapacityFixture.InTransactionAsync(
            fixture,
            transaction => new CovenantQuotaGuard()
                .CheckCanonicalCapacityAsync(scope, demand, transaction, Token)
                .AsTask(),
            Token,
            commit: false);

}
