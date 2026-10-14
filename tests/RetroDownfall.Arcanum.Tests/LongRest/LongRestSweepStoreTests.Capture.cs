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
