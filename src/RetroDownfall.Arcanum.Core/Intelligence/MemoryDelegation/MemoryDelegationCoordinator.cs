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

    private readonly HashSet<Preparation> _preparing = [];

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

        DateTimeOffset preparedAt = timeProvider.GetUtcNow();

        CancellationToken leaseRevocation;

        try
        {
            leaseRevocation = lease.Revocation;
        }
        catch (ObjectDisposedException)
        {
            return Refused;
        }

        using CancellationTokenSource deadlineCancellation = new(MemoryDelegationLimits.MaximumLifetime, timeProvider);

        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.LifetimeEnded, leaseRevocation, deadlineCancellation.Token);

        Preparation preparation = new(cancellation);

        bool admitted = false;

        try
        {
            await _admission.WaitAsync(cancellation.Token).ConfigureAwait(false);

            admitted = true;

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

            Result<MemoryEvaluationSourceSnapshot> read = await reader.ReadAsync(grant, parent, lease, cancellation.Token).ConfigureAwait(false);

            cancellation.Token.ThrowIfCancellationRequested();

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

                registration.DeadlineCancellation = new CancellationTokenSource(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, timeProvider);

                registration.DeadlineRegistration = registration.DeadlineCancellation.Token.Register(() => Revoke(registration));
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
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            ct.ThrowIfCancellationRequested();

            return Refused;
        }
        finally
        {
            lock (_sync)
            {
                preparation.Drained.TrySetResult();

                _preparing.Remove(preparation);
            }

            if (admitted) _admission.Release();
        }
    }

    public async Task<Result<MemoryDelegationChild>> ConsumeAsync(MemoryDelegationRoot root, Guid rootId, Guid childId, CovenantDigest taskDigest, CancellationToken ct = default)
    {
        if (!ReferenceEquals(root.Owner, this) || root.Registration.RootId != rootId || childId == Guid.Empty || !taskDigest.IsValid)
        {
            return Refused;
        }

        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, root.Revocation);

        MemoryDelegationEffect? proof = null;

        bool ownsAdmission = false;

        try
        {
            await _admission.WaitAsync(cancellation.Token).ConfigureAwait(false);

            ownsAdmission = true;

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

            Result valid = await registration.Reader!.ValidateAsync(registration.Snapshot!, registration.Lease!, cancellation.Token).ConfigureAwait(false);

            cancellation.Token.ThrowIfCancellationRequested();

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
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            ct.ThrowIfCancellationRequested();

            return Refused;
        }
        finally
        {
            if (proof is not null)
            {
                await proof.DisposeAsync().ConfigureAwait(false);
            }

            if (ownsAdmission) _admission.Release();
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

        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, child?.Revocation ?? root.Revocation);

        MemoryDelegationEffect? proof = null;

        bool ownsAdmission = false;

        bool admitted = false;

        try
        {
            await _admission.WaitAsync(cancellation.Token).ConfigureAwait(false);

            ownsAdmission = true;

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

            Result valid = await registration.Reader!.ValidateAsync(registration.Snapshot!, registration.Lease!, cancellation.Token).ConfigureAwait(false);

            cancellation.Token.ThrowIfCancellationRequested();

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
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            ct.ThrowIfCancellationRequested();

            return Refused;
        }
        finally
        {
            if (!admitted && proof is not null)
            {
                await proof.DisposeAsync().ConfigureAwait(false);
            }

            if (ownsAdmission) _admission.Release();
        }
    }

    public async ValueTask<MemoryDelegationMutation> BeginMutationAsync(CancellationToken ct = default)
    {
        await _admission.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            Task drain = Task.WhenAll(RevokeAll());

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

    /// <summary>Closes admission synchronously and snapshots the physical lifetimes the global stage-one drain must join.</summary>
    public Task[] CloseForMaintenance()
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

    private Task[] RevokeAll(bool asynchronousCancellation = false)
    {
        Registration[] registrations;

        Preparation[] preparations;

        Task[] drains;

        lock (_sync)
        {
            _generation++;

            registrations = [.. _live];

            preparations = [.. _preparing];

            foreach (Registration registration in registrations)
            {
                registration.Terminal = true;
            }

            drains = registrations.Select(registration => registration.Drained.Task).Concat(preparations.Select(preparation => preparation.Drained.Task)).ToArray();

            foreach (Registration registration in registrations)
            {
                ReleaseSlotIfDrained(registration);
            }
        }

        foreach (Preparation preparation in preparations)
        {
            // Global closure may hold its gate lock. Schedule callbacks, but retain the
            // drain until ReadAsync has physically unwound in PrepareAsync's finally.
            try
            {
                _ = preparation.Cancellation.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
                // Preparation already completed and disposed its linked cancellation.
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

        return drains;
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

            // Stop the deadline without joining a callback under _sync. The root owns
            // the registrations until CompleteAsync joins them outside the lock.
            registration.DeadlineCancellation?.Dispose();
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

        registration.DeadlineCancellation?.Dispose();

        await registration.ParentCancellation.DisposeAsync().ConfigureAwait(false);

        await registration.LeaseCancellation.DisposeAsync().ConfigureAwait(false);

        await registration.LeaseCompletion.DisposeAsync().ConfigureAwait(false);

        await registration.DeadlineRegistration.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class Preparation(CancellationTokenSource cancellation)
    {
        internal CancellationTokenSource Cancellation { get; } = cancellation;

        internal TaskCompletionSource Drained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal sealed class Registration(Guid rootId, ArcanumInvocationContext parent, CovenantTurnLease lease, IMemoryDelegationSourceReader reader,
        MemoryEvaluationSourceSnapshot snapshot, DateTimeOffset expiresAtUtc)
    {
        internal Guid RootId { get; } = rootId;

        internal ArcanumInvocationContext Parent { get; } = parent;

        internal CovenantTurnLease? Lease = lease;

        internal IMemoryDelegationSourceReader? Reader = reader;

        internal MemoryEvaluationSourceSnapshot? Snapshot = snapshot;

        internal ProviderCallSensitivity Sensitivity { get; } = SourceSensitivity(snapshot);

        private static ProviderCallSensitivity SourceSensitivity(MemoryEvaluationSourceSnapshot source)
        {
            ContentSensitivity level = source.Coordinates.Where(c => c.Role is MemoryEvaluationCoordinateRole.Intervention)
                .Aggregate(ContentSensitivity.None, (current, coordinate) => ContentSensitivityAlgebra.Maximum(current, coordinate.Sensitivity));

            return ProviderCallSensitivities.Create(level, level is ContentSensitivity.None
                ? GenerationProvenance.CreateExact([])
                : source.Provenance ?? GenerationProvenance.CreateExact([source.Fence!.DatasetGeneration]));
        }

        internal DateTimeOffset ExpiresAtUtc { get; } = expiresAtUtc;

        internal CancellationTokenSource Cancellation { get; } = new();

        internal CancellationTokenRegistration ParentCancellation;

        internal CancellationTokenRegistration LeaseCancellation;

        internal CancellationTokenRegistration LeaseCompletion;

        internal CancellationTokenSource? DeadlineCancellation;

        internal CancellationTokenRegistration DeadlineRegistration;

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
