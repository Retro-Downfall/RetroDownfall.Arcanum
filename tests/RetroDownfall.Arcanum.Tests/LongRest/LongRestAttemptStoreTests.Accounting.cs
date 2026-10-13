using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed partial class LongRestAttemptStoreTests
{
    [Theory]
    [InlineData(LongRestDispatchAttemptState.Accepted, 0, null, 2, null)]
    [InlineData(LongRestDispatchAttemptState.Invalid, 0, 0.0, 2, 0.5)]
    [InlineData(LongRestDispatchAttemptState.Failed, 0, null, 1, 0.5)]
    [InlineData(LongRestDispatchAttemptState.Failed, 0, 0.0, 1, 0.5)]
    [InlineData(LongRestDispatchAttemptState.Failed, 1, null, 2, null)]
    [InlineData(LongRestDispatchAttemptState.Failed, 0, 0.2, 2, 0.7)]
    public async Task Paid_count_distinguishes_zero_evidence_transport_failure_from_paid_returns(LongRestDispatchAttemptState state, long usage, double? cost, long paidCount, double? totalCost)
    {
        await using LongRestSweepHarness h = await FixtureAsync();

        LongRestWorkIdentity work = await WorkAsync(h);

        LongRestAttemptStore store = new(h.Saga.Context, TimeProvider.System);

        LongRestDispatchAttempt first = LongRestSweepHarness.Require(await store.PrepareAttemptAsync(work, LongRestProviderPayloadKind.Summary, Payload, Destination, CancellationToken.None));

        LongRestDispatchAttempt firstSent = LongRestSweepHarness.Require(await store.MarkSentAsync(first, CancellationToken.None));

        LongRestSweepHarness.Require(await store.CompleteAttemptAsync(firstSent, new(LongRestDispatchAttemptState.Accepted, new(1, 1, 2, 0, 0), 0.5m, null), CancellationToken.None));

        LongRestDispatchAttempt second = LongRestSweepHarness.Require(await store.PrepareAttemptAsync(work, LongRestProviderPayloadKind.Embedding, Payload, Destination, CancellationToken.None));

        LongRestDispatchAttempt secondSent = LongRestSweepHarness.Require(await store.MarkSentAsync(second, CancellationToken.None));

        LongRestDispatchCompletion completion = new(state, new(usage, 0, usage, 0, 0), cost is { } known ? (decimal)known : null, null);

        LongRestSweepHarness.Require(await store.CompleteAttemptAsync(secondSent, completion, CancellationToken.None));

        Assert.Equal(paidCount, await h.ScalarAsync("SELECT PaidCount FROM long_rest_provider_effects;"));

        object? storedCost = await h.ScalarAsync("SELECT CostUsd FROM long_rest_provider_effects;");

        Assert.Equal(totalCost is { } total ? ((decimal)total).ToString(System.Globalization.CultureInfo.InvariantCulture) : (object)DBNull.Value, storedCost);

        Assert.Equal(1 + usage, await h.ScalarAsync("SELECT InputTokens FROM long_rest_provider_effects;"));

        long changes = (long)(await h.ScalarAsync("SELECT total_changes();"))!;

        LongRestSweepHarness.Require(await store.CompleteAttemptAsync(secondSent, completion, CancellationToken.None));

        Assert.Equal(changes, (long)(await h.ScalarAsync("SELECT total_changes();"))!);
    }

    [Fact]
    public async Task Known_zero_cost_success_after_unpaid_failure_starts_cost_aggregate_at_zero()
    {
        await using LongRestSweepHarness h = await FixtureAsync();

        LongRestWorkIdentity work = await WorkAsync(h);

        LongRestAttemptStore store = new(h.Saga.Context, TimeProvider.System);

        LongRestDispatchAttempt first = LongRestSweepHarness.Require(await store.PrepareAttemptAsync(work, LongRestProviderPayloadKind.Summary, Payload, Destination, CancellationToken.None));

        LongRestDispatchAttempt firstSent = LongRestSweepHarness.Require(await store.MarkSentAsync(first, CancellationToken.None));

        LongRestSweepHarness.Require(await store.CompleteAttemptAsync(firstSent, new(LongRestDispatchAttemptState.Failed, new(0, 0, 0, 0, 0), null, "LongRest.TransportFailure"), CancellationToken.None));

        LongRestDispatchAttempt second = LongRestSweepHarness.Require(await store.PrepareAttemptAsync(work, LongRestProviderPayloadKind.Embedding, Payload, Destination, CancellationToken.None));

        LongRestDispatchAttempt secondSent = LongRestSweepHarness.Require(await store.MarkSentAsync(second, CancellationToken.None));

        LongRestSweepHarness.Require(await store.CompleteAttemptAsync(secondSent, new(LongRestDispatchAttemptState.Accepted, new(0, 0, 0, 0, 0), 0m, null), CancellationToken.None));

        Assert.Equal(1L, await h.ScalarAsync("SELECT PaidCount FROM long_rest_provider_effects;"));

        Assert.Equal("0", await h.ScalarAsync("SELECT CostUsd FROM long_rest_provider_effects;"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Positive_usage_in_any_counter_makes_failed_completion_paid(int counter)
    {
        await using LongRestSweepHarness h = await FixtureAsync();

        LongRestWorkIdentity work = await WorkAsync(h);

        LongRestAttemptStore store = new(h.Saga.Context, TimeProvider.System);

        LongRestDispatchAttempt attempt = LongRestSweepHarness.Require(await store.PrepareAttemptAsync(work, LongRestProviderPayloadKind.Summary, Payload, Destination, CancellationToken.None));

        LongRestDispatchAttempt sent = LongRestSweepHarness.Require(await store.MarkSentAsync(attempt, CancellationToken.None));

        long[] counters = new long[5];

        counters[counter] = 1;

        LongRestSweepHarness.Require(await store.CompleteAttemptAsync(sent, new(LongRestDispatchAttemptState.Failed, new(counters[0], counters[1], counters[2], counters[3], counters[4]), null, "LongRest.ProviderFailure"), CancellationToken.None));

        Assert.Equal(1L, await h.ScalarAsync("SELECT PaidCount FROM long_rest_provider_effects;"));

        Assert.Equal(1L, await h.ScalarAsync("SELECT FailureCount FROM long_rest_provider_effects;"));

        Assert.Equal(counters[0], await h.ScalarAsync("SELECT InputTokens FROM long_rest_provider_effects;"));

        Assert.Equal(counters[1], await h.ScalarAsync("SELECT OutputTokens FROM long_rest_provider_effects;"));

        Assert.Equal(counters[2], await h.ScalarAsync("SELECT TotalTokens FROM long_rest_provider_effects;"));

        Assert.Equal(counters[3], await h.ScalarAsync("SELECT CachedInputTokens FROM long_rest_provider_effects;"));

        Assert.Equal(counters[4], await h.ScalarAsync("SELECT ReasoningTokens FROM long_rest_provider_effects;"));
    }

    [Fact]
    public async Task Unknown_cost_remains_unknown_after_later_known_paid_returns()
    {
        await using LongRestSweepHarness h = await FixtureAsync();

        LongRestWorkIdentity work = await WorkAsync(h);

        LongRestAttemptStore store = new(h.Saga.Context, TimeProvider.System);

        LongRestDispatchAttempt first = LongRestSweepHarness.Require(await store.PrepareAttemptAsync(work, LongRestProviderPayloadKind.Summary, Payload, Destination, CancellationToken.None));

        LongRestDispatchAttempt firstSent = LongRestSweepHarness.Require(await store.MarkSentAsync(first, CancellationToken.None));

        LongRestSweepHarness.Require(await store.CompleteAttemptAsync(firstSent, new(LongRestDispatchAttemptState.Accepted, new(1, 0, 1, 0, 0), null, null), CancellationToken.None));

        LongRestDispatchAttempt second = LongRestSweepHarness.Require(await store.PrepareAttemptAsync(work, LongRestProviderPayloadKind.Embedding, Payload, Destination, CancellationToken.None));

        LongRestDispatchAttempt secondSent = LongRestSweepHarness.Require(await store.MarkSentAsync(second, CancellationToken.None));

        LongRestSweepHarness.Require(await store.CompleteAttemptAsync(secondSent, new(LongRestDispatchAttemptState.Accepted, new(1, 0, 1, 0, 0), 0.25m, null), CancellationToken.None));

        Assert.Equal(DBNull.Value, await h.ScalarAsync("SELECT CostUsd FROM long_rest_provider_effects;"));
    }
}
