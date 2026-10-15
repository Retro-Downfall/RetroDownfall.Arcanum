using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Tests.Fixtures;

using Xunit.Abstractions;

using Cost = RetroDownfall.Arcanum.Tests.LongRest.LongRestMutationMatrixTests.Cost;
using Scenario = RetroDownfall.Arcanum.Tests.LongRest.LongRestMutationMatrixTests.Scenario;

namespace RetroDownfall.Arcanum.Tests.LongRest;

/// <summary>
/// What Seams-and-Steps costs, as counts. A step re-proves a bounded amount whatever the corpus holds and a seam re-proves the corpus once; a
/// regression to a pass per step is a number here before it is a slow installation. Counts only, never time.
/// </summary>
[Collection("Grimoire")]
public sealed class LongRestStepSeamCounterTests(ITestOutputHelper output)
{
    private const int Leaves = 40;

    private static string Describe(Cost cost) => $"corpus {cost.CorpusPasses}, windows {cost.ScrubWindows}, full {cost.FullLeafReads}, metadata {cost.MetadataLeafReads}, anti-join {cost.AntiJoinPasses}";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_step_makes_no_corpus_pass_and_reads_only_its_window_and_its_inputs(bool isProtected)
    {
        await using Scenario s = await Scenario.CreateAsync(isProtected, Leaves);

        int window = (await s.WindowAsync()).Count;

        Assert.InRange(window, 1, LongRestDiscoveryStore.ScrubWindowLeaves);

        Cost ensure = await Scenario.MeasureAsync(() => s.EnsureAsync(LongRestVerificationDepth.Step, s.Inputs));

        output.WriteLine("ensure step: " + Describe(ensure));

        Assert.Equal(0, ensure.CorpusPasses);

        Assert.Equal(1, ensure.ScrubWindows);

        Assert.Equal(window + s.Inputs.Length, ensure.FullLeafReads);

        Assert.Equal(0, ensure.MetadataLeafReads);

        Cost attempt = await Scenario.MeasureAsync(() => s.AttemptStepRefusedAsync());

        output.WriteLine("attempt step: " + Describe(attempt));

        Assert.Equal(0, attempt.CorpusPasses);

        Assert.Equal(1, attempt.ScrubWindows);

        Assert.Equal(window + s.Inputs.Length, attempt.MetadataLeafReads);

        Assert.Equal(0, attempt.FullLeafReads);

        // The unit's pending read is a step too: its inputs in full, the window, and no pass over the corpus.
        Cost next = await Scenario.MeasureAsync(async () => LongRestSweepHarness.Require(await s.Sweeps.ReadNextWorkAsync(await s.CurrentSweepAsync(), s.Capability, CancellationToken.None)));

        output.WriteLine("pending read: " + Describe(next));

        Assert.Equal(0, next.CorpusPasses);

        Assert.Equal(1, next.ScrubWindows);

        Assert.Equal(window + s.Inputs.Length, next.FullLeafReads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_seam_makes_exactly_one_corpus_pass(bool isProtected)
    {
        await using Scenario s = await Scenario.CreateAsync(isProtected, Leaves);

        Cost ensure = await Scenario.MeasureAsync(() => s.EnsureAsync(LongRestVerificationDepth.Seam, null));

        Assert.Equal(1, ensure.CorpusPasses);

        Assert.Equal(0, ensure.ScrubWindows);

        Assert.Equal(Leaves, ensure.FullLeafReads);

        Cost attempt = await Scenario.MeasureAsync(() => s.AttemptSeamAsync());

        Assert.Equal(1, attempt.CorpusPasses);

        Assert.Equal(0, attempt.ScrubWindows);

        Assert.Equal(Leaves + s.Inputs.Length, attempt.MetadataLeafReads);

        // Resuming the retained generation is a seam: a clean one re-proves each leaf in full, a protected one by metadata.
        Cost resume = await Scenario.MeasureAsync(async () => LongRestSweepHarness.Require(await s.F.H.Store.StartOrResumeAsync(
            s.F.Operation, s.Generation.Scope, s.Generation.Fence, s.Generation.Recipe, CancellationToken.None)));

        Assert.Equal(1, resume.CorpusPasses);

        Assert.Equal(isProtected ? 0 : Leaves, resume.FullLeafReads);

        Assert.Equal(isProtected ? Leaves : 0, resume.MetadataLeafReads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publish_is_the_only_unit_of_the_build_that_makes_a_corpus_pass(bool isProtected)
    {
        await using Scenario s = await Scenario.CreateAsync(isProtected, Leaves);

        foreach ((LongRestSweepPhase phase, Cost cost) in s.BuildUnits)
        {
            output.WriteLine($"{phase}: corpus {cost.CorpusPasses}, windows {cost.ScrubWindows}, full {cost.FullLeafReads}, metadata {cost.MetadataLeafReads}, anti-join {cost.AntiJoinPasses}");
        }

        // The unit that publishes is a seam. So is the first unit of a protected run, which re-proves the corpus in full under the capability
        // it has just been handed, before anything is sent; at that point the corpus is empty, so it costs nothing to read.
        (LongRestSweepPhase Phase, Cost Cost)[] passes = [.. s.BuildUnits.Where(unit => unit.Cost.CorpusPasses > 0)];

        Assert.Equal(isProtected ? 2 : 1, passes.Length);

        Assert.All(passes, unit => Assert.Equal(1, unit.Cost.CorpusPasses));

        Assert.Equal(LongRestSweepPhase.Discover, passes[^1].Phase);

        if (isProtected)
        {
            Assert.Equal(0, passes[0].Cost.FullLeafReads);

            Assert.Same(s.BuildUnits[0].Cost, passes[0].Cost);
        }

        Assert.All(s.BuildUnits.Where(unit => unit.Cost.CorpusPasses == 0), unit => Assert.True(unit.Cost.ScrubWindows >= 1, "Every unit of the build that is not a seam is a step with its window."));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Completion_makes_one_corpus_pass_beside_the_step_that_reads_toward_it(bool isProtected)
    {
        await using Scenario s = await Scenario.CreateAsync(isProtected, Leaves, queueUnit: false);

        await s.AdvanceDiscoveryToTheEndAsync();

        LongRestSweep sweep = await s.CurrentSweepAsync();

        int window = (await s.WindowAsync()).Count;

        Cost completion = await Scenario.MeasureAsync(async () => LongRestSweepHarness.Require(await s.Sweeps.ReadNextWorkAsync(sweep, s.Capability, CancellationToken.None)));

        output.WriteLine("completion: " + Describe(completion));

        Assert.Equal(1, completion.CorpusPasses);

        Assert.Equal(1, completion.ScrubWindows);

        Assert.Equal(Leaves + window, completion.FullLeafReads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_unit_that_scans_the_corpus_for_an_anchor_reads_a_bounded_multiple_of_it(bool isProtected)
    {
        await using Scenario s = await Scenario.CreateAsync(isProtected, Leaves);

        output.WriteLine($"first read: corpus {s.FirstRead.CorpusPasses}, windows {s.FirstRead.ScrubWindows}, full {s.FirstRead.FullLeafReads}, anti-join {s.FirstRead.AntiJoinPasses}");

        Assert.Equal(0, s.FirstRead.CorpusPasses);

        Assert.Equal(1, s.FirstRead.ScrubWindows);

        // One scan of every leaf for the anchor, the hierarchy lane's read of every summary's subtree, one window and the pack's inputs: a
        // small multiple of the corpus fixed by the height of its tree, never a pass for each gate the unit passes through.
        Assert.InRange(s.FirstRead.FullLeafReads, Leaves, 8L * Leaves);
    }
}
