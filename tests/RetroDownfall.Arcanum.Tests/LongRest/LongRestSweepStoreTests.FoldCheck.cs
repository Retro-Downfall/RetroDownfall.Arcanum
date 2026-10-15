using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

// The Fold Check is the last gate before anything is suppressed: whatever the discovery, the packing and the model said, a unit is applied only
// if every pair of it is byte-identical or clears the floor the unit was queued under, and an equivalence does not join different numbers.
// These tests hand the store an accepted adjudication that the model path itself could never have produced, so only the check can refuse it.
public sealed partial class LongRestSweepStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_unit_whose_numbers_disagree_is_not_folded_whatever_was_accepted(bool protect)
    {
        await using Fixture fixture = await Fixture.CreateAsync(protect, text: index => $"The standup is at {8 + index}:00 on weekdays.");

        LongRestSweep sweep = await fixture.BeginAsync();

        LongRestSweepWork work = LongRestSweepHarness.Require(await fixture.Store.ReadNextWorkAsync(sweep, fixture.Capability, CancellationToken.None))!;

        LongRestAdjudication accepted = await fixture.AcceptAsync(sweep, work);

        Assert.True((await fixture.Store.CommitDecisionAsync(sweep, accepted, fixture.Capability, CancellationToken.None)).IsFailure);

        Assert.Equal(0L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_receipts;"));

        Assert.Equal(0L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_suppressions;"));
    }

    // Numbers are the concern of an application only: the same unit decided NoChange completes its item and folds nothing.
    [Fact]
    public async Task Numbers_that_disagree_do_not_stop_a_unit_decided_NoChange()
    {
        await using Fixture fixture = await Fixture.CreateAsync(false, text: index => $"The standup is at {8 + index}:00 on weekdays.");

        LongRestSweep sweep = await fixture.BeginAsync();

        LongRestSweepWork work = LongRestSweepHarness.Require(await fixture.Store.ReadNextWorkAsync(sweep, null, CancellationToken.None))!;

        LongRestAdjudication none = await fixture.AcceptAsync(sweep, work, LongRestAdjudicationKind.NoChange);

        _ = LongRestSweepHarness.Require(await fixture.Store.CommitDecisionAsync(sweep, none, null, CancellationToken.None));

        Assert.Equal(0L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_receipts;"));

        Assert.Equal(1L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_work_items WHERE KindCode = 3 AND DispositionCode = 2;"));
    }

    [Fact]
    public async Task A_vector_that_changed_after_queuing_refuses_publication()
    {
        await using Fixture fixture = await Fixture.CreateAsync(false);

        LongRestSweep sweep = await fixture.BeginAsync();

        LongRestSweepWork work = LongRestSweepHarness.Require(await fixture.Store.ReadNextWorkAsync(sweep, null, CancellationToken.None))!;

        LongRestAdjudication accepted = await fixture.AcceptAsync(sweep, work);

        // The unit was queued under the fallback floor with every memory paired at cosine one. One memory's vector is now orthogonal to the other's.
        float[] orthogonal = new float[fixture.H.Saga.Embedding().Length];

        orthogonal[^1] = 1;

        await fixture.H.SetEmbeddingAsync(work.Inputs[0].Snapshot.MemoryId, orthogonal);

        Assert.True((await fixture.Store.CommitDecisionAsync(sweep, accepted, null, CancellationToken.None)).IsFailure);

        Assert.Equal(0L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_receipts;"));

        Assert.Equal(0L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_suppressions;"));
    }

    [Fact]
    public async Task A_floor_changed_in_the_stored_work_refuses_publication()
    {
        await using Fixture fixture = await Fixture.CreateAsync(false);

        LongRestSweep sweep = await fixture.BeginAsync();

        LongRestSweepWork work = LongRestSweepHarness.Require(await fixture.Store.ReadNextWorkAsync(sweep, null, CancellationToken.None))!;

        LongRestAdjudication accepted = await fixture.AcceptAsync(sweep, work);

        // The floor is part of the identity the work digest names: a stored floor that is not the one the unit was queued under is another unit.
        await fixture.H.ExecuteAsync("UPDATE long_rest_work_items SET PairingFloorPermille = PairingFloorPermille - 1 WHERE KindCode = 3;");

        Assert.True((await fixture.Store.CommitDecisionAsync(sweep, accepted, null, CancellationToken.None)).IsFailure);

        Assert.Equal(0L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_receipts;"));
    }

    [Theory]
    [InlineData("UPDATE long_rest_work_items SET PairingFloorPermille = 0 WHERE KindCode = 3;")]
    [InlineData("UPDATE long_rest_work_items SET PairingFloorPermille = 499 WHERE KindCode = 3;")]
    [InlineData("UPDATE long_rest_work_items SET PairingFloorPermille = 1001 WHERE KindCode = 3;")]
    [InlineData("UPDATE long_rest_work_items SET PairingFloorPermille = 920 WHERE KindCode <> 3;")]
    public async Task The_schema_refuses_a_floor_that_does_not_belong_to_the_kind_of_work(string update)
    {
        await using Fixture fixture = await Fixture.CreateAsync(false);

        LongRestSweep sweep = await fixture.BeginAsync();

        _ = LongRestSweepHarness.Require(await fixture.Store.ReadNextWorkAsync(sweep, null, CancellationToken.None))!;

        // An adjudication unit has a floor from 500 to 1000 and every other kind of work none; the table says so.
        Assert.True((long)(await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_work_items WHERE KindCode = 3;"))! > 0);

        Assert.True((long)(await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_work_items WHERE KindCode <> 3;"))! > 0);

        _ = await Assert.ThrowsAnyAsync<Exception>(() => fixture.H.ExecuteAsync(update));
    }
}
