using System.Collections.Immutable;

using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.LongRest;

// An anchor's frozen groups are packs that each render within the recipe's input budget. The recipe carries that budget, the planner
// and the renderer measure one layout, and a neighbour that cannot fit beside its anchor is counted, never silently dropped.
public sealed partial class LongRestSweepStoreTests
{
    private const string SkippedMetric = "arcanum.long_rest.sweep.skipped_candidates";

    private static string Padded(int index, int bytes) => "related observation " + index + " " + string.Join(' ', Enumerable.Repeat("lorem ipsum dolor", bytes / 18));

    [Fact]
    public async Task An_anchor_group_over_the_input_budget_is_frozen_as_packs_that_each_keep_the_anchor_and_render_within_the_budget()
    {
        await using Fixture fixture = await Fixture.CreateAsync(protect: false, sourceCount: 6, inputBudget: 6144, text: index => Padded(index, 1500));

        using AsyncFlowMetricCapture metrics = new(SkippedMetric);

        LongRestSweep sweep = await fixture.BeginAsync();

        LongRestSweepWork first = LongRestSweepHarness.Require(await fixture.Store.ReadNextWorkAsync(sweep, fixture.Capability, CancellationToken.None))!;

        LongRestTarget anchor = await fixture.H.TargetAsync("10000000-0000-0000-0000-000000000001");

        Assert.Equal(anchor, first.Inputs[0].Target);

        // The ranked neighbours as discovery orders them, before packing.
        string[] ranked = [.. LongRestSweepHarness.Require(await fixture.H.Store.DiscoverAsync(fixture.Generation.GenerationId, anchor, fixture.Capability, CancellationToken.None))
            .SelectMany(group => group.Targets.Skip(1)).Select(target => target.MemoryId)];

        // The fixed prompt, the anchor and two neighbours fit 6144 bytes; five neighbours therefore need three packs.
        Assert.Equal(3L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_work_items WHERE KindCode = 3 AND DispositionCode = 1;"));

        Assert.Equal(3, first.Inputs.Length);

        long fixedBytes = LongRestPromptLayout.FixedBytes(LongRestProviderPayloadKind.Adjudication);

        ImmutableArray<string>.Builder neighbors = ImmutableArray.CreateBuilder<string>();

        LongRestSweepWork? current = first;

        for (int index = 0; index < 3; index++)
        {
            Assert.NotNull(current);

            Assert.Equal(anchor, current.Inputs[0].Target);

            Assert.True(fixedBytes + current.Inputs.Sum(LongRestPromptLayout.AdjudicationMemoryBytes) <= 6144);

            Assert.True(LongRestProviderPayload.Adjudication(current.Identity, current.Inputs, 6144).IsSuccess, "A frozen pack must build its payload under the recipe budget.");

            neighbors.AddRange(current.Inputs.Skip(1).Select(input => input.Target.MemoryId));

            LongRestAdjudication accepted = await fixture.AcceptAsync(sweep, current, LongRestAdjudicationKind.NoChange);

            sweep = LongRestSweepHarness.Require(await fixture.Store.CommitDecisionAsync(sweep, accepted, fixture.Capability, CancellationToken.None));

            current = index < 2 ? LongRestSweepHarness.Require(await fixture.Store.ReadNextWorkAsync(sweep, fixture.Capability, CancellationToken.None)) : null;
        }

        // Ranked order kept, no neighbour twice, every one of the five packed, nothing counted as skipped.
        Assert.Equal(5, ranked.Length);

        Assert.Equal(ranked, neighbors.ToArray());

        Assert.Equal(0, metrics.Sum(SkippedMetric));
    }

    [Fact]
    public async Task A_neighbor_that_cannot_fit_beside_its_anchor_is_left_out_and_counted_by_reason_alone()
    {
        await using Fixture fixture = await Fixture.CreateAsync(protect: false, sourceCount: 6, inputBudget: 6144,
            text: index => Padded(index, index == 3 ? 4600 : 1500));

        using AsyncFlowMetricCapture metrics = new(SkippedMetric);

        LongRestSweep sweep = await fixture.BeginAsync();

        LongRestSweepWork first = LongRestSweepHarness.Require(await fixture.Store.ReadNextWorkAsync(sweep, fixture.Capability, CancellationToken.None))!;

        LongRestTarget huge = await fixture.H.TargetAsync("10000000-0000-0000-0000-000000000003");

        // It fits a summary request alone (the hierarchy built), but not an adjudication request beside the anchor.
        Assert.DoesNotContain(await AllPackedTargetsAsync(fixture, sweep, first), target => target == huge);

        Assert.Equal(1, metrics.Sum(SkippedMetric, "reason", "oversize"));

        Assert.Equal(0, metrics.Sum(SkippedMetric, "reason", "pack_limit"));

        Assert.All(metrics.Measurements, measurement => Assert.Equal(new[] { "reason" }, measurement.Tags.Keys.ToArray()));
    }

    [Fact]
    public async Task An_anchor_none_of_whose_neighbors_fit_beside_it_is_passed_over_and_the_sweep_still_completes()
    {
        // Every memory is 2.5 KiB: the fixed prompt plus a pair is about 6 KiB, over the minimum 4096-byte recipe.
        await using Fixture fixture = await Fixture.CreateAsync(protect: false, sourceCount: 5, inputBudget: LongRestPromptLayout.MinimumInputUtf8Bytes,
            text: index => Padded(index, 2500));

        using AsyncFlowMetricCapture metrics = new(SkippedMetric);

        LongRestSweep sweep = await fixture.BeginAsync();

        for (int step = 0; step < 20 && sweep.Phase != LongRestSweepPhase.Completed; step++)
        {
            Assert.Null(LongRestSweepHarness.Require(await fixture.Store.ReadNextWorkAsync(sweep, fixture.Capability, CancellationToken.None)));

            sweep = await fixture.BeginAsync();
        }

        Assert.Equal(LongRestSweepPhase.Completed, sweep.Phase);

        Assert.Equal(0L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_work_items WHERE KindCode = 3;"));

        Assert.Equal(0L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_provider_effects;"));

        Assert.True(metrics.Sum(SkippedMetric, "reason", "oversize") >= 4);
    }

    [Fact]
    public async Task A_group_that_fits_the_budget_is_frozen_exactly_as_the_discovery_groups_it()
    {
        await using Fixture fixture = await Fixture.CreateAsync(protect: false, sourceCount: 24);

        using AsyncFlowMetricCapture metrics = new(SkippedMetric);

        LongRestSweep sweep = await fixture.BeginAsync();

        LongRestSweepWork first = LongRestSweepHarness.Require(await fixture.Store.ReadNextWorkAsync(sweep, fixture.Capability, CancellationToken.None))!;

        LongRestTarget anchor = await fixture.H.TargetAsync("10000000-0000-0000-0000-000000000001");

        LongRestCandidateGroup[] groups = LongRestSweepHarness.Require(await fixture.H.Store.DiscoverAsync(fixture.Generation.GenerationId, anchor, fixture.Capability, CancellationToken.None));

        Assert.True(groups.Length > 1);

        Assert.Equal((long)groups.Length, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_work_items WHERE KindCode = 3 AND DispositionCode = 1;"));

        Assert.Equal(groups[0].Targets.ToArray(), first.Inputs.Select(input => input.Target).ToArray());

        Assert.Equal(0, metrics.Sum(SkippedMetric));
    }

    // Reads the packs of the first anchor by finishing each as NoChange, and returns every neighbour that was packed.
    private static async Task<List<LongRestTarget>> AllPackedTargetsAsync(Fixture fixture, LongRestSweep sweep, LongRestSweepWork first)
    {
        List<LongRestTarget> packed = [];

        LongRestSweepWork? current = first;

        while (current is not null && current.Inputs[0].Target.MemoryId == first.Inputs[0].Target.MemoryId)
        {
            packed.AddRange(current.Inputs.Skip(1).Select(input => input.Target));

            LongRestAdjudication accepted = await fixture.AcceptAsync(sweep, current, LongRestAdjudicationKind.NoChange);

            sweep = LongRestSweepHarness.Require(await fixture.Store.CommitDecisionAsync(sweep, accepted, fixture.Capability, CancellationToken.None));

            if (sweep.CommittedItemCursor is not null) break;

            current = LongRestSweepHarness.Require(await fixture.Store.ReadNextWorkAsync(sweep, fixture.Capability, CancellationToken.None));
        }

        return packed;
    }
}
