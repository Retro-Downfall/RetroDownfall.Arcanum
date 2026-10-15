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
        await using Fixture fixture = await Fixture.CreateAsync(protect: false, sourceCount: 6, inputBudget: 8192, text: index => Padded(index, 1500));

        using AsyncFlowMetricCapture metrics = new(SkippedMetric);

        LongRestSweep sweep = await fixture.BeginAsync();

        LongRestSweepWork first = LongRestSweepHarness.Require(await fixture.Store.ReadNextWorkAsync(sweep, fixture.Capability, CancellationToken.None))!;

        LongRestTarget anchor = await fixture.H.TargetAsync("10000000-0000-0000-0000-000000000001");

        Assert.Equal(anchor, first.Inputs[0].Target);

        // The ranked neighbours as discovery nominates them, before packing.
        string[] ranked = [.. LongRestSweepHarness.Require(await fixture.H.Store.DiscoverAsync(fixture.Generation.GenerationId, anchor, fixture.Capability, CancellationToken.None))
            .SelectMany(group => group.Targets.Skip(1)).Select(target => target.MemoryId)];

        // The fixed prompt, the anchor and two neighbours fit 8192 bytes; five neighbours therefore need three packs, and the two packs that hold
        // two neighbours each earn a pair for every member: seven units, in work order.
        Assert.Equal(7L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_work_items WHERE KindCode = 3 AND DispositionCode = 1;"));

        long fixedBytes = LongRestPromptLayout.FixedBytes(LongRestProviderPayloadKind.Adjudication);

        ImmutableArray<string>.Builder neighbors = ImmutableArray.CreateBuilder<string>();

        List<int> shapes = [];

        LongRestSweepWork? current = first;

        for (int index = 0; index < 7; index++)
        {
            Assert.NotNull(current);

            Assert.Equal(anchor, current.Inputs[0].Target);

            Assert.True(fixedBytes + current.Inputs.Sum(LongRestPromptLayout.AdjudicationMemoryBytes) <= 8192);

            Assert.True(LongRestProviderPayload.Adjudication(current.Identity, current.Inputs, 8192).IsSuccess, "A frozen pack must build its payload under the recipe budget.");

            if (index < 3) neighbors.AddRange(current.Inputs.Skip(1).Select(input => input.Target.MemoryId));

            shapes.Add(current.Inputs.Length);

            LongRestAdjudication accepted = await fixture.AcceptAsync(sweep, current, LongRestAdjudicationKind.NoChange);

            sweep = LongRestSweepHarness.Require(await fixture.Store.CommitDecisionAsync(sweep, accepted, fixture.Capability, CancellationToken.None));

            current = index < 6 ? LongRestSweepHarness.Require(await fixture.Store.ReadNextWorkAsync(sweep, fixture.Capability, CancellationToken.None)) : null;
        }

        // Three clique packs of three, three and two members, then the four pairs of the first two: the packs keep the nearest-first order, no
        // neighbour is in two clique packs, and every one of the five is packed and none counted as skipped for its size.
        Assert.Equal([3, 3, 2, 2, 2, 2, 2], shapes);

        Assert.Equal(5, ranked.Length);

        Assert.Equal(ranked, neighbors.ToArray());

        Assert.Equal(0, metrics.Sum(SkippedMetric, "reason", "oversize") + metrics.Sum(SkippedMetric, "reason", "pack_limit") + metrics.Sum(SkippedMetric, "reason", "incohesive"));
    }

    [Fact]
    public async Task A_neighbor_that_cannot_fit_beside_its_anchor_is_left_out_and_counted_by_reason_alone()
    {
        await using Fixture fixture = await Fixture.CreateAsync(protect: false, sourceCount: 6, inputBudget: 8192,
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

    // What a read counted is published only once the transaction that made it has committed: a read that fails after packing rolls its
    // work back, so it must not have counted the neighbours it left out, and the read that then succeeds counts them exactly once.
    [Fact]
    public async Task A_read_that_fails_after_packing_counts_nothing_and_the_read_that_succeeds_counts_once()
    {
        await using Fixture fixture = await Fixture.CreateAsync(protect: false, sourceCount: 6, inputBudget: 8192,
            text: index => Padded(index, index == 3 ? 4600 : 1500));

        using AsyncFlowMetricCapture metrics = new(SkippedMetric);

        LongRestSweep sweep = await fixture.BeginAsync();

        await fixture.H.ExecuteAsync("CREATE TEMP TRIGGER long_rest_fixture_work_refusal BEFORE INSERT ON long_rest_work_items BEGIN SELECT RAISE(ABORT, 'LongRest.FixtureRefusal'); END;");

        Assert.True((await fixture.Store.ReadNextWorkAsync(sweep, fixture.Capability, CancellationToken.None)).IsFailure);

        Assert.Equal(0L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_work_items WHERE KindCode = 3;"));

        Assert.Empty(metrics.Measurements);

        await fixture.H.ExecuteAsync("DROP TRIGGER long_rest_fixture_work_refusal;");

        Assert.NotNull(LongRestSweepHarness.Require(await fixture.Store.ReadNextWorkAsync(sweep, fixture.Capability, CancellationToken.None)));

        Assert.Equal(1, metrics.Sum(SkippedMetric, "reason", "oversize"));

        Assert.Single(metrics.Measurements);
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
        // Eight memories that name one another among sixteen that each have a vector of their own.
        await using Fixture fixture = await Fixture.CreateAsync(protect: false, sourceCount: 24, related: 8);

        using AsyncFlowMetricCapture metrics = new(SkippedMetric);

        LongRestSweep sweep = await fixture.BeginAsync();

        LongRestSweepWork first = LongRestSweepHarness.Require(await fixture.Store.ReadNextWorkAsync(sweep, fixture.Capability, CancellationToken.None))!;

        LongRestTarget anchor = await fixture.H.TargetAsync("10000000-0000-0000-0000-000000000001");

        LongRestCandidateGroup[] groups = LongRestSweepHarness.Require(await fixture.H.Store.DiscoverAsync(fixture.Generation.GenerationId, anchor, fixture.Capability, CancellationToken.None));

        // Discovery nominated the seven that name the anchor, and the anchor's floor travels with them.
        LongRestCandidateGroup group = Assert.Single(groups, static candidate => candidate.Evidence.Any(static item => item.Lane == LongRestDiscoveryLane.Vector));

        Assert.All(groups, candidate => Assert.InRange(candidate.PairingFloorPermille, 500, 1000));

        // Discovery only nominates: the group holds the seven and whatever else the lexical and hierarchy lanes named. The pack is the nominees the
        // gate paired with the anchor, nearest first, as one unit; the anchor's floor is recorded on it; and each of the seven is then paired with the
        // anchor in a unit of its own.
        Assert.Equal(Enumerable.Range(1, 8).Select(static place => "10000000-0000-0000-0000-" + place.ToString("D12", System.Globalization.CultureInfo.InvariantCulture)),
            first.Inputs.Select(static input => input.Target.MemoryId));

        Assert.All(first.Inputs, input => Assert.Contains(input.Target, group.Targets));

        Assert.Equal(group.PairingFloorPermille, first.Identity.PairingFloorPermille);

        Assert.Equal(8L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_work_items WHERE KindCode = 3 AND DispositionCode = 1;"));

        Assert.Equal(Enumerable.Repeat((long)group.PairingFloorPermille, 8), await FloorsAsync(fixture));

        Assert.Equal(0, metrics.Sum(SkippedMetric, "reason", "oversize") + metrics.Sum(SkippedMetric, "reason", "pack_limit") + metrics.Sum(SkippedMetric, "reason", "incohesive"));
    }

    private static async Task<List<long>> FloorsAsync(Fixture fixture)
    {
        List<long> floors = [];

        await using Microsoft.Data.Sqlite.SqliteCommand command = fixture.H.Connection.CreateCommand();

        command.CommandText = "SELECT PairingFloorPermille FROM long_rest_work_items WHERE KindCode = 3 ORDER BY ItemId;";

        await using Microsoft.Data.Sqlite.SqliteDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync()) floors.Add(reader.GetInt64(0));

        return floors;
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
