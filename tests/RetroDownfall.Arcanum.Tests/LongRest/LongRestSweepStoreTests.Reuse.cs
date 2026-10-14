using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.LongRest;

// A set of memories that has been decided NoChange in a generation is never decided again in it, whichever of its members the sweep
// meets next; and the decision belongs to that generation alone, so a successor generation, which may see different memories, asks again.
public sealed partial class LongRestSweepStoreTests
{
    private const string ReusedMetric = "arcanum.long_rest.sweep.reused_decisions";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_identical_set_decided_NoChange_is_reused_inside_its_generation_and_asked_again_in_the_next(bool protect)
    {
        await using Fixture fixture = await Fixture.CreateAsync(protect, sourceCount: 3);

        using AsyncFlowMetricCapture metrics = new(ReusedMetric);

        LongRestSweep sweep = await fixture.BeginAsync();

        LongRestSweepWork first = LongRestSweepHarness.Require(await fixture.Store.ReadNextWorkAsync(sweep, fixture.Capability, CancellationToken.None))!;

        Assert.Equal(3, first.Inputs.Length);

        LongRestAdjudication accepted = await fixture.AcceptAsync(sweep, first, LongRestAdjudicationKind.NoChange);

        LongRestSweep committed = LongRestSweepHarness.Require(await fixture.Store.CommitDecisionAsync(sweep, accepted, fixture.Capability, CancellationToken.None));

        Assert.Equal(0L, metrics.Sum(ReusedMetric));

        // The second memory finds the same three: the set is decided, so no work item is made and the scan simply moves past it.
        Assert.Null(LongRestSweepHarness.Require(await fixture.Store.ReadNextWorkAsync(committed, fixture.Capability, CancellationToken.None)));

        Assert.Equal("10000000-0000-0000-0000-000000000002", await fixture.H.ScalarAsync("SELECT DiscoveryCursor FROM long_rest_sweeps;"));

        Assert.Equal(1L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_work_items WHERE KindCode = 3;"));

        Assert.Equal(1L, metrics.Sum(ReusedMetric));

        LongRestSweep scanning = await fixture.BeginAsync();

        Assert.Null(LongRestSweepHarness.Require(await fixture.Store.ReadNextWorkAsync(scanning, fixture.Capability, CancellationToken.None)));

        Assert.Equal("10000000-0000-0000-0000-000000000003", await fixture.H.ScalarAsync("SELECT DiscoveryCursor FROM long_rest_sweeps;"));

        scanning = await fixture.BeginAsync();

        Assert.Null(LongRestSweepHarness.Require(await fixture.Store.ReadNextWorkAsync(scanning, fixture.Capability, CancellationToken.None)));

        LongRestSweep completed = await fixture.BeginAsync();

        Assert.Equal(LongRestSweepPhase.Completed, completed.Phase);

        Assert.Equal(2L, metrics.Sum(ReusedMetric));

        Assert.Equal(1L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_work_items WHERE KindCode = 3;"));

        Assert.Equal(1L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_adjudications;"));

        Assert.Equal(1, fixture.AdjudicationSends);

        // A successor generation of the same scope and partition holds the same three memories, and asks again: a decision is never reused
        // beyond the generation, and never across a partition, that made it.
        LongRestDiscoveryGeneration captured = await fixture.H.CaptureAsync(null, fixture.Generation.Scope.ProtectedPartitionDigest, protect ? fixture.Authority : null);

        LongRestDiscoveryGeneration successor = await fixture.H.BuildAsync(captured);

        LongRestMaintenanceCapability? capability = protect ? await fixture.CreateCapabilityAsync(successor) : null;

        LongRestSweep next = LongRestSweepHarness.Require(await fixture.Store.BeginOrResumeAsync(successor.OperationId, successor.Scope, successor.Fence.Configuration, CancellationToken.None));

        LongRestSweepWork again = LongRestSweepHarness.Require(await fixture.Store.ReadNextWorkAsync(next, capability, CancellationToken.None))!;

        Assert.Equal(3, again.Inputs.Length);

        Assert.Equal(successor.GenerationId, again.Identity.GenerationId);

        Assert.Equal(first.Identity.SourceDigest, again.Identity.SourceDigest);

        Assert.Equal(2L, metrics.Sum(ReusedMetric));
    }

    // A, B and C name one another by their vectors; D names only C, by a rare word. So A's set and B's set are {A, B, C} and C's is {A, B, C, D}.
    [Fact]
    public async Task A_decided_set_that_differs_by_one_member_is_a_new_question()
    {
        await using Fixture fixture = await Fixture.CreateAsync(false, sourceCount: 4, policyFault: "separate-fourth",
            text: index => index switch { 1 => "grape one", 2 => "grape two", 3 => "grape three zebra", _ => "zebra four" });

        LongRestSweep sweep = await fixture.BeginAsync();

        LongRestSweepWork first = LongRestSweepHarness.Require(await fixture.Store.ReadNextWorkAsync(sweep, null, CancellationToken.None))!;

        Assert.Equal(["10000000-0000-0000-0000-000000000001", "10000000-0000-0000-0000-000000000002", "10000000-0000-0000-0000-000000000003"],
            first.Inputs.Select(static input => input.Target.MemoryId));

        LongRestAdjudication accepted = await fixture.AcceptAsync(sweep, first, LongRestAdjudicationKind.NoChange);

        LongRestSweep committed = LongRestSweepHarness.Require(await fixture.Store.CommitDecisionAsync(sweep, accepted, null, CancellationToken.None));

        // B names the same three, so there is nothing to ask.
        Assert.Null(LongRestSweepHarness.Require(await fixture.Store.ReadNextWorkAsync(committed, null, CancellationToken.None)));

        Assert.Equal(1L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_work_items WHERE KindCode = 3;"));

        // C names four, one more than the decided set, so deciding the three settles nothing about the four.
        LongRestSweepWork second = LongRestSweepHarness.Require(await fixture.Store.ReadNextWorkAsync(await fixture.BeginAsync(), null, CancellationToken.None))!;

        Assert.Equal(4, second.Inputs.Length);

        Assert.Equal("10000000-0000-0000-0000-000000000003", second.Inputs[0].Target.MemoryId);

        Assert.NotEqual(first.Identity.SourceDigest, second.Identity.SourceDigest);

        Assert.Equal(2L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_work_items WHERE KindCode = 3;"));
    }
}
