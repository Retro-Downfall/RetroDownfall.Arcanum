using System.Diagnostics;

using Xunit.Abstractions;

namespace RetroDownfall.Arcanum.Tests.LongRest;

/// <summary>
/// The Long Rest's verification budget at a thousand and at three thousand memories. Opt in with <c>ARCANUM_LONG_REST_SCALE_PROBE=1</c>;
/// building a corpus this size takes minutes, so an ordinary run reports these as skipped.
/// </summary>
/// <remarks>
/// The probe asserts counts and reports time. A wall-clock bound would fail on a busy machine and would pass a quadratic algorithm that is
/// still small today; a count of what was verified is the same on every machine and moves the moment the algorithm does. The time is
/// written to the test output as a diagnostic, for the person comparing one build with another.
/// </remarks>
[Collection("Grimoire")]
[Trait("Category", "LongRestScaleProbe")]
public sealed class LongRestScaleProbe(ITestOutputHelper output)
{
    private const string OptIn = "ARCANUM_LONG_REST_SCALE_PROBE";

    private const int Partitions = 3;

    /// <summary>
    /// How many corpus-sized passes of full leaf reads one unit of an open sweep may make. A unit that finds nothing to pay for reads about
    /// eight corpora today: one scan of every leaf for the anchor, and the reads the hierarchy lane makes of every summary's subtree. Admitting
    /// the generation is a step, so it reads a window of 32 leaves and no corpus pass. The bound is a dozen, so it holds from a thousand
    /// memories to three thousand and fails the day a unit re-verifies per candidate, fold or call, which would make it a multiple of the
    /// corpus itself rather than of its depth.
    /// </summary>
    private const int UnitCorpusPasses = 12;

    private static void RequireOptIn() => Skip.IfNot(
        string.Equals(System.Environment.GetEnvironmentVariable(OptIn), "1", StringComparison.Ordinal),
        $"Set {OptIn}=1 to run the Long Rest scale probe.");

    [SkippableTheory]
    [InlineData(1000)]
    [InlineData(3000)]
    public async Task One_unit_of_an_open_sweep_and_the_idle_turn_stay_within_their_counted_budget(int corpus)
    {
        RequireOptIn();

        Skip.IfNot(RetroDownfall.Arcanum.Tests.Fixtures.GrimoireFixture.SqlCipherAvailable, RetroDownfall.Arcanum.Tests.Fixtures.GrimoireFixture.SqlCipherUnavailableReason);

        Stopwatch clock = Stopwatch.StartNew();

        await using LongRestScaleScenario scenario = await LongRestScaleScenario.CreateAsync(corpus, Partitions, LongRestScaleCompletion.Open);

        output.WriteLine($"[{corpus}] corpus built in {clock.Elapsed.TotalSeconds:F1}s");

        clock.Restart();

        ScanUnit unit = await scenario.RunScanUnitAsync();

        output.WriteLine($"[{corpus}] one unit: {unit.FullLeafReads} full leaf reads, {unit.CorpusPasses} corpus passes, {unit.AntiJoinPasses} anti-join passes in {clock.Elapsed.TotalSeconds:F1}s");

        // A unit is linear in the corpus. The day it is quadratic - a unit that re-verifies per candidate, per fold or per call - this is the
        // number that moves.
        Assert.InRange(unit.FullLeafReads, 0, (long)UnitCorpusPasses * corpus);

        Assert.Equal(0, unit.CorpusPasses);

        Assert.Equal(0, unit.MetadataLeafReads);

        Assert.Equal(1, unit.WriterTransactions);

        await scenario.MarkCompleteAsync();

        clock.Restart();

        IdleTurn turn = await scenario.RunIdleTurnAsync();

        output.WriteLine($"[{corpus}] idle turn: {turn.StatementCount} statements, {turn.CorpusPasses} corpus pass, {turn.MetadataLeafReads} metadata leaf reads in {clock.Elapsed.TotalSeconds:F1}s");

        Assert.Equal(LongRestVerificationBudgetTests.CorpusPassesPerIdleCleanLookup, turn.CorpusPasses);

        Assert.Equal(LongRestVerificationBudgetTests.CorpusPassesPerIdleCleanLookup * corpus, turn.MetadataLeafReads);

        Assert.Equal(0, turn.FullLeafReads);

        Assert.Equal(Partitions + 1, turn.PartitionPasses);

        Assert.Equal(2 * (1 + Partitions), turn.ReadTransactions);

        Assert.Equal(0, turn.WriterTransactions);

        Assert.Equal(0, turn.ImmediateTransactions);

        // The statements beyond the corpus pass do not depend on the corpus: the same shapes run the same number of times as over the small corpus.
        await using LongRestScaleScenario baseline = await LongRestScaleScenario.CreateAsync(30, Partitions, LongRestScaleCompletion.Marked);

        IdleTurn small = await baseline.RunIdleTurnAsync();

        int pages = (corpus / 128) - (30 / 128);

        foreach (string text in small.Statements.Keys.Union(turn.Statements.Keys, StringComparer.Ordinal))
        {
            int extra = turn.Statements.GetValueOrDefault(text) - small.Statements.GetValueOrDefault(text);

            Assert.True(extra == 0 || extra == corpus - 30 || extra == pages, $"A statement ran {extra} more times over {corpus} memories than over 30.");
        }
    }
}
