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
        await using Fixture fixture = await Fixture.CreateAsync(protect, sourceCount: 24);

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
}
