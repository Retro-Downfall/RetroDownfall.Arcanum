using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.LongRest;

// A set of memories that has been decided NoChange in a generation is never decided again in it, whichever of its members the sweep
// meets next; and the decision belongs to that generation alone, so a successor generation, which may see different memories, asks again.
public sealed partial class LongRestSweepStoreTests
{
    private const string ReusedMetric = "arcanum.long_rest.sweep.reused_decisions";

    // Reads on until the next unit that needs a decision: a pass that only moves the scan past decided sets, or closes a unit whose members an
    // earlier decision consolidated, returns no work, and the sweep's revision has moved by then.
    private static async Task<(LongRestSweepWork? Work, LongRestSweep Sweep)> NextUnitAsync(Fixture fixture, LongRestSweep sweep, LongRestMaintenanceCapability? capability)
    {
        for (int step = 0; step < 16; step++)
        {
            LongRestSweepWork? work = LongRestSweepHarness.Require(await fixture.Store.ReadNextWorkAsync(sweep, capability, CancellationToken.None));

            if (work is not null) return (work, sweep);

            sweep = await fixture.BeginAsync();

            if (sweep.Phase == LongRestSweepPhase.Completed) return (null, sweep);
        }

        return (null, sweep);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_identical_set_decided_NoChange_is_reused_inside_its_generation_and_asked_again_in_the_next(bool protect)
    {
        await using Fixture fixture = await Fixture.CreateAsync(protect, sourceCount: 3);

        using AsyncFlowMetricCapture metrics = new(ReusedMetric);

        LongRestSweep sweep = await fixture.BeginAsync();

        (LongRestSweepWork? start, sweep) = await NextUnitAsync(fixture, sweep, fixture.Capability);

        LongRestSweepWork first = start!;

        // The anchor's clique is the set of all three, and is asked once.
        Assert.Equal(3, first.Inputs.Length);

        Assert.Equal(0L, metrics.Sum(ReusedMetric));

        LongRestSweepWork current = first;

        for (int step = 0; step < 8; step++)
        {
            LongRestAdjudication accepted = await fixture.AcceptAsync(sweep, current, LongRestAdjudicationKind.NoChange);

            sweep = LongRestSweepHarness.Require(await fixture.Store.CommitDecisionAsync(sweep, accepted, fixture.Capability, CancellationToken.None));

            (LongRestSweepWork? following, LongRestSweep moved) = await NextUnitAsync(fixture, sweep, fixture.Capability);

            sweep = moved;

            if (following is null) break;

            current = following;
        }

        Assert.Equal(LongRestSweepPhase.Completed, (await fixture.BeginAsync()).Phase);

        // The set of three, then the anchor's pair with each of the other two, then the one pair the second memory has that the first did not.
        // Every other set the second and third memories find is one this generation has already decided NoChange: no work item, no request.
        Assert.Equal(4L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_work_items WHERE KindCode = 3;"));

        Assert.Equal(4L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_adjudications;"));

        Assert.Equal(4, fixture.AdjudicationSends);

        Assert.Equal(5L, metrics.Sum(ReusedMetric));

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

        Assert.Equal(5L, metrics.Sum(ReusedMetric));
    }

    // A, B and C name one another by their vectors; D is 20 degrees from C and so names C, but 27 and 30 degrees from B and A and so names neither.
    // A's set and B's set are {A, B, C}; C's are {A, B, C} and {C, D}.
    [Fact]
    public async Task A_decided_set_that_differs_by_one_member_is_a_new_question()
    {
        await using Fixture fixture = await Fixture.CreateAsync(false, sourceCount: 4,
            text: index => index switch { 1 => "grape alpha", 2 => "grape beta", 3 => "grape gamma zebra", _ => "zebra delta" },
            vector: index => LongRestPackingData.Angle(index switch { 1 => 0, 2 => 3, 3 => 10, _ => 30 }).ToArray());

        LongRestSweep sweep = await fixture.BeginAsync();

        LongRestSweepWork first = LongRestSweepHarness.Require(await fixture.Store.ReadNextWorkAsync(sweep, null, CancellationToken.None))!;

        Assert.Equal(["10000000-0000-0000-0000-000000000001", "10000000-0000-0000-0000-000000000002", "10000000-0000-0000-0000-000000000003"],
            first.Inputs.Select(static input => input.Target.MemoryId));

        List<string[]> asked = [first.Inputs.Select(static input => input.Target.MemoryId).ToArray()];

        LongRestSweepWork current = first;

        for (int step = 0; step < 12; step++)
        {
            LongRestAdjudication accepted = await fixture.AcceptAsync(sweep, current, LongRestAdjudicationKind.NoChange);

            sweep = LongRestSweepHarness.Require(await fixture.Store.CommitDecisionAsync(sweep, accepted, null, CancellationToken.None));

            (LongRestSweepWork? following, LongRestSweep moved) = await NextUnitAsync(fixture, sweep, null);

            sweep = moved;

            if (following is null) break;

            asked.Add(following.Inputs.Select(static input => input.Target.MemoryId).ToArray());

            current = following;
        }

        // A asks the three, then its pair with each of B and C. B finds the same three (decided: nothing to ask), the pair with A (decided), and
        // the pair with C (new). C finds the same three (decided), its pairs with B and A (decided) and, because D is paired with C alone and so is not
        // in that pack, a pack of its own: {C, D} differs from {A, B, C} by a member and is a new question.
        string[] four = ["10000000-0000-0000-0000-000000000003", "10000000-0000-0000-0000-000000000004"];

        Assert.Equal(5, asked.Count);

        Assert.Contains(asked, ids => ids.SequenceEqual(four));

        Assert.Equal(1, asked.Count(static ids => ids.Length == 3));

        Assert.Equal(5L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_work_items WHERE KindCode = 3;"));

        Assert.Equal(5, fixture.AdjudicationSends);
    }
}
