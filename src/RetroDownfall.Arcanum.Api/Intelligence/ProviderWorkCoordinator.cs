using System.Diagnostics.Metrics;
using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Coordinates foreground turns and individual background provider calls on a shared resource.
/// Reservations survive tool gaps; a dispatched call remains active until its lease is disposed.
/// </summary>
public sealed class ProviderWorkCoordinator : IDisposable
{
    private static readonly Meter Meter = new("RetroDownfall.Arcanum.ProviderWork");

    private static readonly Counter<long> Waiting = Meter.CreateCounter<long>("arcanum.provider_work.waiting");

    private static readonly Counter<long> Resumed = Meter.CreateCounter<long>("arcanum.provider_work.resumed");

    private readonly object _gate = new();

    private readonly Dictionary<ProviderResourceKey, Resource> _resources = [];

    private bool _disposed;

    private sealed class Resource
    {
        internal int Owners;

        internal int Pending;

        internal int ForegroundCalls;

        internal bool Background;

        internal readonly LinkedList<object> BackgroundQueue = [];

        internal TaskCompletionSource Changed = NewSignal();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void Signal(Resource resource)
    {
        TaskCompletionSource old = resource.Changed;

        resource.Changed = NewSignal();

        old.TrySetResult();
    }

    private Resource Get(ProviderResourceKey key)
    {
        if (!_resources.TryGetValue(key, out Resource? resource))
        {
            resource = new Resource();

            _resources.Add(key, resource);
        }

        return resource;
    }

    // States are retained while an owner, physical call, or waiter can still name them.
    private void Prune(ProviderResourceKey key, Resource resource)
    {
        if (resource.Owners == 0 && resource.Pending == 0 && resource.ForegroundCalls == 0
            && !resource.Background && resource.BackgroundQueue.Count == 0)
        {
            _resources.Remove(key);
        }
    }

    internal ForegroundTurn BeginForegroundTurn(Func<string, CancellationToken, ValueTask>? reportStatus = null)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            return new ForegroundTurn(this, reportStatus);
        }
    }

    internal async ValueTask<IDisposable> EnterCallAsync(ProviderResourceKey key, ForegroundTurn? owner, CancellationToken ct)
    {
        CancellationToken closing = ProviderWorkWaitAmbient.CancellationToken;

        if (owner is not null || !closing.CanBeCanceled)
        {
            return await EnterCallCoreAsync(key, owner, ct).ConfigureAwait(false);
        }

        // Only the unsent admission observes closure. The returned physical-call lease and the
        // caller's provider token must survive closure so an already dispatched effect can settle.
        using CancellationTokenSource waiting = CancellationTokenSource.CreateLinkedTokenSource(ct, closing);

        IDisposable? admitted = null;

        try
        {
            admitted = await EnterCallCoreAsync(key, null, waiting.Token).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();

            closing.ThrowIfCancellationRequested();

            return admitted;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            admitted?.Dispose();

            throw new OperationCanceledException(ct);
        }
        catch (OperationCanceledException) when (closing.IsCancellationRequested)
        {
            admitted?.Dispose();

            throw new ProviderWorkDeferredException(closing);
        }
    }

    private async ValueTask<IDisposable> EnterCallCoreAsync(ProviderResourceKey key, ForegroundTurn? owner, CancellationToken ct)
    {
        if (owner is not null)
        {
            if (!ReferenceEquals(owner.Coordinator, this)) throw new ArgumentException("Owner belongs to another coordinator.", nameof(owner));

            await owner.ReserveAsync(key, ct).ConfigureAwait(false);

            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed || !owner.Active, owner);

                ct.ThrowIfCancellationRequested();

                Resource foregroundResource = Get(key);

                foregroundResource.ForegroundCalls++;

                return new CallLease(this, key, foregroundResource, false);
            }
        }

        bool waited = false;

        Resource resource;

        LinkedListNode<object> ticket;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            ct.ThrowIfCancellationRequested();

            resource = Get(key);

            ticket = resource.BackgroundQueue.AddLast(new object());
        }

        try
        {
            while (true)
            {
                Task? changed = null;

                bool acquired = false;

                bool notifyWaiting = false;

                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);

                    ct.ThrowIfCancellationRequested();

                    if (ReferenceEquals(resource.BackgroundQueue.First, ticket) && !resource.Background
                        && resource.Owners == 0 && resource.Pending == 0 && resource.ForegroundCalls == 0)
                    {
                        resource.BackgroundQueue.Remove(ticket);

                        resource.Background = true;

                        acquired = true;
                    }
                    else
                    {
                        notifyWaiting = !waited;

                        waited = true;

                        changed = resource.Changed.Task;
                    }
                }

                // Meter listeners are external code and must never run under the coordinator lock.
                if (notifyWaiting) Waiting.Add(1, new KeyValuePair<string, object?>("kind", "background"));

                if (acquired)
                {
                    var lease = new CallLease(this, key, resource, true);

                    try
                    {
                        if (waited) Resumed.Add(1, new KeyValuePair<string, object?>("kind", "background"));

                        return lease;
                    }
                    catch
                    {
                        lease.Dispose();

                        throw;
                    }
                }

                await changed!.WaitAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_gate)
            {
                if (ticket.List is not null)
                {
                    resource.BackgroundQueue.Remove(ticket);

                    Signal(resource);

                    Prune(key, resource);
                }
            }
        }
    }

    internal sealed class ForegroundTurn : IDisposable, IProviderWorkOwner
    {
        internal readonly ProviderWorkCoordinator Coordinator;

        private readonly Func<string, CancellationToken, ValueTask>? _reportStatus;

        private readonly HashSet<ProviderResourceKey> _reserved = [];

        internal bool Active = true;

        public bool IsActive { get { lock (Coordinator._gate) return Active && !Coordinator._disposed; } }

        internal ForegroundTurn(ProviderWorkCoordinator coordinator, Func<string, CancellationToken, ValueTask>? reportStatus)
        {
            Coordinator = coordinator;

            _reportStatus = reportStatus;
        }

        internal async ValueTask ReserveAsync(ProviderResourceKey key, CancellationToken ct)
        {
            Resource resource;

            bool pending = false;

            bool waited = false;

            try
            {
                while (true)
                {
                    Task changed;

                    lock (Coordinator._gate)
                    {
                        ObjectDisposedException.ThrowIf(Coordinator._disposed || !Active, this);

                        ct.ThrowIfCancellationRequested();

                        resource = Coordinator.Get(key);

                        if (_reserved.Contains(key)) return;

                        if (!resource.Background)
                        {
                            _reserved.Add(key);

                            resource.Owners++;

                            if (pending)
                            {
                                resource.Pending--;

                                pending = false;
                            }

                            Signal(resource);

                            break;
                        }

                        if (!pending)
                        {
                            resource.Pending++;

                            pending = true;
                        }

                        changed = resource.Changed.Task;
                    }

                    if (!waited)
                    {
                        waited = true;

                        Waiting.Add(1, new KeyValuePair<string, object?>("kind", "foreground"));

                        if (_reportStatus is not null) await _reportStatus("Waiting for background model work to finish.", ct).ConfigureAwait(false);
                    }

                    await changed.WaitAsync(ct).ConfigureAwait(false);
                }

                if (waited)
                {
                    Resumed.Add(1, new KeyValuePair<string, object?>("kind", "foreground"));

                    if (_reportStatus is not null) await _reportStatus("Background model work finished; resuming.", ct).ConfigureAwait(false);
                }
            }
            finally
            {
                if (pending)
                {
                    lock (Coordinator._gate)
                    {
                        resource = Coordinator.Get(key);

                        resource.Pending--;

                        Signal(resource);

                        Coordinator.Prune(key, resource);
                    }
                }
            }
        }

        public void Dispose()
        {
            lock (Coordinator._gate)
            {
                if (!Active) return;

                Active = false;

                foreach (ProviderResourceKey key in _reserved)
                {
                    Resource resource = Coordinator.Get(key);

                    resource.Owners--;

                    Signal(resource);

                    Coordinator.Prune(key, resource);
                }

                _reserved.Clear();

                foreach (Resource resource in Coordinator._resources.Values) Signal(resource);
            }
        }
    }

    private sealed class CallLease(ProviderWorkCoordinator coordinator, ProviderResourceKey key, Resource resource, bool background) : IDisposable
    {
        private bool _active = true;

        public void Dispose()
        {
            lock (coordinator._gate)
            {
                if (!_active) return;

                _active = false;

                if (background)
                {
                    resource.Background = false;
                }
                else
                {
                    resource.ForegroundCalls--;
                }

                Signal(resource);

                coordinator.Prune(key, resource);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;

            _disposed = true;

            foreach (Resource resource in _resources.Values) Signal(resource);
        }
    }
}
