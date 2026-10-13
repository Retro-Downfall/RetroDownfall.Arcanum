using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed partial class LongRestSweepStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Protected_interrupted_send_requires_fresh_claim_attempt_and_disclosure_and_rolls_back_with_fence(bool injectFailure)
    {
        await using Fixture fixture = await Fixture.CreateAsync(true);

        LongRestSweep sweep = await fixture.BeginAsync();

        LongRestSweepWork work = LongRestSweepHarness.Require(await fixture.Store.ReadNextWorkAsync(sweep, fixture.Capability, CancellationToken.None))!;

        LongRestAttemptStore attempts = new(fixture.H.Saga.Context, TimeProvider.System);

        CovenantDigest payload = new(Enumerable.Repeat((byte)49, 32).ToArray());

        LongRestDispatchAttempt attempt = LongRestSweepHarness.Require(await attempts.PrepareAttemptAsync(work.Identity, LongRestProviderPayloadKind.Adjudication, payload, fixture.Generation.Recipe.SummaryDestinationDigest, CancellationToken.None));

        attempt = LongRestSweepHarness.Require(await attempts.RecordDisclosureAsync(attempt, payload, CancellationToken.None));

        attempt = LongRestSweepHarness.Require(await attempts.MarkSentAsync(attempt, CancellationToken.None));

        LongRestDiscoveryGeneration original = fixture.Generation;

        LongRestSweepFence fence = original.Fence with { Configuration = original.Fence.Configuration with { BootId = Guid.NewGuid(), Epoch = 23 } };

        string before = (string)(await fixture.H.ScalarAsync("SELECT FenceJson FROM long_rest_discovery_generations;"))!;

        if (injectFailure)
        {
            await fixture.H.ExecuteAsync("CREATE TEMP TRIGGER fail_boot_fence BEFORE UPDATE OF FenceJson ON long_rest_discovery_generations BEGIN SELECT RAISE(ABORT, 'fence failure'); END;");

            Assert.True((await fixture.H.Store.StartOrResumeAsync(original.OperationId, original.Scope, fence, original.Recipe, CancellationToken.None)).IsFailure);

            Assert.Equal(before, await fixture.H.ScalarAsync("SELECT FenceJson FROM long_rest_discovery_generations;"));

            Assert.Equal(3L, await fixture.H.ScalarAsync("SELECT StateCode FROM long_rest_provider_effects WHERE PayloadKindCode = 2;"));

            Assert.Equal(0L, await fixture.H.ScalarAsync("SELECT FailureCount FROM long_rest_provider_effects WHERE PayloadKindCode = 2;"));

            await fixture.H.ExecuteAsync("DROP TRIGGER fail_boot_fence;");
        }

        LongRestDiscoveryGeneration current = LongRestSweepHarness.Require(await fixture.H.Store.StartOrResumeAsync(original.OperationId, original.Scope, fence, original.Recipe, CancellationToken.None));

        Assert.Equal(DBNull.Value, await fixture.H.ScalarAsync("SELECT CompletionJson FROM long_rest_provider_effects WHERE PayloadKindCode = 2;"));

        Assert.Equal(0L, await fixture.H.ScalarAsync("SELECT PaidCount FROM long_rest_provider_effects WHERE PayloadKindCode = 2;"));

        Assert.Equal(1L, await fixture.H.ScalarAsync("SELECT FailureCount FROM long_rest_provider_effects WHERE PayloadKindCode = 2;"));

        Assert.True((await attempts.CompleteAttemptAsync(attempt, new(LongRestDispatchAttemptState.Accepted, new(1, 1, 2, 0, 0), 0.1m, null), CancellationToken.None)).IsFailure);

        LongRestSweep resumed = LongRestSweepHarness.Require(await fixture.Store.BeginOrResumeAsync(current.OperationId, current.Scope, fence.Configuration, CancellationToken.None));

        Assert.True((await fixture.Store.ReadNextWorkAsync(resumed, fixture.Capability, CancellationToken.None)).IsFailure);

        LongRestMaintenanceCapability capability = await fixture.CreateCapabilityAsync(current);

        LongRestSweepWork next = LongRestSweepHarness.Require(await fixture.Store.ReadNextWorkAsync(resumed, capability, CancellationToken.None))!;

        LongRestDispatchAttempt fresh = LongRestSweepHarness.Require(await attempts.PrepareAttemptAsync(next.Identity, LongRestProviderPayloadKind.Adjudication, payload, current.Recipe.SummaryDestinationDigest, CancellationToken.None));

        Assert.NotEqual(attempt.AttemptId, fresh.AttemptId);

        Assert.Equal(2UL, fresh.PhysicalOrdinal);

        Assert.Null(fresh.DisclosureReceiptDigest);

        Assert.True((await attempts.MarkSentAsync(fresh, CancellationToken.None)).IsFailure);

        CovenantDigest disclosure = new(Enumerable.Repeat((byte)50, 32).ToArray());

        fresh = LongRestSweepHarness.Require(await attempts.RecordDisclosureAsync(fresh, disclosure, CancellationToken.None));

        LongRestSweepHarness.Require(await attempts.MarkSentAsync(fresh, CancellationToken.None));

        Assert.Equal(disclosure, fresh.DisclosureReceiptDigest);

        Assert.Equal(0L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_adjudications;"));

        Assert.Equal(0L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_receipts;"));
    }

    [Fact]
    public async Task Protected_pending_work_without_borrowed_capability_refuses_without_effects()
    {
        await using Fixture fixture = await Fixture.CreateAsync(true);

        LongRestSweep sweep = await fixture.BeginAsync();

        long changes = (long)(await fixture.H.ScalarAsync("SELECT total_changes();"))!;

        Assert.True((await fixture.Store.ReadNextWorkAsync(sweep, null, CancellationToken.None)).IsFailure);

        Assert.Equal(changes, await fixture.H.ScalarAsync("SELECT total_changes();"));

        Assert.Equal(0L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_work_items WHERE KindCode = 3;"));
    }
}
