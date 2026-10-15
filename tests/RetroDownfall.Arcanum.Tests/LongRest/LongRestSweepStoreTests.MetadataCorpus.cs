using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

using Verdicts = RetroDownfall.Arcanum.Tests.LongRest.LongRestMetadataCorpusEquivalenceTests.Verdicts;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed partial class LongRestSweepStoreTests
{
    /// <summary>
    /// A seam over a generation the sweep has acted on. The leaves its decision consolidated no longer have the flags they were frozen with, and
    /// the receipt that explains the difference is proved in full, by the set read as by the per-leaf read, and proved once however many leaves
    /// the decision consolidated. A receipt that is the sweep's own, so the set-based attribution question admits it, and that does not prove,
    /// is refused by both: the full proof is not something the one statement stands in for.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_seam_proves_the_receipt_of_the_leaves_the_sweep_consolidated_once_and_refuses_a_receipt_that_does_not_prove(bool protect)
    {
        await using Fixture fixture = await Fixture.CreateAsync(protect, sourceCount: 24, related: 8);

        LongRestSweep sweep = await fixture.BeginAsync();

        LongRestSweepWork work = LongRestSweepHarness.Require(await fixture.Store.ReadNextWorkAsync(sweep, fixture.Capability, CancellationToken.None))!;

        LongRestAdjudication accepted = await fixture.AcceptAsync(sweep, work);

        _ = LongRestSweepHarness.Require(await fixture.Store.CommitDecisionAsync(sweep, accepted, fixture.Capability, CancellationToken.None));

        Assert.True(work.Inputs.Length >= 2, "One decision must have consolidated more than one leaf for the receipt to be shared.");

        Assert.Equal(1L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_receipts WHERE OutcomeCode = 1;"));

        // The leaves the decision touched are the ones whose live flags are no longer the frozen ones.
        Assert.True(work.Inputs.Length <= (long)(await fixture.H.ScalarAsync("""
            SELECT count(*) FROM long_rest_discovery_leaves
            WHERE json_extract(SnapshotJson, '$.isConsolidated') = 0 AND json_extract(SnapshotJson, '$.isTransformationOutput') = 0
              AND (VersionId IN (SELECT SourceVersionId FROM long_rest_suppressions) OR VersionId IN (SELECT SurvivorVersionId FROM long_rest_receipts));
            """))!);

        using (SqliteStatementTrace trace = new(fixture.H.Connection))
        {
            await using Microsoft.Data.Sqlite.SqliteTransaction transaction = fixture.H.Connection.BeginTransaction(deferred: true);

            await LongRestDiscoveryStore.VerifyFrozenMetadataCorpusAsync(fixture.H.Connection, transaction, fixture.Generation, CancellationToken.None);

            // The proof of a receipt reads its unit once; sixteen leaves of one decision are not sixteen proofs.
            Assert.Equal(1, trace.Statements.Count(static statement => statement.Contains("FROM long_rest_work_items unit", StringComparison.Ordinal)
                && statement.Contains("JOIN long_rest_sweeps sweep", StringComparison.Ordinal)));
        }

        Verdicts admitted = await LongRestMetadataCorpusEquivalenceTests.VerdictsAsync(fixture.H.Connection, fixture.Generation);

        Assert.Equal("admitted", admitted.PerLeaf);

        Assert.Equal("admitted", admitted.Set);

        await fixture.H.ExecuteAsync("DROP TRIGGER long_rest_receipts_guard_update;");

        await fixture.H.ExecuteAsync("UPDATE long_rest_receipts SET OutputHash = $other WHERE OutcomeCode = 1;", ("$other", new string('b', 64)));

        // The receipt is still on one of the generation's work items, so the question the statement asks is satisfied...
        await using (Microsoft.Data.Sqlite.SqliteTransaction transaction = fixture.H.Connection.BeginTransaction(deferred: true))
        {
            Assert.True(await LongRestDiscoveryStore.FrozenSourceMetadataMatchesAsync(fixture.H.Connection, transaction, fixture.Generation, CancellationToken.None));
        }

        // ...and both reads refuse it, because only the full proof of the receipt can.
        Verdicts refused = await LongRestMetadataCorpusEquivalenceTests.VerdictsAsync(fixture.H.Connection, fixture.Generation);

        Assert.Equal(nameof(InvalidDataException), refused.PerLeaf);

        Assert.Equal(nameof(InvalidDataException), refused.Set);
    }

    /// <summary>
    /// What a seam costs once the sweep has acted: the statements it runs are a constant, plus the statements of one proof for each receipt it
    /// has to prove, and nothing else. They do not depend on how many memories the corpus holds, nor on how many leaves a decision consolidated
    /// (a decision that consolidated sixteen leaves is one receipt, however many leaves it flagged), and a second decision costs the statements
    /// of the first decision's proof again and no more. Counted by SQLite's own trace, never by the stores.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_seam_after_decisions_costs_a_constant_and_one_proof_for_each_receipt_whatever_the_corpus_and_the_leaves_consolidated(bool protect)
    {
        await using Fixture small = await Fixture.CreateAsync(protect, sourceCount: 6, clusters: [3, 3]);

        await using Fixture large = await Fixture.CreateAsync(protect, sourceCount: 40, clusters: [16, 8]);

        // The set-based question and the statement over the leaves.
        int untouched = await SeamStatementsAsync(small);

        Assert.Equal(2, untouched);

        Assert.Equal(untouched, await SeamStatementsAsync(large));

        int consolidatedSmall = (await DecideNextAsync(small)).Inputs.Length;

        int consolidatedLarge = (await DecideNextAsync(large)).Inputs.Length;

        // The two decisions flagged a different number of leaves, so equal statements are not an accident of equal work.
        Assert.NotEqual(consolidatedSmall, consolidatedLarge);

        int oneReceiptSmall = await SeamStatementsAsync(small);

        int oneReceiptLarge = await SeamStatementsAsync(large);

        Assert.Equal(oneReceiptSmall, oneReceiptLarge);

        int proof = oneReceiptLarge - untouched;

        // The proof of one receipt: the receipt, its inputs, its suppressions, its unit and the unit's inputs, and a protected receipt's
        // evidence besides. A statement added to the proof changes the figure, which the design's cost of a seam states.
        Assert.Equal(protect ? 8 : 5, proof);

        _ = await DecideNextAsync(large);

        Assert.Equal(untouched + (2 * proof), await SeamStatementsAsync(large));
    }

    /// <summary>
    /// The turn that matters: a sweep that consolidated leaves has completed, and the idle Session turn that follows adopts its answer. That is a
    /// seam over a generation whose leaves are flagged, and its statements are the same at six memories as at fourteen, though the decision
    /// consolidated six leaves in one and fourteen in the other, because the leaves share a receipt and the receipt is proved once.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_idle_turn_after_a_sweep_that_consolidated_leaves_runs_the_same_statements_whatever_the_size_of_the_corpus(bool protect)
    {
        async Task<(int Statements, long Leaves, int Consolidated)> IdleTurnAsync(int sources)
        {
            await using Fixture fixture = await Fixture.CreateAsync(protect, sourceCount: sources, duplicates: true);

            LongRestSweep sweep = await fixture.BeginAsync();

            LongRestSweepWork work = LongRestSweepHarness.Require(await fixture.Store.ReadNextWorkAsync(sweep, fixture.Capability, CancellationToken.None))!;

            LongRestSweep committed = LongRestSweepHarness.Require(await fixture.Store.CommitExactDuplicatesAsync(sweep, work.Identity, fixture.Capability, CancellationToken.None));

            await DrainCompletedCorpusAsync(fixture, committed);

            await fixture.H.ExecuteAsync("UPDATE LongRunningOperations SET State = $completed;", ("$completed", (int)RetroDownfall.Arcanum.Core.Operations.LongRunningOperationState.Completed));

            Assert.Equal(1L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_receipts WHERE OutcomeCode = 1;"));

            using SqliteStatementTrace trace = new(fixture.H.Connection);

            using LongRestVerificationTally tally = LongRestVerificationCounters.Measure();

            Assert.Equal(fixture.Generation.OperationId, LongRestSweepHarness.Require(await fixture.Store.FindCompletedRequestReplayAsync(
                fixture.Generation.Fence.CanonicalScope, fixture.Generation.Scope, fixture.Generation.Recipe, CancellationToken.None)));

            Assert.Equal(0, trace.ImmediateTransactions);

            return (trace.Count, tally.MetadataLeafReads, work.Inputs.Length);
        }

        (int smallStatements, long smallLeaves, int smallConsolidated) = await IdleTurnAsync(6);

        (int largeStatements, long largeLeaves, int largeConsolidated) = await IdleTurnAsync(14);

        Assert.NotEqual(smallConsolidated, largeConsolidated);

        // Every leaf is still proved: the work that grew with the corpus is the work inside the one statement.
        Assert.Equal([6L, 14L], [smallLeaves, largeLeaves]);

        Assert.Equal(smallStatements, largeStatements);
    }

    private static async Task<LongRestSweepWork> DecideNextAsync(Fixture fixture)
    {
        LongRestSweep sweep = await fixture.BeginAsync();

        // A unit whose members an earlier decision consolidated is passed over without a request, and a pass over it returns no work, so the
        // next unit that does need a decision is found by reading on.
        LongRestSweepWork? work = null;

        for (int step = 0; step < 64 && work is null; step++)
        {
            work = LongRestSweepHarness.Require(await fixture.Store.ReadNextWorkAsync(sweep, fixture.Capability, CancellationToken.None));

            if (work is null) sweep = await fixture.BeginAsync();
        }

        Assert.NotNull(work);

        LongRestAdjudication accepted = await fixture.AcceptAsync(sweep, work);

        _ = LongRestSweepHarness.Require(await fixture.Store.CommitDecisionAsync(sweep, accepted, fixture.Capability, CancellationToken.None));

        return work;
    }

    /// <summary>The statements one seam over the generation runs: the set-based question and the read of every leaf.</summary>
    private static async Task<int> SeamStatementsAsync(Fixture fixture)
    {
        await using Microsoft.Data.Sqlite.SqliteTransaction transaction = fixture.H.Connection.BeginTransaction(deferred: true);

        using SqliteStatementTrace trace = new(fixture.H.Connection);

        await LongRestDiscoveryStore.VerifyFrozenMetadataAsync(fixture.H.Connection, transaction, fixture.Generation, LongRestVerificationDepth.Seam, CancellationToken.None);

        return trace.Count;
    }
}
