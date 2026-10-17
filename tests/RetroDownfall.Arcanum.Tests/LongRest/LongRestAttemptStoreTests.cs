using System.Text.Json;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

[Collection("Grimoire")]
public sealed partial class LongRestAttemptStoreTests
{
    private static readonly CovenantDigest Payload = new(Enumerable.Repeat((byte)42, 32).ToArray());

    private static readonly CovenantDigest Destination = new(new byte[32]);

    [Theory]
    [InlineData(LongRestDispatchAttemptState.Accepted)]
    [InlineData(LongRestDispatchAttemptState.Invalid)]
    [InlineData(LongRestDispatchAttemptState.Failed)]
    public async Task Completion_is_exact_once_and_replacement_retains_aggregates(LongRestDispatchAttemptState state)
    {
        await using LongRestSweepHarness harness = await FixtureAsync();

        LongRestWorkIdentity work = await WorkAsync(harness);

        LongRestAttemptStore store = new(harness.Saga.Context, TimeProvider.System);

        LongRestDispatchAttempt prepared = LongRestSweepHarness.Require(await store.PrepareAttemptAsync(work, LongRestProviderPayloadKind.Summary, Payload, Destination, CancellationToken.None));

        Assert.Equal(1UL, prepared.PhysicalOrdinal);

        LongRestDispatchAttempt sent = LongRestSweepHarness.Require(await store.MarkSentAsync(prepared, CancellationToken.None));

        Assert.True((await store.MarkSentAsync(prepared, CancellationToken.None)).IsFailure);

        LongRestDispatchCompletion completion = new(state, new(10, 3, 13, 2, 1), 0.125m, state == LongRestDispatchAttemptState.Accepted ? null : "LongRest.InvalidOutput");

        LongRestDispatchAttempt completed = LongRestSweepHarness.Require(await store.CompleteAttemptAsync(sent, completion, CancellationToken.None));

        long changes = (long)(await harness.ScalarAsync("SELECT total_changes();"))!;

        Assert.Equal(completed, LongRestSweepHarness.Require(await store.CompleteAttemptAsync(sent, completion, CancellationToken.None)));

        Assert.Equal(completed, LongRestSweepHarness.Require(await store.CompleteAttemptAsync(completed, completion, CancellationToken.None)));

        Assert.Equal(changes, (long)(await harness.ScalarAsync("SELECT total_changes();"))!);

        Assert.True((await store.CompleteAttemptAsync(sent, completion with { CostUsd = 0.126m }, CancellationToken.None)).IsFailure);

        Assert.Equal(JsonSerializer.Serialize(completion, LongRestJsonContext.Default.LongRestDispatchCompletion), await harness.ScalarAsync("SELECT CompletionJson FROM long_rest_provider_effects;"));

        Assert.Equal(10L, await harness.ScalarAsync("SELECT InputTokens FROM long_rest_provider_effects;"));

        Assert.Equal(1L, await harness.ScalarAsync("SELECT PaidCount FROM long_rest_provider_effects;"));

        Assert.Equal(state == LongRestDispatchAttemptState.Invalid ? 1L : 0L, await harness.ScalarAsync("SELECT InvalidCount FROM long_rest_provider_effects;"));

        Assert.Equal(state == LongRestDispatchAttemptState.Failed ? 1L : 0L, await harness.ScalarAsync("SELECT FailureCount FROM long_rest_provider_effects;"));

        LongRestDispatchAttempt next = LongRestSweepHarness.Require(await store.PrepareAttemptAsync(work, LongRestProviderPayloadKind.Embedding, Payload, Destination, CancellationToken.None));

        Assert.Equal(2UL, next.PhysicalOrdinal);

        Assert.NotEqual(prepared.AttemptId, next.AttemptId);

        Assert.Equal(DBNull.Value, await harness.ScalarAsync("SELECT CompletionJson FROM long_rest_provider_effects;"));

        Assert.Equal(10L, await harness.ScalarAsync("SELECT InputTokens FROM long_rest_provider_effects;"));

        Assert.True((await store.ValidateAttemptAsync(completed, CancellationToken.None)).IsFailure);

        LongRestDispatchAttempt nextSent = LongRestSweepHarness.Require(await store.MarkSentAsync(next, CancellationToken.None));

        LongRestSweepHarness.Require(await store.CompleteAttemptAsync(nextSent, new(LongRestDispatchAttemptState.Accepted, new(20, 4, 24, 0, 0), 0.25m, null), CancellationToken.None));

        Assert.Equal(30L, await harness.ScalarAsync("SELECT InputTokens FROM long_rest_provider_effects;"));

        Assert.Equal(7L, await harness.ScalarAsync("SELECT OutputTokens FROM long_rest_provider_effects;"));

        Assert.Equal(37L, await harness.ScalarAsync("SELECT TotalTokens FROM long_rest_provider_effects;"));

        Assert.Equal("0.375", await harness.ScalarAsync("SELECT CostUsd FROM long_rest_provider_effects;"));
    }

    [Fact]
    public async Task Exact_attempt_facts_and_current_work_fence_are_required_before_send()
    {
        await using LongRestSweepHarness harness = await FixtureAsync();

        LongRestWorkIdentity work = await WorkAsync(harness);

        LongRestAttemptStore store = new(harness.Saga.Context, TimeProvider.System);

        LongRestDispatchAttempt attempt = LongRestSweepHarness.Require(await store.PrepareAttemptAsync(work, LongRestProviderPayloadKind.Summary, Payload, Destination, CancellationToken.None));

        foreach (LongRestDispatchAttempt changed in new[] { attempt with { AttemptId = Guid.NewGuid() }, attempt with { Revision = 1 }, attempt with { PhysicalOrdinal = 2 }, attempt with { PayloadDigest = Destination }, attempt with { DestinationDigest = Payload }, attempt with { State = LongRestDispatchAttemptState.Disclosed }, attempt with { DisclosureReceiptDigest = Payload } })
        {
            Assert.True((await store.ValidateAttemptAsync(changed, CancellationToken.None)).IsFailure);

            Assert.True((await store.MarkSentAsync(changed, CancellationToken.None)).IsFailure);
        }

        Assert.True((await store.CompleteAttemptAsync(attempt, new(LongRestDispatchAttemptState.Accepted, new(1, 1, 2, 0, 0), null, null), CancellationToken.None)).IsFailure);

        await harness.ExecuteAsync("UPDATE long_rest_work_items SET WorkDigest = $changed;", ("$changed", Payload.Bytes));

        Assert.True((await store.ValidateAttemptAsync(attempt, CancellationToken.None)).IsFailure);

        Assert.True((await store.MarkSentAsync(attempt, CancellationToken.None)).IsFailure);

        Assert.True((await store.PrepareAttemptAsync(work, LongRestProviderPayloadKind.Summary, Payload, Destination, CancellationToken.None)).IsFailure);

        Assert.Equal(0L, await harness.ScalarAsync("SELECT SentCount FROM long_rest_provider_effects;"));
    }

    [Fact]
    public async Task New_preparation_does_not_overwrite_an_uncompleted_physical_send()
    {
        await using LongRestSweepHarness harness = await FixtureAsync();

        LongRestWorkIdentity work = await WorkAsync(harness);

        LongRestAttemptStore store = new(harness.Saga.Context, TimeProvider.System);

        LongRestDispatchAttempt prepared = LongRestSweepHarness.Require(await store.PrepareAttemptAsync(work, LongRestProviderPayloadKind.Summary, Payload, Destination, CancellationToken.None));

        LongRestDispatchAttempt sent = LongRestSweepHarness.Require(await store.MarkSentAsync(prepared, CancellationToken.None));

        Assert.True((await store.PrepareAttemptAsync(work, LongRestProviderPayloadKind.Embedding, Payload, Destination, CancellationToken.None)).IsFailure);

        Assert.Equal(sent.AttemptId.ToString("D").ToUpperInvariant(), await harness.ScalarAsync("SELECT AttemptId FROM long_rest_provider_effects;"));
    }

    [Theory]
    [InlineData(-1, 1, 0, 0, 0)]
    [InlineData(1, -1, 0, 0, 0)]
    [InlineData(1, 1, -1, 0, 0)]
    [InlineData(1, 1, 2, -1, 0)]
    [InlineData(1, 1, 2, 0, -1)]
    public async Task Malformed_completion_does_not_consume_the_sent_attempt(long input, long output, long total, long cached, long reasoning)
    {
        await using LongRestSweepHarness harness = await FixtureAsync();

        LongRestWorkIdentity work = await WorkAsync(harness);

        LongRestAttemptStore store = new(harness.Saga.Context, TimeProvider.System);

        LongRestDispatchAttempt sent = LongRestSweepHarness.Require(await store.MarkSentAsync(LongRestSweepHarness.Require(await store.PrepareAttemptAsync(work, LongRestProviderPayloadKind.Summary, Payload, Destination, CancellationToken.None)), CancellationToken.None));

        Assert.True((await store.CompleteAttemptAsync(sent, new(LongRestDispatchAttemptState.Accepted, new(input, output, total, cached, reasoning), 0m, null), CancellationToken.None)).IsFailure);

        Assert.True((await store.ValidateAttemptAsync(sent, CancellationToken.None)).IsSuccess);

        Assert.Equal(0L, await harness.ScalarAsync("SELECT PaidCount FROM long_rest_provider_effects;"));

        Assert.Equal(DBNull.Value, await harness.ScalarAsync("SELECT CompletionJson FROM long_rest_provider_effects;"));
    }

    private static async Task<LongRestSweepHarness> FixtureAsync()
    {
        LongRestSweepHarness harness = await LongRestSweepHarness.CreateAsync();

        await harness.SeedAsync("a", "Alpha observation.");

        await harness.SeedAsync("b", "Beta observation.");

        await harness.CaptureAsync();

        return harness;
    }

    private static async Task<LongRestWorkIdentity> WorkAsync(LongRestSweepHarness harness)
    {
        Guid generation = Guid.Parse((string)(await harness.ScalarAsync("SELECT GenerationId FROM long_rest_discovery_generations;"))!);

        return LongRestSweepHarness.Require(await harness.Store.ReadNextFoldAsync(generation, null, CancellationToken.None))!.Fold.Work;
    }
}
