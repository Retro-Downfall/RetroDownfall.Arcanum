using System.Text.Json;

using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed partial class LongRestAttemptStoreTests
{
    [Fact]
    public async Task New_boot_settles_exact_interrupted_send_without_inventing_completion_or_payment()
    {
        await using LongRestSweepHarness h = await FixtureAsync();

        LongRestWorkIdentity work = await WorkAsync(h);

        LongRestAttemptStore store = new(h.Saga.Context, TimeProvider.System);

        LongRestDispatchAttempt first = LongRestSweepHarness.Require(await store.PrepareAttemptAsync(work, LongRestProviderPayloadKind.Summary, Payload, Destination, CancellationToken.None));

        first = LongRestSweepHarness.Require(await store.MarkSentAsync(first, CancellationToken.None));

        LongRestSweepHarness.Require(await store.CompleteAttemptAsync(first, new(LongRestDispatchAttemptState.Invalid, new(4, 1, 5, 0, 0), 0.25m, "LongRest.InvalidOutput"), CancellationToken.None));

        LongRestDispatchAttempt sent = LongRestSweepHarness.Require(await store.PrepareAttemptAsync(work, LongRestProviderPayloadKind.Summary, Payload, Destination, CancellationToken.None));

        sent = LongRestSweepHarness.Require(await store.MarkSentAsync(sent, CancellationToken.None));

        LongRestRecipeIdentity recipe = JsonSerializer.Deserialize((string)(await h.ScalarAsync("SELECT RecipeJson FROM long_rest_discovery_generations;"))!, LongRestJsonContext.Default.LongRestRecipeIdentity)!;

        LongRestSweepHarness.Require(await h.Store.StartOrResumeAsync(work.OperationId, work.Scope, work.Fence, recipe, CancellationToken.None));

        Assert.True((await store.PrepareAttemptAsync(work, LongRestProviderPayloadKind.Summary, Payload, Destination, CancellationToken.None)).IsFailure);

        LongRestSweepFence nextFence = work.Fence with { Configuration = work.Fence.Configuration with { BootId = Guid.NewGuid(), Epoch = 9 } };

        LongRestSweepHarness.Require(await h.Store.StartOrResumeAsync(work.OperationId, work.Scope, nextFence, recipe, CancellationToken.None));

        Assert.Equal(6L, await h.ScalarAsync("SELECT StateCode FROM long_rest_provider_effects;"));

        Assert.Equal(1L, await h.ScalarAsync("SELECT FailureCount FROM long_rest_provider_effects;"));

        Assert.Equal(2L, await h.ScalarAsync("SELECT SentCount FROM long_rest_provider_effects;"));

        Assert.Equal(1L, await h.ScalarAsync("SELECT PaidCount FROM long_rest_provider_effects;"));

        Assert.Equal("0.25", await h.ScalarAsync("SELECT CostUsd FROM long_rest_provider_effects;"));

        Assert.Equal(DBNull.Value, await h.ScalarAsync("SELECT CompletionJson FROM long_rest_provider_effects;"));

        Assert.Equal(sent.AttemptId.ToString("D").ToUpperInvariant(), await h.ScalarAsync("SELECT AttemptId FROM long_rest_provider_effects;"));

        Assert.Equal(LongRestDiscoveryHash.Work(work).Value.Bytes, (byte[])(await h.ScalarAsync("SELECT WorkDigest FROM long_rest_provider_effects;"))!);

        Assert.True((await store.CompleteAttemptAsync(sent, new(LongRestDispatchAttemptState.Accepted, new(5, 1, 6, 0, 0), 0.5m, null), CancellationToken.None)).IsFailure);

        LongRestWorkIdentity current = await WorkAsync(h);

        LongRestDispatchAttempt fresh = LongRestSweepHarness.Require(await store.PrepareAttemptAsync(current, LongRestProviderPayloadKind.Summary, Payload, Destination, CancellationToken.None));

        Assert.Equal(3UL, fresh.PhysicalOrdinal);

        Assert.NotEqual(sent.AttemptId, fresh.AttemptId);

        Assert.Null(fresh.DisclosureReceiptDigest);

        Assert.Equal(1L, await h.ScalarAsync("SELECT PaidCount FROM long_rest_provider_effects;"));
    }

    [Fact]
    public async Task New_boot_refuses_malformed_original_sent_work_association_without_fence_change()
    {
        await using LongRestSweepHarness h = await FixtureAsync();

        LongRestWorkIdentity work = await WorkAsync(h);

        LongRestAttemptStore store = new(h.Saga.Context, TimeProvider.System);

        LongRestDispatchAttempt sent = LongRestSweepHarness.Require(await store.PrepareAttemptAsync(work, LongRestProviderPayloadKind.Summary, Payload, Destination, CancellationToken.None));

        LongRestSweepHarness.Require(await store.MarkSentAsync(sent, CancellationToken.None));

        await h.ExecuteAsync("UPDATE long_rest_provider_effects SET WorkDigest = $wrong;", ("$wrong", Payload.Bytes));

        string before = (string)(await h.ScalarAsync("SELECT FenceJson FROM long_rest_discovery_generations;"))!;

        LongRestRecipeIdentity recipe = JsonSerializer.Deserialize((string)(await h.ScalarAsync("SELECT RecipeJson FROM long_rest_discovery_generations;"))!, LongRestJsonContext.Default.LongRestRecipeIdentity)!;

        Assert.True((await h.Store.StartOrResumeAsync(work.OperationId, work.Scope,
            work.Fence with { Configuration = work.Fence.Configuration with { BootId = Guid.NewGuid() } }, recipe, CancellationToken.None)).IsFailure);

        Assert.Equal(before, await h.ScalarAsync("SELECT FenceJson FROM long_rest_discovery_generations;"));

        Assert.Equal(3L, await h.ScalarAsync("SELECT StateCode FROM long_rest_provider_effects;"));

        Assert.Equal(0L, await h.ScalarAsync("SELECT FailureCount FROM long_rest_provider_effects;"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task New_boot_refuses_a_sent_effect_without_its_exact_pending_unit(bool wrongOperation)
    {
        await using LongRestSweepHarness h = await FixtureAsync();

        LongRestWorkIdentity work = await WorkAsync(h);

        LongRestAttemptStore store = new(h.Saga.Context, TimeProvider.System);

        LongRestDispatchAttempt sent = LongRestSweepHarness.Require(await store.PrepareAttemptAsync(work, LongRestProviderPayloadKind.Summary, Payload, Destination, CancellationToken.None));

        LongRestSweepHarness.Require(await store.MarkSentAsync(sent, CancellationToken.None));

        if (wrongOperation)
        {
            Guid operation = Guid.NewGuid();

            await h.ExecuteAsync("INSERT INTO LongRunningOperations (Id, Kind, State, RecoveryPolicy, CreatedAt, PublicSummary) VALUES ($id, 'long-rest-sweep', 1, 1, '2026-10-10T00:00:00.0000000Z', 'other');", ("$id", operation.ToString("N")));

            await h.ExecuteAsync("UPDATE long_rest_work_items SET OperationId = $operation;", ("$operation", operation.ToString("N")));
        }
        else
        {
            await h.ExecuteAsync("UPDATE long_rest_work_items SET DispositionCode = 4;");
        }

        string before = (string)(await h.ScalarAsync("SELECT FenceJson FROM long_rest_discovery_generations;"))!;

        long changes = (long)(await h.ScalarAsync("SELECT total_changes();"))!;

        LongRestRecipeIdentity recipe = JsonSerializer.Deserialize((string)(await h.ScalarAsync("SELECT RecipeJson FROM long_rest_discovery_generations;"))!, LongRestJsonContext.Default.LongRestRecipeIdentity)!;

        Assert.True((await h.Store.StartOrResumeAsync(work.OperationId, work.Scope,
            work.Fence with { Configuration = work.Fence.Configuration with { BootId = Guid.NewGuid() } }, recipe, CancellationToken.None)).IsFailure);

        Assert.Equal(before, await h.ScalarAsync("SELECT FenceJson FROM long_rest_discovery_generations;"));

        Assert.Equal(3L, await h.ScalarAsync("SELECT StateCode FROM long_rest_provider_effects;"));

        Assert.Equal(changes, await h.ScalarAsync("SELECT total_changes();"));
    }
}
