using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;

namespace RetroDownfall.Arcanum.Core.Intelligence.MemoryDelegation;

/// <summary>One process-local owner for grant admission and source mutation linearization.</summary>
public sealed class MemoryDelegationCoordinator(TimeProvider timeProvider) : IMemoryDelegationMutationBarrier
{
    private readonly object _sync = new();

    private readonly SemaphoreSlim _admission = new(1, 1);

    private readonly HashSet<Registration> _live = [];

    private readonly HashSet<TaskCompletionSource> _preparing = [];

    private readonly Dictionary<Guid, Registration> _roots = [];

    private bool _maintenanceClosed;

    private long _generation;

    private static Error Refused => new(ErrorCodes.MemoryDelegation.StaleSource, "The memory delegation capability is unavailable or no longer live.");

    public async Task<Result<MemoryDelegationRoot>> PrepareAsync(Guid rootId, MemoryDelegationGrant grant, ArcanumInvocationContext parent,
        CovenantTurnLease lease, IMemoryDelegationSourceReader reader, CancellationToken ct = default)
    {
        if (rootId == Guid.Empty || !MemoryDelegationDigests.IsValid(grant))
        {
            return new Error(ErrorCodes.MemoryDelegation.InvalidGrant, "The memory delegation grant violates policy v1.");
        }

        TaskCompletionSource preparation = new(TaskCreationOptions.RunContinuationsAsynchronously);

        DateTimeOffset preparedAt = timeProvider.GetUtcNow();

        await _admission.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            long generation;

            lock (_sync)
            {
                if (_maintenanceClosed || _live.Count >= MemoryDelegationLimits.ActiveGrantsPerProcess || _roots.ContainsKey(rootId))
                {
                    return Refused;
                }

                generation = _generation;

                _preparing.Add(preparation);
            }

            Result<MemoryEvaluationSourceSnapshot> read = await reader.ReadAsync(grant, parent, lease, ct).ConfigureAwait(false);

            if (read.IsFailure)
            {
                return read.Error;
            }

            DateTimeOffset expires = preparedAt + MemoryDelegationLimits.MaximumLifetime;

            foreach (MemoryEvaluationSourceCoordinate coordinate in read.Value.Coordinates)
            {
                if (coordinate.Role == MemoryEvaluationCoordinateRole.Intervention && coordinate.ValidToUtc is { } deadline && deadline < expires)
                {
                    expires = deadline;
                }
            }

            Registration registration = new(rootId, parent, lease, reader, read.Value, expires);

            MemoryDelegationRoot root = new(this, registration);

            lock (_sync)
            {
                if (_maintenanceClosed || generation != _generation || expires <= timeProvider.GetUtcNow() || ct.IsCancellationRequested)
                {
                    return Refused;
                }

                _live.Add(registration);

                _roots.Add(rootId, registration);
            }

            // Registration callbacks may run synchronously; never create them under _sync.
            // Subscribe to the Turn lifetime before touching its registration-owned CTS, which
            // may already have been disposed after the source read completed.
            try
            {
                registration.ParentCancellation = ct.Register(() => Revoke(registration));

                registration.LeaseCompletion = lease.LifetimeEnded.Register(() => Revoke(registration));

                registration.LeaseCancellation = lease.Revocation.Register(() => Revoke(registration));

                TimeSpan remaining = expires - timeProvider.GetUtcNow();

                registration.Timer = timeProvider.CreateTimer(_ => Revoke(registration), null, remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
                await CompleteAsync(root).ConfigureAwait(false);

                return Refused;
            }

            bool live;

            lock (_sync)
            {
                live = IsLive(registration);
            }

            if (!live)
            {
                await CompleteAsync(root).ConfigureAwait(false);

                return Refused;
            }

            return root;
        }
        finally
        {
            lock (_sync)
            {
                preparation.TrySetResult();

                _preparing.Remove(preparation);
            }

            _admission.Release();
        }
    }

    public async Task<Result<MemoryDelegationChild>> ConsumeAsync(MemoryDelegationRoot root, Guid rootId, Guid childId, CovenantDigest taskDigest, CancellationToken ct = default)
    {
        if (!ReferenceEquals(root.Owner, this) || root.Registration.RootId != rootId || childId == Guid.Empty || !taskDigest.IsValid)
        {
            return Refused;
        }

        await _admission.WaitAsync(ct).ConfigureAwait(false);

        MemoryDelegationEffect? proof = null;

        try
        {
            Registration registration = root.Registration;

            lock (_sync)
            {
                if (!IsLive(registration) || registration.Consumed)
                {
                    return Refused;
                }

                RegisterEffect(registration);

                proof = new MemoryDelegationEffect(root);
            }

            Result valid = await registration.Reader!.ValidateAsync(registration.Snapshot!, registration.Lease!, ct).ConfigureAwait(false);

            if (valid.IsFailure)
            {
                Revoke(registration);

                return valid.Error;
            }

            lock (_sync)
            {
                if (!IsLive(registration) || registration.Consumed)
                {
                    return Refused;
                }

                registration.Consumed = true;

                registration.Child = new(root, childId, taskDigest);

                return registration.Child;
            }
        }
        finally
        {
            if (proof is not null)
            {
                await proof.DisposeAsync().ConfigureAwait(false);
            }

            _admission.Release();
        }
    }

    public Task<Result<MemoryDelegationEffect>> AdmitEffectAsync(MemoryDelegationChild child, Guid childId, CovenantDigest taskDigest, CancellationToken ct = default) =>
        child.ChildId != childId || child.TaskDigest != taskDigest || !ReferenceEquals(child.Root.Registration.Child, child)
            ? Task.FromResult(Result<MemoryDelegationEffect>.Failure(Refused))
            : AdmitEffectCoreAsync(child.Root, child, ct);

    public Task<Result<MemoryDelegationEffect>> AdmitEffectAsync(MemoryDelegationRoot root, CancellationToken ct = default) =>
        AdmitEffectCoreAsync(root, null, ct);

    private async Task<Result<MemoryDelegationEffect>> AdmitEffectCoreAsync(MemoryDelegationRoot root, MemoryDelegationChild? child, CancellationToken ct)
    {
        if (!ReferenceEquals(root.Owner, this))
        {
            return Refused;
        }

        await _admission.WaitAsync(ct).ConfigureAwait(false);

        MemoryDelegationEffect? proof = null;

        bool admitted = false;

        try
        {
            Registration registration = root.Registration;

            lock (_sync)
            {
                if (!IsLive(registration) || !registration.Consumed || child?.Completed == true)
                {
                    return Refused;
                }

                RegisterEffect(registration);

                if (child is not null && child.Effects++ == 0)
                {
                    child.Drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                proof = new MemoryDelegationEffect(root, child);
            }

            Result valid = await registration.Reader!.ValidateAsync(registration.Snapshot!, registration.Lease!, ct).ConfigureAwait(false);

            if (valid.IsFailure)
            {
                Revoke(registration);

                return valid.Error;
            }

            lock (_sync)
            {
                if (!IsLive(registration) || child?.Completed == true)
                {
                    return Refused;
                }

                admitted = true;

                return proof;
            }
        }
        finally
        {
            if (!admitted && proof is not null)
            {
                await proof.DisposeAsync().ConfigureAwait(false);
            }

            _admission.Release();
        }
    }

    public async ValueTask<MemoryDelegationMutation> BeginMutationAsync(CancellationToken ct = default)
    {
        await _admission.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            Task drain = RevokeAll();

            await drain.WaitAsync(ct).ConfigureAwait(false);

            return new MemoryDelegationMutation(this);
        }
        catch
        {
            _admission.Release();

            throw;
        }
    }

    internal void EndMutation() => _admission.Release();

    /// <summary>Called by the existing global admission spine before its stage-one drain.</summary>
    public Task CloseForMaintenance()
    {
        lock (_sync)
        {
            _maintenanceClosed = true;
        }

        return RevokeAll(asynchronousCancellation: true);
    }

    public void ReopenAfterMaintenance()
    {
        lock (_sync)
        {
            _maintenanceClosed = false;

            _generation++;
        }
    }

    private Task RevokeAll(bool asynchronousCancellation = false)
    {
        Registration[] registrations;

        Task[] drains;

        lock (_sync)
        {
            _generation++;

            registrations = [.. _live];

            foreach (Registration registration in registrations)
            {
                registration.Terminal = true;
            }

            drains = registrations.Select(registration => registration.Drained.Task).Concat(_preparing.Select(preparation => preparation.Task)).ToArray();

            foreach (Registration registration in registrations)
            {
                ReleaseSlotIfDrained(registration);
            }
        }

        foreach (Registration registration in registrations)
        {
            if (asynchronousCancellation)
            {
                _ = registration.Cancellation.CancelAsync();
            }
            else
            {
                Cancel(registration);
            }
        }

        return Task.WhenAll(drains);
    }

    private bool IsLive(Registration registration) => !_maintenanceClosed && !registration.Terminal && _live.Contains(registration)
        && !registration.Cancellation.IsCancellationRequested && timeProvider.GetUtcNow() < registration.ExpiresAtUtc;

    private void Revoke(Registration registration)
    {
        lock (_sync)
        {
            registration.Terminal = true;

            ReleaseSlotIfDrained(registration);
        }

        Cancel(registration);
    }

    private static void Cancel(Registration registration)
    {
        try
        {
            registration.Cancellation.Cancel();
        }
        catch (AggregateException)
        {
            // Untrusted cancellation callbacks cannot reopen a terminal capability.
        }
    }

    private void ReleaseSlotIfDrained(Registration registration)
    {
        if (registration.Terminal && registration.Effects == 0)
        {
            _live.Remove(registration);

            registration.Snapshot = null;

            registration.Reader = null;

            registration.Lease = null;

            registration.Timer?.Dispose();

            registration.ParentCancellation.Unregister();

            registration.LeaseCancellation.Unregister();

            registration.LeaseCompletion.Unregister();
        }
    }

    private static void RegisterEffect(Registration registration)
    {
        if (registration.Effects++ == 0)
        {
            registration.Drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    internal void ReleaseEffect(MemoryDelegationRoot root, MemoryDelegationChild? child)
    {
        lock (_sync)
        {
            Registration registration = root.Registration;

            if (child is not null && --child.Effects == 0)
            {
                child.Drained.TrySetResult();
            }

            if (--registration.Effects == 0)
            {
                registration.Drained.TrySetResult();

                ReleaseSlotIfDrained(registration);
            }
        }
    }

    internal async ValueTask CompleteChildAsync(MemoryDelegationChild child)
    {
        Task drain;

        lock (_sync)
        {
            child.Completed = true;

            drain = child.Drained.Task;
        }

        try
        {
            child.Cancellation.Cancel();
        }
        catch (AggregateException)
        {
            // A consumer callback cannot revive the completed child.
        }

        await drain.ConfigureAwait(false);
    }

    internal async ValueTask CompleteAsync(MemoryDelegationRoot root)
    {
        Registration registration = root.Registration;

        Revoke(registration);

        Task drain;

        lock (_sync)
        {
            drain = registration.Drained.Task;
        }

        await drain.ConfigureAwait(false);

        if (registration.Child is { } child)
        {
            await CompleteChildAsync(child).ConfigureAwait(false);
        }

        lock (_sync)
        {
            if (_roots.TryGetValue(registration.RootId, out Registration? current) && ReferenceEquals(current, registration))
            {
                _roots.Remove(registration.RootId);
            }
        }

        registration.Timer?.Dispose();

        await registration.ParentCancellation.DisposeAsync().ConfigureAwait(false);

        await registration.LeaseCancellation.DisposeAsync().ConfigureAwait(false);

        await registration.LeaseCompletion.DisposeAsync().ConfigureAwait(false);
    }

    internal sealed class Registration(Guid rootId, ArcanumInvocationContext parent, CovenantTurnLease lease, IMemoryDelegationSourceReader reader,
        MemoryEvaluationSourceSnapshot snapshot, DateTimeOffset expiresAtUtc)
    {
        internal Guid RootId { get; } = rootId;

        internal ArcanumInvocationContext Parent { get; } = parent;

        internal CovenantTurnLease? Lease = lease;

        internal IMemoryDelegationSourceReader? Reader = reader;

        internal MemoryEvaluationSourceSnapshot? Snapshot = snapshot;

        internal DateTimeOffset ExpiresAtUtc { get; } = expiresAtUtc;

        internal CancellationTokenSource Cancellation { get; } = new();

        internal CancellationTokenRegistration ParentCancellation;

        internal CancellationTokenRegistration LeaseCancellation;

        internal CancellationTokenRegistration LeaseCompletion;

        internal ITimer? Timer;

        internal bool Terminal;

        internal bool Consumed;

        internal int Effects;

        internal MemoryDelegationChild? Child;

        internal TaskCompletionSource Drained = Completed();

        private static TaskCompletionSource Completed()
        {
            TaskCompletionSource signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

            signal.SetResult();

            return signal;
        }
    }
}
