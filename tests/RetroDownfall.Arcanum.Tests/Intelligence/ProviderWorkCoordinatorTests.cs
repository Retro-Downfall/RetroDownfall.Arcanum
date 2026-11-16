using System.Diagnostics.Metrics;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class ProviderWorkCoordinatorTests
{
    private static ProviderResourceKey Key(string endpoint) => ProviderResourceKey.FromEndpoint(endpoint)!.Value;

    [Fact]
    public async Task Foreground_waits_for_active_call_and_blocks_next_background_until_turn_ends()
    {
        using var coordinator = new ProviderWorkCoordinator();
        var key = Key("http://localhost:11434/v1");
        using var active = await coordinator.EnterCallAsync(key, null, CancellationToken.None);
        using var turn = coordinator.BeginForegroundTurn();
        Task reservation = turn.ReserveAsync(key, CancellationToken.None).AsTask();
        Task<IDisposable> later = coordinator.EnterCallAsync(key, null, CancellationToken.None).AsTask();
        Assert.False(reservation.IsCompleted);
        Assert.False(later.IsCompleted);
        active.Dispose();
        await reservation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(later.IsCompleted);
        using (await coordinator.EnterCallAsync(key, turn, CancellationToken.None)) { }
        Assert.False(later.IsCompleted);
        turn.Dispose();
        (await later.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }

    [Fact]
    public async Task Multiple_foregrounds_and_independent_origins_can_proceed()
    {
        using var coordinator = new ProviderWorkCoordinator();
        using var first = coordinator.BeginForegroundTurn();
        using var second = coordinator.BeginForegroundTurn();
        var key = Key("http://localhost:11434/v1");
        await first.ReserveAsync(key, CancellationToken.None);
        await second.ReserveAsync(key, CancellationToken.None);
        using var other = await coordinator.EnterCallAsync(Key("http://localhost:11435/v1"), null, CancellationToken.None);
        using var call = await coordinator.EnterCallAsync(key, second, CancellationToken.None);
    }

    [Fact]
    public async Task Cancellation_and_owner_disposal_do_not_release_active_call_early()
    {
        using var coordinator = new ProviderWorkCoordinator();
        var key = Key("http://localhost:11434/v1");
        using var turn = coordinator.BeginForegroundTurn();
        using var call = await coordinator.EnterCallAsync(key, turn, CancellationToken.None);
        using var canceled = new CancellationTokenSource();
        Task<IDisposable> waiter = coordinator.EnterCallAsync(key, null, canceled.Token).AsTask();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiter);
        turn.Dispose();
        Task<IDisposable> later = coordinator.EnterCallAsync(key, null, CancellationToken.None).AsTask();
        Assert.False(later.IsCompleted);
        call.Dispose();
        (await later.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await coordinator.EnterCallAsync(key, turn, CancellationToken.None));
    }

    [Fact]
    public void Endpoint_identity_is_origin_based_and_loopback_normalized()
    {
        Assert.Equal(Key("http://localhost:11434/v1"), Key("http://127.0.0.1:11434/other"));
        Assert.Equal(Key("http://[::1]:11434"), Key("http://localhost:11434"));
        Assert.NotEqual(Key("http://localhost:11434"), Key("http://localhost:11435"));
        Assert.Null(ProviderResourceKey.FromProvider(new ProviderSettings { Type = AiProviderKind.CodexCli }));
    }
    [Fact]
    public async Task Waiting_reports_content_free_diagnostics_and_shutdown_wakes_waiter()
    {
        long waiting = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "RetroDownfall.Arcanum.ProviderWork" && instrument.Name == "arcanum.provider_work.waiting")
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref waiting, value));
        listener.Start();
        var coordinator = new ProviderWorkCoordinator();
        var key = Key("http://localhost:11434");
        using var background = await coordinator.EnterCallAsync(key, null, CancellationToken.None);
        var messages = new List<string>();
        using var turn = coordinator.BeginForegroundTurn((message, _) => { messages.Add(message); return ValueTask.CompletedTask; });
        Task queued = turn.ReserveAsync(key, CancellationToken.None).AsTask();
        Assert.Equal(1, Volatile.Read(ref waiting));
        Assert.Single(messages);
        coordinator.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await queued);
    }

    [Fact]
    public async Task Background_waiters_are_served_in_order_and_cancellation_unblocks_the_next()
    {
        using var coordinator = new ProviderWorkCoordinator();
        var key = Key("http://localhost:11434");
        using var turn = coordinator.BeginForegroundTurn();
        await turn.ReserveAsync(key, CancellationToken.None);
        using var cancelled = new CancellationTokenSource();
        Task<IDisposable> first = coordinator.EnterCallAsync(key, null, cancelled.Token).AsTask();
        Task<IDisposable> second = coordinator.EnterCallAsync(key, null, CancellationToken.None).AsTask();
        Task<IDisposable> third = coordinator.EnterCallAsync(key, null, CancellationToken.None).AsTask();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await first);
        turn.Dispose();
        using var secondLease = await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(third.IsCompleted);
        secondLease.Dispose();
        (await third.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }

    [Fact]
    public async Task Concurrent_reservations_and_disposal_of_queued_owner_release_priority()
    {
        using var coordinator = new ProviderWorkCoordinator();
        var key = Key("http://localhost:11434");
        using var active = await coordinator.EnterCallAsync(key, null, CancellationToken.None);
        using var turn = coordinator.BeginForegroundTurn();
        Task first = turn.ReserveAsync(key, CancellationToken.None).AsTask();
        Task second = turn.ReserveAsync(key, CancellationToken.None).AsTask();
        turn.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await first);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await second);
        Task<IDisposable> next = coordinator.EnterCallAsync(key, null, CancellationToken.None).AsTask();
        active.Dispose();
        (await next.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }

    [Fact]
    public async Task Meter_listener_can_reenter_coordinator_during_wait_notification()
    {
        using var coordinator = new ProviderWorkCoordinator();
        var key = Key("http://localhost:11434");
        using var turn = coordinator.BeginForegroundTurn();
        await turn.ReserveAsync(key, CancellationToken.None);
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "RetroDownfall.Arcanum.ProviderWork" && instrument.Name == "arcanum.provider_work.waiting") l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) =>
        {
            using var finished = new ManualResetEventSlim();
            var thread = new Thread(() => { using var nested = coordinator.BeginForegroundTurn(); finished.Set(); });
            thread.Start();
            Assert.True(finished.Wait(TimeSpan.FromSeconds(5)));
            thread.Join();
        });
        listener.Start();
        Task<IDisposable> pending = coordinator.EnterCallAsync(key, null, CancellationToken.None).AsTask();
        turn.Dispose();
        (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }

    [Fact]
    public async Task Concurrent_reservations_for_one_owner_release_as_one_reservation()
    {
        using var coordinator = new ProviderWorkCoordinator();
        var key = Key("http://localhost:11434");
        using var active = await coordinator.EnterCallAsync(key, null, CancellationToken.None);
        using var turn = coordinator.BeginForegroundTurn();
        Task first = turn.ReserveAsync(key, CancellationToken.None).AsTask();
        Task second = turn.ReserveAsync(key, CancellationToken.None).AsTask();
        active.Dispose();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Task<IDisposable> next = coordinator.EnterCallAsync(key, null, CancellationToken.None).AsTask();
        Assert.False(next.IsCompleted);
        turn.Dispose();
        (await next.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }

}
