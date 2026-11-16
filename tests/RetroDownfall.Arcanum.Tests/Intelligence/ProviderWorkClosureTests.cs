using System.Diagnostics.Metrics;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class ProviderWorkClosureTests
{
    private static readonly ProviderResourceKey Resource = ProviderResourceKey.FromEndpoint("http://localhost:11434")!.Value;

    [Fact]
    public async Task Closing_cancels_unsent_wait_and_leaves_foreground_owner_active()
    {
        using ProviderWorkCoordinator coordinator = new();

        using ProviderWorkCoordinator.ForegroundTurn foreground = coordinator.BeginForegroundTurn();

        await foreground.ReserveAsync(Resource, CancellationToken.None);

        using CancellationTokenSource closing = new();

        Task<IDisposable> waiting;

        using (ProviderWorkWaitAmbient.Push(closing.Token))
        {
            waiting = coordinator.EnterCallAsync(Resource, null, CancellationToken.None).AsTask();
        }

        Assert.False(waiting.IsCompleted);

        closing.Cancel();

        try
        {
            await Assert.ThrowsAsync<ProviderWorkDeferredException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.True(foreground.IsActive);

            using IDisposable call = await coordinator.EnterCallAsync(Resource, foreground, CancellationToken.None);
        }
        finally
        {
            foreground.Dispose();

            try { (await waiting).Dispose(); } catch (ProviderWorkDeferredException) { }
        }
    }

    [Fact]
    public async Task Closing_does_not_release_an_already_admitted_call()
    {
        using ProviderWorkCoordinator coordinator = new();

        using CancellationTokenSource closing = new();

        IDisposable admitted;

        using (ProviderWorkWaitAmbient.Push(closing.Token))
        {
            admitted = await coordinator.EnterCallAsync(Resource, null, CancellationToken.None);
        }

        using (admitted)
        using (ProviderWorkCoordinator.ForegroundTurn foreground = coordinator.BeginForegroundTurn())
        {
            Task reservation = foreground.ReserveAsync(Resource, CancellationToken.None).AsTask();

            closing.Cancel();

            Assert.False(reservation.IsCompleted);

            admitted.Dispose();

            await reservation.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(foreground.IsActive);
        }
    }

    [Fact]
    public async Task Foreground_calls_do_not_inherit_background_closure_cancellation()
    {
        using ProviderWorkCoordinator coordinator = new();

        using ProviderWorkCoordinator.ForegroundTurn foreground = coordinator.BeginForegroundTurn();

        using CancellationTokenSource closing = new();

        closing.Cancel();

        using IDisposable scope = ProviderWorkWaitAmbient.Push(closing.Token);

        using IDisposable admitted = await coordinator.EnterCallAsync(Resource, foreground, CancellationToken.None);

        Assert.True(foreground.IsActive);
    }

    [Fact]
    public async Task Caller_cancellation_retains_its_token_when_closure_also_cancels()
    {
        using ProviderWorkCoordinator coordinator = new();

        using CancellationTokenSource caller = new();

        using CancellationTokenSource closing = new();

        caller.Cancel();

        closing.Cancel();

        using IDisposable scope = ProviderWorkWaitAmbient.Push(closing.Token);

        OperationCanceledException failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await coordinator.EnterCallAsync(Resource, null, caller.Token));

        Assert.IsNotType<ProviderWorkDeferredException>(failure);

        Assert.Equal(caller.Token, failure.CancellationToken);
    }

    [Fact]
    public async Task Closing_at_admission_handover_releases_the_acquired_call()
    {
        using ProviderWorkCoordinator coordinator = new();

        using ProviderWorkCoordinator.ForegroundTurn foreground = coordinator.BeginForegroundTurn();

        await foreground.ReserveAsync(Resource, CancellationToken.None);

        using CancellationTokenSource closing = new();

        AsyncLocal<bool> ownCall = new() { Value = true };

        using MeterListener listener = new();

        listener.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Meter.Name == "RetroDownfall.Arcanum.ProviderWork"
                && instrument.Name == "arcanum.provider_work.resumed") current.EnableMeasurementEvents(instrument);
        };

        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            if (!ownCall.Value) return;

            foreach (KeyValuePair<string, object?> tag in tags)
            {
                if (tag.Key == "kind" && Equals(tag.Value, "background")) closing.Cancel();
            }
        });

        listener.Start();

        Task<IDisposable> waiting;

        using (ProviderWorkWaitAmbient.Push(closing.Token))
        {
            waiting = coordinator.EnterCallAsync(Resource, null, CancellationToken.None).AsTask();
        }

        foreground.Dispose();

        await Assert.ThrowsAsync<ProviderWorkDeferredException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(5)));

        using ProviderWorkCoordinator.ForegroundTurn next = coordinator.BeginForegroundTurn();

        await next.ReserveAsync(Resource, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(next.IsActive);
    }
}
