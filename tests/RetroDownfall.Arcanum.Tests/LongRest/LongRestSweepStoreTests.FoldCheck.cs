using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
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

    // Two memories 0.95 alike in a space of their own: paired under the fallback floor (0.92) that a generation of two is queued under.
    private static float[] Alike(int index)
    {
        float[] vector = new float[LongRestClusters.Width];

        vector[0] = index == 1 ? 1f : 0.95f;

        if (index != 1)
        {
            vector[1] = (float)Math.Sqrt(1 - 0.95 * 0.95);
        }

        return vector;
    }

    // The memories recall may return: the projection the recall queries filter by, in id order.
    private static Task<object?> RecallableAsync(Fixture fixture) => fixture.H.ScalarAsync(
        $"SELECT group_concat(\"Id\", ',') FROM (SELECT memory.\"Id\" FROM \"saga_memories\" memory WHERE memory.\"RetiredAtUtc\" IS NULL AND {LongRestRecallSql.Eligible("memory", true)} ORDER BY memory.\"Id\");");

    // The geometric half of the Fold Check, at the mutation point. The unit below is a mixed one in every way the store can check but the one this
    // test is about: its digests are consistent with its recorded floor (the floor and the work digest are rewritten together, and the accepted
    // adjudication is made against that identity), its leaves are the frozen ones, and the model "accepted" an equivalence. Only the pair under the
    // recorded floor stands in the way. Unforged, the same unit is applied, so the refusal is the floor's and nothing else's.
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task A_unit_whose_pair_is_under_the_floor_recorded_for_it_is_not_folded_whatever_was_accepted(bool protect, bool forge)
    {
        await using Fixture fixture = await Fixture.CreateAsync(protect, vector: Alike);

        LongRestSweep sweep = await fixture.BeginAsync();

        LongRestSweepWork queued = LongRestSweepHarness.Require(await fixture.Store.ReadNextWorkAsync(sweep, fixture.Capability, CancellationToken.None))!;

        Assert.Equal(LongRestDiscoveryLimits.PairingFallbackPermille, queued.Identity.PairingFloorPermille);

        Assert.True(LongRestPairing.Paired(queued.Inputs[0], queued.Inputs[1], queued.Identity.PairingFloorPermille));

        object? recallableBefore = await RecallableAsync(fixture);

        Assert.NotNull(recallableBefore);

        LongRestWorkIdentity identity = queued.Identity;

        if (forge)
        {
            // A floor the 0.95 pair is under, recorded for the unit together with the digest that names it.
            identity = queued.Identity with { PairingFloorPermille = 990 };

            Assert.False(LongRestPairing.Paired(queued.Inputs[0], queued.Inputs[1], identity.PairingFloorPermille));

            await fixture.H.ExecuteAsync("UPDATE long_rest_work_items SET PairingFloorPermille = $floor, WorkDigest = $digest WHERE KindCode = 3 AND DispositionCode = 1;",
                ("$floor", identity.PairingFloorPermille), ("$digest", LongRestDiscoveryHash.Work(identity).Value.Bytes));
        }

        LongRestAdjudication accepted = await fixture.AcceptAsync(sweep, queued with { Identity = identity });

        Result<LongRestSweep> committed = await fixture.Store.CommitDecisionAsync(sweep, accepted, fixture.Capability, CancellationToken.None);

        if (!forge)
        {
            Assert.True(committed.IsSuccess);

            Assert.Equal(1L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_receipts;"));

            Assert.Equal(1L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_suppressions;"));

            Assert.NotEqual(recallableBefore, await RecallableAsync(fixture));

            return;
        }

        Assert.True(committed.IsFailure);

        Assert.Equal(0L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_receipts;"));

        Assert.Equal(0L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_suppressions;"));

        Assert.Equal(recallableBefore, await RecallableAsync(fixture));

        Assert.Equal(1L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_work_items WHERE KindCode = 3 AND DispositionCode = 1;"));
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
