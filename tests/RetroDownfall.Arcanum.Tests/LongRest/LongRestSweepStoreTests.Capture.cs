using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed partial class LongRestSweepStoreTests
{
    private const string FirstSource = "10000000-0000-0000-0000-000000000001";

    // The corpus a completed sweep covers is the eligible one. A pinned memory is not part of it, so a completed sweep still replays beside it;
    // and the day the memory is unpinned it is eligible and in no generation, which is new debt, not a replay.
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task A_completed_sweep_replays_beside_a_pinned_source_until_that_source_is_unpinned(bool protect, bool unpin)
    {
        await using Fixture fixture = await Fixture.CreateAsync(protect, sourceCount: 3, duplicates: true, policyFault: "pin");

        Assert.Equal(2L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_discovery_leaves;"));

        Assert.Equal(0L, await fixture.H.ScalarAsync($"SELECT count(*) FROM long_rest_discovery_leaves WHERE MemoryId = '{FirstSource}';"));

        LongRestSweep sweep = await fixture.BeginAsync();

        LongRestSweepWork work = LongRestSweepHarness.Require(await fixture.Store.ReadNextWorkAsync(sweep, fixture.Capability, CancellationToken.None))!;

        Assert.Equal(2, work.Inputs.Length);

        LongRestSweep committed = LongRestSweepHarness.Require(await fixture.Store.CommitExactDuplicatesAsync(sweep, work.Identity, fixture.Capability, CancellationToken.None));

        await DrainCompletedCorpusAsync(fixture, committed);

        if (unpin)
        {
            await fixture.H.Saga.Store.SetPinAsync(FirstSource, false, DateTimeOffset.UtcNow, CancellationToken.None);
        }

        Guid? found = LongRestSweepHarness.Require(await fixture.Store.FindCompletedReplayAsync(fixture.Generation.Fence.CanonicalScope,
            fixture.Generation.Scope, fixture.Generation.Recipe, CancellationToken.None));

        Assert.Equal(unpin ? null : fixture.Generation.OperationId, found);
    }

    // Pinning never creates an Annals claim, and neither does a memory written while claims were off, so a pinned memory may have no claim head at
    // all. Capture skips it as ineligible, so the completed-corpus proof must skip it too: if the proof still counted it as uncovered, no completed
    // sweep could replay and every eligible turn would start a new paid sweep. A claim-less memory that is not pinned stays uncovered debt.
    [Fact]
    public async Task A_completed_sweep_replays_beside_a_pinned_memory_that_has_no_annals_claim()
    {
        await using Fixture fixture = await Fixture.CreateAsync(false, sourceCount: 3, duplicates: true);

        LongRestSweep sweep = await fixture.BeginAsync();

        LongRestSweepWork work = LongRestSweepHarness.Require(await fixture.Store.ReadNextWorkAsync(sweep, null, CancellationToken.None))!;

        LongRestSweep committed = LongRestSweepHarness.Require(await fixture.Store.CommitExactDuplicatesAsync(sweep, work.Identity, null, CancellationToken.None));

        await DrainCompletedCorpusAsync(fixture, committed);

        async Task<Guid?> ReplayOperationAsync() => LongRestSweepHarness.Require(await fixture.Store.FindCompletedReplayAsync(
            fixture.Generation.Fence.CanonicalScope, fixture.Generation.Scope, fixture.Generation.Recipe, CancellationToken.None));

        Assert.Equal(fixture.Generation.OperationId, await ReplayOperationAsync());

        await fixture.H.ExecuteAsync("""
            INSERT INTO saga_memories (Id, Content, CreatedAt, SessionId, ScopeKindCode, CampaignId, PinnedAtUtc)
            VALUES ('20000000-0000-0000-0000-000000000001', 'pinned without a claim', '2026-10-10T00:00:00.0000000Z', NULL, 1, NULL, '2026-10-10T00:00:00.0000000Z');
            """);

        Assert.Equal(0L, await fixture.H.ScalarAsync("SELECT count(*) FROM annal_claims WHERE SubjectId = '20000000-0000-0000-0000-000000000001';"));

        Assert.Equal(fixture.Generation.OperationId, await ReplayOperationAsync());

        await fixture.H.ExecuteAsync("""
            INSERT INTO saga_memories (Id, Content, CreatedAt, SessionId, ScopeKindCode, CampaignId)
            VALUES ('20000000-0000-0000-0000-000000000002', 'unpinned without a claim', '2026-10-10T00:00:00.0000000Z', NULL, 1, NULL);
            """);

        Assert.Null(await ReplayOperationAsync());
    }

    // A source the sweep's own decision suppresses after capture is still a leaf of the generation, and the candidate check still refuses it:
    // it never spends a place in a lane, however well it matches.
    [Fact]
    public async Task A_source_the_sweep_itself_consolidates_is_not_a_neighbour_afterwards()
    {
        await using Fixture fixture = await Fixture.CreateAsync(false, sourceCount: 3, duplicates: true);

        Assert.Equal(3L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_discovery_leaves;"));

        LongRestSweep sweep = await fixture.BeginAsync();

        LongRestSweepWork work = LongRestSweepHarness.Require(await fixture.Store.ReadNextWorkAsync(sweep, null, CancellationToken.None))!;

        _ = LongRestSweepHarness.Require(await fixture.Store.CommitExactDuplicatesAsync(sweep, work.Identity, null, CancellationToken.None));

        Assert.Equal(2L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_suppressions;"));

        LongRestCandidateGroup[] groups = LongRestSweepHarness.Require(await fixture.H.Store.DiscoverAsync(fixture.Generation.GenerationId,
            await fixture.H.TargetAsync(FirstSource), null, CancellationToken.None));

        Assert.Empty(groups);
    }
}
