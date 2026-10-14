using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

// Withdrawing a Prepared attempt that no send ever followed. Nothing reached the provider, so the row must not survive as something a same-boot
// readmission has to refuse over, and it must not charge the Unknown-Usage Cap.
public sealed partial class LongRestAttemptStoreTests
{
    [Fact]
    public async Task A_prepared_attempt_that_never_sent_is_withdrawn_to_no_attempt_at_all()
    {
        await using LongRestSweepHarness h = await FixtureAsync();

        LongRestWorkIdentity work = await WorkAsync(h);

        LongRestAttemptStore store = new(h.Saga.Context, TimeProvider.System);

        LongRestDispatchAttempt prepared = LongRestSweepHarness.Require(await store.PrepareAttemptAsync(work, LongRestProviderPayloadKind.Summary, Payload, Destination, CancellationToken.None));

        Assert.Equal(1L, await h.ScalarAsync("SELECT count(*) FROM long_rest_provider_effects WHERE StateCode = 1;"));

        Assert.True((await store.ReleaseUnsentAttemptAsync(prepared, CancellationToken.None)).IsSuccess);

        Assert.Equal(0L, await h.ScalarAsync("SELECT count(*) FROM long_rest_provider_effects;"));

        // The withdrawn attempt is gone, and the unit prepares exactly as if it had never been prepared.
        Assert.True((await store.ValidateAttemptAsync(prepared, CancellationToken.None)).IsFailure);

        Assert.True((await store.ReleaseUnsentAttemptAsync(prepared, CancellationToken.None)).IsFailure);

        LongRestDispatchAttempt again = LongRestSweepHarness.Require(await store.PrepareAttemptAsync(work, LongRestProviderPayloadKind.Summary, Payload, Destination, CancellationToken.None));

        Assert.Equal(1UL, again.PhysicalOrdinal);

        Assert.Equal(0L, again.Revision);

        Assert.NotEqual(prepared.AttemptId, again.AttemptId);
    }

    [Fact]
    public async Task A_second_attempt_withdrawn_before_it_sent_keeps_the_counters_of_the_first_and_charges_no_failure()
    {
        await using LongRestSweepHarness h = await FixtureAsync();

        LongRestWorkIdentity work = await WorkAsync(h);

        LongRestAttemptStore store = new(h.Saga.Context, TimeProvider.System);

        LongRestDispatchAttempt first = LongRestSweepHarness.Require(await store.PrepareAttemptAsync(work, LongRestProviderPayloadKind.Summary, Payload, Destination, CancellationToken.None));

        first = LongRestSweepHarness.Require(await store.MarkSentAsync(first, CancellationToken.None));

        first = LongRestSweepHarness.Require(await store.CompleteAttemptAsync(first, new(LongRestDispatchAttemptState.Accepted, new(10, 3, 13, 0, 0), 0.125m, null), CancellationToken.None));

        LongRestDispatchAttempt second = LongRestSweepHarness.Require(await store.PrepareAttemptAsync(work, LongRestProviderPayloadKind.Embedding, Payload, Destination, CancellationToken.None));

        Assert.Equal(2UL, second.PhysicalOrdinal);

        Assert.True((await store.ReleaseUnsentAttemptAsync(second, CancellationToken.None)).IsSuccess);

        // One row, as the unit always has: the second attempt is the failed shape of a send that never left the process.
        Assert.Equal(1L, await h.ScalarAsync("SELECT count(*) FROM long_rest_provider_effects;"));

        Assert.Equal((long)LongRestDispatchAttemptState.Failed, await h.ScalarAsync("SELECT StateCode FROM long_rest_provider_effects;"));

        Assert.Equal(ErrorCodes.LongRest.ProviderUnreachable, await h.ScalarAsync("SELECT FailureCode FROM long_rest_provider_effects;"));

        Assert.Equal(DBNull.Value, await h.ScalarAsync("SELECT CompletionJson FROM long_rest_provider_effects;"));

        Assert.Equal(1L, await h.ScalarAsync("SELECT SentCount FROM long_rest_provider_effects;"));

        Assert.Equal(1L, await h.ScalarAsync("SELECT PaidCount FROM long_rest_provider_effects;"));

        Assert.Equal(0L, await h.ScalarAsync("SELECT FailureCount FROM long_rest_provider_effects;"));

        Assert.Equal(10L, await h.ScalarAsync("SELECT InputTokens FROM long_rest_provider_effects;"));

        Assert.Equal(2L, await h.ScalarAsync("SELECT PhysicalOrdinal FROM long_rest_provider_effects;"));

        // Not a billing question: the cap is untouched and the unit may prepare again.
        LongRestDispatchAttempt third = LongRestSweepHarness.Require(await store.PrepareAttemptAsync(work, LongRestProviderPayloadKind.Embedding, Payload, Destination, CancellationToken.None));

        Assert.Equal(3UL, third.PhysicalOrdinal);

        third = LongRestSweepHarness.Require(await store.MarkSentAsync(third, CancellationToken.None));

        LongRestSweepHarness.Require(await store.CompleteAttemptAsync(third, new(LongRestDispatchAttemptState.Accepted, new(20, 4, 24, 0, 0), 0.25m, null), CancellationToken.None));

        Assert.Equal(2L, await h.ScalarAsync("SELECT SentCount FROM long_rest_provider_effects;"));

        Assert.Equal(30L, await h.ScalarAsync("SELECT InputTokens FROM long_rest_provider_effects;"));
    }

    [Fact]
    public async Task Only_the_exact_prepared_attempt_without_a_disclosure_can_be_withdrawn()
    {
        await using LongRestSweepHarness h = await FixtureAsync();

        LongRestWorkIdentity work = await WorkAsync(h);

        LongRestAttemptStore store = new(h.Saga.Context, TimeProvider.System);

        LongRestDispatchAttempt prepared = LongRestSweepHarness.Require(await store.PrepareAttemptAsync(work, LongRestProviderPayloadKind.Summary, Payload, Destination, CancellationToken.None));

        // A disclosed attempt carries a durable receipt that a send may follow: it is left to the reboot settlement.
        LongRestDispatchAttempt disclosed = LongRestSweepHarness.Require(await store.RecordDisclosureAsync(prepared, Payload, CancellationToken.None));

        Assert.True((await store.ReleaseUnsentAttemptAsync(disclosed, CancellationToken.None)).IsFailure);

        Assert.True((await store.ReleaseUnsentAttemptAsync(prepared, CancellationToken.None)).IsFailure);

        Assert.Equal((long)LongRestDispatchAttemptState.Disclosed, await h.ScalarAsync("SELECT StateCode FROM long_rest_provider_effects;"));

        // A sent attempt is never withdrawn, and neither is the attempt it replaced.
        LongRestDispatchAttempt next = LongRestSweepHarness.Require(await store.PrepareAttemptAsync(work, LongRestProviderPayloadKind.Summary, Payload, Destination, CancellationToken.None));

        LongRestDispatchAttempt sent = LongRestSweepHarness.Require(await store.MarkSentAsync(next, CancellationToken.None));

        Assert.True((await store.ReleaseUnsentAttemptAsync(sent, CancellationToken.None)).IsFailure);

        Assert.True((await store.ReleaseUnsentAttemptAsync(next, CancellationToken.None)).IsFailure);

        Assert.True((await store.ReleaseUnsentAttemptAsync(disclosed, CancellationToken.None)).IsFailure);

        Assert.Equal((long)LongRestDispatchAttemptState.Sent, await h.ScalarAsync("SELECT StateCode FROM long_rest_provider_effects;"));

        Assert.Equal(1L, await h.ScalarAsync("SELECT SentCount FROM long_rest_provider_effects;"));
    }
}
