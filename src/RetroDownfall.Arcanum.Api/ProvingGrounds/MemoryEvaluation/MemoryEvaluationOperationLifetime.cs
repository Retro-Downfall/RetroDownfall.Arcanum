using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;

namespace RetroDownfall.Arcanum.Api.ProvingGrounds.MemoryEvaluation;

/// <summary>Installation-scoped single flight, held until adapter and accounting work have drained.</summary>
internal sealed class MemoryEvaluationExecutionSlot
{
    private readonly object _gate = new();

    private Reservation? _current;

    public Reservation? TryAcquire(Guid evaluationId)
    {
        lock (_gate)
        {
            if (_current is not null) return null;

            return _current = new Reservation(this, evaluationId);
        }
    }

    public Task WaitForInitializationAsync(Guid evaluationId, CancellationToken ct)
    {
        lock (_gate)
        {
            return _current?.EvaluationId == evaluationId ? _current.Initialized.WaitAsync(ct) : Task.CompletedTask;
        }
    }

    internal sealed class Reservation(MemoryEvaluationExecutionSlot owner, Guid evaluationId) : IDisposable
    {
        private readonly TaskCompletionSource _initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Guid EvaluationId { get; } = evaluationId;

        internal Task Initialized => _initialized.Task;

        internal void CompleteInitialization() => _initialized.TrySetResult();

        public void Dispose()
        {
            lock (owner._gate)
            {
                CompleteInitialization();

                if (ReferenceEquals(owner._current, this)) owner._current = null;
            }
        }
    }
}

/// <summary>Observes state independently of heartbeat renewal and retains the slot through actual I/O drain.</summary>
internal sealed class MemoryEvaluationOperationLifetime : IAsyncDisposable
{
    internal static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);

    private readonly CancellationTokenSource _stop = new();

    private readonly CancellationTokenSource _dispatch;

    private readonly Task _observer;

    private readonly Task _heartbeat;

    private readonly MemoryEvaluationExecutionSlot.Reservation _slot;

    private Task _provider = Task.CompletedTask;

    private Task? _disposal;

    private int _cancellationReason;

    public MemoryEvaluationOperationLifetime(IServiceScopeFactory scopes, Guid operationId, string ownerId, TimeProvider clock, MemoryEvaluationExecutionSlot.Reservation slot, CancellationToken caller, CancellationToken stopping, CancellationToken revocation)
    {
        _slot = slot;

        _dispatch = CancellationTokenSource.CreateLinkedTokenSource(caller, stopping, revocation, _stop.Token);

        _observer = ObserveAsync(scopes, operationId, ownerId, clock);

        _heartbeat = HeartbeatAsync(scopes, operationId, ownerId, clock);
    }

    public CancellationToken Token => _dispatch.Token;

    public MemoryEvaluationReason CancellationReason => Volatile.Read(ref _cancellationReason) is var reason && reason != 0
        ? (MemoryEvaluationReason)reason : MemoryEvaluationReason.Cancelled;

    public void TrackProvider(Task task)
    {
        if (!_provider.IsCompleted) throw new InvalidOperationException("Only one evaluation arm may be active.");

        _provider = task;
    }

    private async Task ObserveAsync(IServiceScopeFactory scopes, Guid id, string owner, TimeProvider clock)
    {
        try
        {
            using PeriodicTimer timer = new(TimeSpan.FromSeconds(1), clock);
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                // AsyncLocal ordinary request admission flows into this independent scoped connection;

                // the host's enrolled DbContext interceptor still proves its live provenance on open.
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();

                var state = await scope.ServiceProvider.GetRequiredService<ILongRunningOperationStore>().GetAsync(id, _stop.Token).ConfigureAwait(false);

                if (state is null || state.Kind != LongRunningOperationKinds.MemoryEvaluation || state.State != LongRunningOperationState.Running
                    || state.LeaseOwner != owner || state.LeaseExpiresAt is null || state.LeaseExpiresAt <= clock.GetUtcNow())
                { Cancel(state?.State == LongRunningOperationState.Cancelling ? MemoryEvaluationReason.Cancelled : MemoryEvaluationReason.Interrupted); return; }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception) { Cancel(MemoryEvaluationReason.Interrupted); }
    }

    private async Task HeartbeatAsync(IServiceScopeFactory scopes, Guid id, string owner, TimeProvider clock)
    {
        try
        {
            using PeriodicTimer timer = new(TimeSpan.FromSeconds(10), clock);
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();

                if (!await scope.ServiceProvider.GetRequiredService<ILongRunningOperationCoordinator>().HeartbeatAsync(id, owner, LeaseDuration, _stop.Token).ConfigureAwait(false))
                { Cancel(MemoryEvaluationReason.Interrupted); return; }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception) { Cancel(MemoryEvaluationReason.Interrupted); }
    }

    private void Cancel(MemoryEvaluationReason reason)
    {
        if (_dispatch.IsCancellationRequested) return;

        Interlocked.CompareExchange(ref _cancellationReason, (int)reason, 0);

        _dispatch.Cancel();
    }

    public ValueTask DisposeAsync() => new(_disposal ??= DrainAsync());

    private async Task DrainAsync()
    {
        _slot.CompleteInitialization();

        try
        {
            await _stop.CancelAsync().ConfigureAwait(false);

            try { await _provider.ConfigureAwait(false); } catch (Exception) { }
            await Task.WhenAll(_observer, _heartbeat).ConfigureAwait(false);
        }
        finally
        {
            _dispatch.Dispose();

            _stop.Dispose();

            _slot.Dispose();
        }
    }
}
