using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Api.ProvingGrounds.MemoryEvaluation;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Tests.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.ProvingGrounds;

[Collection("Grimoire")]
public sealed class MemoryEvaluationCancellationTests(GrimoireFixture fixture)
{
    [SkippableFact]
    public async Task Cancelling_with_successful_heartbeat_cancels_active_io_and_holds_slot_until_drain()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string path = fixture.CopyDatabase();

        await using var db = fixture.CreateContext(path);

        var clock = new ManualWallClockTimeProvider();

        var store = new LongRunningOperationStore(db, TestOrdinaryConnectionFactory.For(db));

        var coordinator = new LongRunningOperationCoordinator(store, clock);

        var started = await coordinator.StartAsync(new("memory-evaluation", LongRunningOperationRecoveryPolicy.AbandonSafely, "Memory evaluation", clock.GetUtcNow()), "request", TimeSpan.FromMinutes(2));

        int observers = 0;

        var services = new ServiceCollection();

        services.AddScoped(_ => { Interlocked.Increment(ref observers); return fixture.CreateContext(path); });

        services.AddScoped<ILongRunningOperationStore>(sp => new LongRunningOperationStore(sp.GetRequiredService<ArcanumDbContext>(), TestOrdinaryConnectionFactory.For(sp.GetRequiredService<ArcanumDbContext>())));

        services.AddScoped<ILongRunningOperationCoordinator>(sp => new LongRunningOperationCoordinator(sp.GetRequiredService<ILongRunningOperationStore>(), clock));

        await using var provider = services.BuildServiceProvider();

        var slot = new MemoryEvaluationExecutionSlot();

        Guid evaluationId = Guid.NewGuid();

        var owned = slot.TryAcquire(evaluationId);

        Assert.NotNull(owned);

        var lifetime = new MemoryEvaluationOperationLifetime(provider.GetRequiredService<IServiceScopeFactory>(), started.Operation.Id, "request", clock, owned, default, default, default);

        TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource drain = new(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task Transport()
        {
            using var registration = lifetime.Token.Register(() => cancelled.TrySetResult());
            await cancelled.Task;

            await drain.Task;
        }

        var active = Transport();

        lifetime.TrackProvider(active);

        var operation = (await store.GetAsync(started.Operation.Id))!;

        Assert.True(await store.RequestCancellationAsync(operation.Id, operation.Revision, clock.GetUtcNow()));

        Assert.True(await coordinator.HeartbeatAsync(operation.Id, "request", TimeSpan.FromMinutes(2)));

        clock.Advance(TimeSpan.FromSeconds(1));

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(observers > 0);

        Assert.Equal(MemoryEvaluationReason.Cancelled, lifetime.CancellationReason);

        Assert.Null(slot.TryAcquire(Guid.NewGuid()));

        var initializing = slot.WaitForInitializationAsync(evaluationId, default);

        Assert.False(initializing.IsCompleted);

        Assert.True(slot.WaitForInitializationAsync(Guid.NewGuid(), default).IsCompletedSuccessfully);

        var disposal = lifetime.DisposeAsync().AsTask();

        await initializing.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(disposal.IsCompleted);

        Assert.Null(slot.TryAcquire(Guid.NewGuid()));

        drain.SetResult();

        await disposal.WaitAsync(TimeSpan.FromSeconds(10));

        using var next = slot.TryAcquire(Guid.NewGuid());
        Assert.NotNull(next);
    }

    [Theory]
    [InlineData("lost-owner")]
    [InlineData("expired")]
    [InlineData("missing")]
    [InlineData("read-failure")]
    public async Task Independent_observer_cancels_when_its_live_state_proof_is_lost(string change)
    {
        string path = fixture.CopyDatabase();

        await using var db = fixture.CreateContext(path);

        var clock = new ManualWallClockTimeProvider();

        var store = new LongRunningOperationStore(db, TestOrdinaryConnectionFactory.For(db));

        var coordinator = new LongRunningOperationCoordinator(store, clock);

        var started = await coordinator.StartAsync(new("memory-evaluation", LongRunningOperationRecoveryPolicy.AbandonSafely, "Memory evaluation", clock.GetUtcNow()), "request", change == "expired" ? TimeSpan.FromSeconds(5) : TimeSpan.FromMinutes(2));

        var services = new ServiceCollection();

        services.AddScoped(_ => fixture.CreateContext(path));

        services.AddScoped<ILongRunningOperationStore>(sp => change == "read-failure" ? throw new InvalidOperationException("bounded fixture read failure") : new LongRunningOperationStore(sp.GetRequiredService<ArcanumDbContext>(), TestOrdinaryConnectionFactory.For(sp.GetRequiredService<ArcanumDbContext>())));

        await using var provider = services.BuildServiceProvider();

        var slot = new MemoryEvaluationExecutionSlot();

        await using var lifetime = new MemoryEvaluationOperationLifetime(provider.GetRequiredService<IServiceScopeFactory>(), change == "missing" ? Guid.NewGuid() : started.Operation.Id, change == "lost-owner" ? "other" : "request", clock, slot.TryAcquire(Guid.NewGuid())!, default, default, default);

        TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        using var registration = lifetime.Token.Register(() => cancelled.TrySetResult());
        clock.Advance(TimeSpan.FromSeconds(change == "expired" ? 5 : 1));

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(lifetime.Token.IsCancellationRequested);

        Assert.Equal(MemoryEvaluationReason.Interrupted, lifetime.CancellationReason);

        Assert.Null(slot.TryAcquire(Guid.NewGuid()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Request_shutdown_and_lease_revocation_cancel_dispatch(int source)
    {
        using var caller = new CancellationTokenSource();
        using var stopping = new CancellationTokenSource();
        using var revocation = new CancellationTokenSource();
        using var services = new ServiceCollection().BuildServiceProvider();
        var slot = new MemoryEvaluationExecutionSlot();

        await using var lifetime = new MemoryEvaluationOperationLifetime(services.GetRequiredService<IServiceScopeFactory>(), Guid.NewGuid(), "request", new ManualWallClockTimeProvider(), slot.TryAcquire(Guid.NewGuid())!, caller.Token, stopping.Token, revocation.Token);

        new[] { caller, stopping, revocation }[source].Cancel();

        Assert.Equal(MemoryEvaluationReason.Cancelled, lifetime.CancellationReason);

        Assert.True(lifetime.Token.IsCancellationRequested);
    }
}
