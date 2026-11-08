using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

internal sealed partial class ApprenticeService
{
    private static readonly AsyncLocal<GuildProviderActivity?> GuildActivity = new();

    private sealed class GuildProviderActivity(GuildEngineFrame frame)
    {
        internal GuildEngineFrame Frame { get; } = frame;

        internal bool Running = true;

        internal bool Suspended;

        internal bool Closed;
    }

    private sealed partial class GuildEngineFrame
    {
        private readonly SemaphoreSlim _capacitySerial = new(1, 1);

        private int _activeTurns;

        private int _childWaits;

        private bool _expired;

        internal async Task<GuildProviderActivity> BeginActivityAsync(CancellationToken ct)
        {
            await _capacitySerial.WaitAsync(ct).ConfigureAwait(false);

            try
            {
                await EnsureCapacityAsync(ct).ConfigureAwait(false);

                _activeTurns++;

                return new(this);
            }
            finally
            {
                _capacitySerial.Release();
            }
        }

        internal async ValueTask<IAsyncDisposable> SuspendForChildAsync(GuildLocalInferenceExecution execution, CancellationToken ct)
        {
            GuildProviderActivity? activity = GuildActivity.Value;

            await _capacitySerial.WaitAsync(ct).ConfigureAwait(false);

            try
            {
                RequireCurrentReservation();

                if (!ReferenceEquals(execution, Execution) || !ReferenceEquals(GuildEngine.Value, this)
                    || activity is null || !ReferenceEquals(activity.Frame, this) || activity.Closed || !activity.Running)
                    throw new InvalidOperationException("The child wait does not belong to an active Guild provider turn.");

                activity.Running = false;

                activity.Suspended = true;

                _activeTurns--;

                _childWaits++;

                ReleaseIdleCapacity();

                return new GuildChildCapacityWait(this, activity);
            }
            finally
            {
                _capacitySerial.Release();
            }
        }

        internal async Task EndActivityAsync(GuildProviderActivity activity)
        {
            await _capacitySerial.WaitAsync().ConfigureAwait(false);

            try
            {
                if (activity.Closed) return;

                activity.Closed = true;

                if (activity.Running) _activeTurns--;

                if (activity.Suspended) _childWaits--;

                activity.Running = activity.Suspended = false;

                ReleaseIdleCapacity();
            }
            finally
            {
                _capacitySerial.Release();
            }
        }

        internal async Task ExpireCapacityAsync()
        {
            await _capacitySerial.WaitAsync().ConfigureAwait(false);

            try
            {
                _expired = true;
            }
            finally
            {
                _capacitySerial.Release();
            }
        }

        private async ValueTask ResumeChildAsync(GuildProviderActivity activity)
        {
            await _capacitySerial.WaitAsync().ConfigureAwait(false);

            try
            {
                if (activity.Closed || !activity.Suspended) return;

                activity.Suspended = false;

                _childWaits--;

                if (_expired || LifetimeToken.IsCancellationRequested) return;

                await EnsureCapacityAsync(LifetimeToken).ConfigureAwait(false);

                activity.Running = true;

                _activeTurns++;
            }
            finally
            {
                _capacitySerial.Release();
            }
        }

        private void ReleaseIdleCapacity()
        {
            // A sibling still executing a turn retains the parent's existing slot. Only a
            // wholly blocked parent yields; its reservation and cancellation token stay owned.
            if (_expired || _activeTurns != 0 || _childWaits == 0) return;

            lock (Owner._executionLifecycleLock)
            {
                if (Owner._executionReservations.TryGetValue(Binding.ApprenticeId, out ExecutionReservation? reservation)
                    && reservation.Generation == Generation
                    && Owner._executionLeases.TryRemove(Binding.ApprenticeId, out AdmissionGate.Lease? lease)) lease.Dispose();
            }
        }

        private async Task EnsureCapacityAsync(CancellationToken ct)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                RequireCurrentReservation();

                if (Owner._executionLeases.ContainsKey(Binding.ApprenticeId)) return;

                if (Owner._concurrencyGate.TryAcquire(
                    ArcanumSettingClamps.MaxConcurrentApprentices(Owner.GetApprenticeSettings().MaxConcurrentApprentices), out AdmissionGate.Lease? acquired))
                {
                    if (!Owner._executionLeases.TryAdd(Binding.ApprenticeId, acquired!)) acquired!.Dispose();

                    return;
                }

                await Task.Delay(50, ct).ConfigureAwait(false);
            }
        }

        private void RequireCurrentReservation()
        {
            lock (Owner._executionLifecycleLock)
            {
                if (_expired || Owner._stopping || !Owner._executionReservations.TryGetValue(Binding.ApprenticeId, out ExecutionReservation? reservation)
                    || reservation.Generation != Generation || reservation.StopRequested)
                    throw new OperationCanceledException("The Guild execution reservation is no longer active.", LifetimeToken);
            }
        }

        private sealed class GuildChildCapacityWait(GuildEngineFrame frame, GuildProviderActivity activity) : IAsyncDisposable
        {
            private int _disposed;

            public ValueTask DisposeAsync() => Interlocked.Exchange(ref _disposed, 1) == 0
                ? frame.ResumeChildAsync(activity) : ValueTask.CompletedTask;
        }
    }
}
