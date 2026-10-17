using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed partial class LongRestSweepStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exact_partition_replay_ignores_unrelated_partition_debt_without_writes(bool protect)
    {
        await using Fixture fixture = await Fixture.CreateAsync(protect, duplicates: true);

        LongRestSweep sweep = await fixture.BeginAsync();

        LongRestSweepWork work = LongRestSweepHarness.Require(await fixture.Store.ReadNextWorkAsync(sweep, fixture.Capability, CancellationToken.None))!;

        LongRestSweep committed = LongRestSweepHarness.Require(await fixture.Store.CommitExactDuplicatesAsync(sweep, work.Identity, fixture.Capability, CancellationToken.None));

        await DrainCompletedCorpusAsync(fixture, committed);

        Guid other = Guid.NewGuid();

        await fixture.H.SeedAsync(other.ToString("D"), "unrelated protected observation");

        await fixture.H.ProtectAsync(other, GenerationProvenance.CreateExact([Guid.NewGuid()]));

        long changes = (long)(await fixture.H.ScalarAsync("SELECT total_changes();"))!;

        var found = await fixture.Store.FindCompletedReplayAsync(fixture.Generation.Fence.CanonicalScope,
            fixture.Generation.Scope, fixture.Generation.Recipe, CancellationToken.None);

        Assert.True(found.IsSuccess);

        Assert.Equal(fixture.Generation.OperationId, found.Value);

        Assert.Null(LongRestSweepHarness.Require(await fixture.Store.FindCompletedReplayAsync(fixture.Generation.Fence.CanonicalScope,
            fixture.Generation.Recipe, CancellationToken.None)));

        Assert.Equal(changes, await fixture.H.ScalarAsync("SELECT total_changes();"));
    }

    [Theory]
    [InlineData(false, "unchanged")]
    [InlineData(true, "unchanged")]
    [InlineData(false, "inserted")]
    [InlineData(true, "inserted")]
    [InlineData(false, "other-partition")]
    [InlineData(true, "other-partition")]
    [InlineData(false, "new-owner")]
    [InlineData(true, "new-owner")]
    [InlineData(false, "recipe")]
    [InlineData(true, "recipe")]
    [InlineData(false, "manifest")]
    [InlineData(true, "manifest")]
    public async Task Completed_lookup_is_read_only_and_requires_the_complete_current_scope_corpus(bool protect, string change)
    {
        await using Fixture fixture = await Fixture.CreateAsync(protect, duplicates: true, omittedOwner: change == "new-owner");

        LongRestSweep sweep = await fixture.BeginAsync();

        LongRestSweepWork work = LongRestSweepHarness.Require(await fixture.Store.ReadNextWorkAsync(sweep, fixture.Capability, CancellationToken.None))!;

        LongRestSweep committed = LongRestSweepHarness.Require(await fixture.Store.CommitExactDuplicatesAsync(sweep, work.Identity, fixture.Capability, CancellationToken.None));

        await DrainCompletedCorpusAsync(fixture, committed);

        Assert.Equal(6L, await fixture.H.ScalarAsync("SELECT PhaseCode FROM long_rest_sweeps;"));

        if (change is "inserted" or "other-partition")
        {
            Guid memory = Guid.NewGuid();

            await fixture.H.SeedAsync(memory.ToString("D"), "new eligible observation");

            if (change == "other-partition" || protect)
            {
                await fixture.H.ProtectAsync(memory, change == "other-partition" ? GenerationProvenance.CreateExact([Guid.NewGuid()]) : work.Inputs[0].Provenance);
            }
        }
        else if (change == "new-owner")
        {
            using var authorization = CovenantSqliteConnectionInitializer.Instance.Authorize(fixture.H.Connection, CovenantSqliteAuthorizationKind.SessionBindingWrite);

            await fixture.H.ExecuteAsync("INSERT INTO session_campaign_bindings (SessionId, BindingKindCode, CampaignId, BoundAtUtc) SELECT upper(SessionId), 1, NULL, '2026-10-10T00:00:00.0000000Z' FROM saga_memories WHERE Id = '10000000-0000-0000-0000-000000000099';");
        }
        else if (change == "manifest")
        {
            await fixture.H.ExecuteAsync("UPDATE long_rest_work_items SET ReceiptId = NULL WHERE KindCode = 3 AND DispositionCode = 2;");
        }

        LongRestRecipeIdentity recipe = change == "recipe" ? fixture.Generation.Recipe with { SummaryModel = "different-model" } : fixture.Generation.Recipe;

        long changes = (long)(await fixture.H.ScalarAsync("SELECT total_changes();"))!;

        Guid? found = LongRestSweepHarness.Require(await fixture.Store.FindCompletedReplayAsync(fixture.Generation.Fence.CanonicalScope, recipe, CancellationToken.None));

        Assert.Equal(change == "unchanged" ? fixture.Generation.OperationId : (Guid?)null, found);

        Assert.Equal(changes, await fixture.H.ScalarAsync("SELECT total_changes();"));
    }

    private static async Task DrainCompletedCorpusAsync(Fixture fixture, LongRestSweep sweep)
    {
        for (int page = 0; page < 8 && sweep.Phase != LongRestSweepPhase.Completed; page++)
        {
            Assert.Null(LongRestSweepHarness.Require(await fixture.Store.ReadNextWorkAsync(sweep, fixture.Capability, CancellationToken.None)));

            sweep = await fixture.BeginAsync();
        }

        Assert.Equal(LongRestSweepPhase.Completed, sweep.Phase);
    }
}
