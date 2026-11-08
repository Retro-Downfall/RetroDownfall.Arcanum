using System.Collections.Immutable;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Infrastructure.Workspaces;

internal interface IWorkspaceMutationFence
{
    /// <summary>Durable ownership outside issued writer leases cannot be disproved by registration disposal.</summary>
    bool RequiresRetirementHold => false;

    ValueTask<Result<IWorkspaceMutationLease>> AdmitAsync(string workspaceRoot, ImmutableArray<GuildPathScope> scopes, CancellationToken cancellationToken);
}

internal interface IWorkspaceMutationLease : IAsyncDisposable
{
    ValueTask<Result> ValidateAsync(bool rollback, CancellationToken cancellationToken);

    ValueTask<Result> RetainRecoveryAsync(string reasonCode, CancellationToken cancellationToken);

    ValueTask<Result> ValidateDirectoryCreationAsync(WorkspaceLeaseDirectoryCreation preparation, bool rollback, CancellationToken cancellationToken);

    ValueTask<Result> AdoptCreatedDirectoryAsync(WorkspaceLeaseCreatedDirectory proof, bool rollback, CancellationToken cancellationToken);

    ValueTask<Result> ValidateDirectoryRemovalAsync(WorkspaceLeaseDirectoryRemoval preparation, bool rollback, CancellationToken cancellationToken);

    ValueTask<Result> AdoptRemovedDirectoryAsync(WorkspaceLeaseRemovedDirectory proof, bool rollback, CancellationToken cancellationToken);

    IDisposable Enter();
}

/// <summary>Process-wide dispatch beneath physical writers. Registered participants own authenticated applicability and fail-closed policy.</summary>
internal static class WorkspaceMutationFence
{
    private static readonly object RegistrationGate = new();

    private static RegistryState _state = new();

    /// <summary>Acquisition and protection installation share one gate; disposal never proves child extinction.</summary>
    internal static bool TryBeginOpaqueEffect(out OpaqueEffect? effect)
    {
        lock (RegistrationGate)
        {
            if (!_state.Participants.IsEmpty)
            {
                effect = null;

                return false;
            }

            _state.ActiveOpaqueEffects++;

            effect = new OpaqueEffect(_state);

            return true;
        }
    }

    /// <summary>Serialized tests model a fresh host without resetting the production singleton registry.</summary>
    internal static IDisposable IsolateProcessHistoryForTests()
    {
        lock (RegistrationGate)
        {
            if (!_state.Participants.IsEmpty || _state.ActiveOpaqueEffects != 0 || _state.ActiveManagedLeases != 0)
            {
                throw new InvalidOperationException("An isolated test registry requires no installed fence or in-flight effects.");
            }

            RegistryState previous = _state;

            _state = new RegistryState();

            return new IsolatedRegistry(previous, _state);
        }
    }

    internal static bool IsInstalled
    {
        get
        {
            lock (RegistrationGate)
            {
                return !_state.Participants.IsEmpty;
            }
        }
    }

    internal static Registration Install(IWorkspaceMutationFence participant)
    {
        ArgumentNullException.ThrowIfNull(participant);

        lock (RegistrationGate)
        {
            if (_state.ActiveOpaqueEffects != 0 || _state.ActiveManagedLeases != 0 || _state.Ambiguous)
            {
                throw new InvalidOperationException(WorkspaceProcessBoundary.Message);
            }

            Registration registration = new(_state, participant);

            _state.Participants = _state.Participants.Add(registration);

            return registration;
        }
    }

    internal static async ValueTask<Result<IWorkspaceMutationLease>> AdmitAsync(string workspaceRoot, ImmutableArray<GuildPathScope> scopes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        RegistryState state;

        ImmutableArray<Registration> participants;

        lock (RegistrationGate)
        {
            state = _state;

            participants = state.Participants;

            if (participants.Any(participant => participant.Retired))
            {
                return new Error(ErrorCodes.Guild.LeaseNotHeld, "The installed workspace fence is draining or awaiting reconciliation.");
            }

            state.ActiveManagedLeases++;

            foreach (Registration participant in participants)
            {
                participant.ActiveLeases++;
            }
        }

        List<IWorkspaceMutationLease> leases = [];

        bool transferred = false;

        try
        {
            foreach (Registration participant in participants)
            {
                Result<IWorkspaceMutationLease> admitted = await participant.Participant.AdmitAsync(workspaceRoot, scopes, cancellationToken).ConfigureAwait(false);

                if (admitted.IsFailure)
                {
                    return admitted.Error;
                }

                leases.Add(admitted.Value);
            }

            CompositeLease result = new(state, participants, leases.ToArray());

            transferred = true;

            return result;
        }
        finally
        {
            if (!transferred)
            {
                await DisposeAndReleaseAsync(state, participants, leases).ConfigureAwait(false);
            }
        }
    }

    private static void MarkUncertain(RegistryState state, ImmutableArray<Registration> participants)
    {
        lock (RegistrationGate)
        {
            // Even a previously unprotected managed operation must not be hidden by later installation.
            state.Ambiguous = true;

            foreach (Registration participant in participants)
            {
                participant.Uncertain = true;
            }
        }
    }

    private static async Task DisposeAndReleaseAsync(RegistryState state, ImmutableArray<Registration> participants,
        IReadOnlyList<IWorkspaceMutationLease> leases)
    {
        List<Exception>? failures = null;

        try
        {
            for (int index = leases.Count - 1; index >= 0; index--)
            {
                try
                {
                    await leases[index].DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    (failures ??= []).Add(exception);

                    MarkUncertain(state, participants);
                }
            }
        }
        finally
        {
            lock (RegistrationGate)
            {
                state.ActiveManagedLeases--;

                foreach (Registration participant in participants)
                {
                    participant.ActiveLeases--;

                    participant.RemoveWhenDrained();
                }
            }
        }

        if (failures is not null)
        {
            throw new AggregateException("Workspace mutation leases could not be fully drained.", failures);
        }
    }

    internal sealed class RegistryState
    {
        internal ImmutableArray<Registration> Participants = [];

        internal int ActiveOpaqueEffects;

        internal int ActiveManagedLeases;

        internal bool Ambiguous;
    }

    internal sealed class OpaqueEffect(RegistryState state) : IDisposable
    {
        private bool _disposed;

        internal void MarkDispatched()
        {
            lock (RegistrationGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);

                state.Ambiguous = true;
            }
        }

        public void Dispose()
        {
            lock (RegistrationGate)
            {
                if (!_disposed)
                {
                    _disposed = true;

                    state.ActiveOpaqueEffects--;
                }
            }
        }
    }

    private sealed class IsolatedRegistry(RegistryState previous, RegistryState isolated) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            lock (RegistrationGate)
            {
                if (_disposed)
                {
                    return;
                }

                if (!ReferenceEquals(_state, isolated) || isolated.ActiveOpaqueEffects != 0 || isolated.ActiveManagedLeases != 0
                    || isolated.Participants.Any(participant => !participant.Retired || participant.ActiveLeases != 0))
                {
                    throw new InvalidOperationException("The isolated test registry still owns a live fence or in-flight effect.");
                }

                // Retired uncertainty tombstones belong to this synthetic host, never to the production singleton.
                _state = previous;

                _disposed = true;
            }
        }
    }

    internal sealed class Registration(RegistryState state, IWorkspaceMutationFence participant) : IDisposable
    {
        internal IWorkspaceMutationFence Participant { get; } = participant;

        internal int ActiveLeases;

        internal bool Retired;

        internal bool Uncertain;

        public void Dispose()
        {
            lock (RegistrationGate)
            {
                Retired = true;

                RemoveWhenDrained();
            }
        }

        internal void RemoveWhenDrained()
        {
            if (Retired && ActiveLeases == 0 && !Uncertain && !Participant.RequiresRetirementHold)
            {
                state.Participants = state.Participants.Remove(this);
            }
        }
    }

    private sealed class CompositeLease(RegistryState state, ImmutableArray<Registration> participants,
        IWorkspaceMutationLease[] leases) : IWorkspaceMutationLease
    {
        private readonly object _gate = new();

        private Task? _disposal;

        private int _disposed;

        public async ValueTask<Result> ValidateAsync(bool rollback, CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return new Error(ErrorCodes.Guild.LeaseNotHeld, "The physical mutation lease has been disposed.");
            }

            foreach (IWorkspaceMutationLease lease in leases)
            {
                Result valid = await lease.ValidateAsync(rollback, cancellationToken).ConfigureAwait(false);

                if (valid.IsFailure)
                {
                    return valid;
                }
            }

            return Volatile.Read(ref _disposed) == 0
                ? Result.Success()
                : new Error(ErrorCodes.Guild.LeaseNotHeld, "The physical mutation lease has been disposed.");
        }

        public ValueTask<Result> ValidateDirectoryCreationAsync(WorkspaceLeaseDirectoryCreation preparation, bool rollback, CancellationToken cancellationToken) =>
            ForwardAsync(lease => lease.ValidateDirectoryCreationAsync(preparation, rollback, cancellationToken));

        public ValueTask<Result> AdoptCreatedDirectoryAsync(WorkspaceLeaseCreatedDirectory proof, bool rollback, CancellationToken cancellationToken) =>
            AdoptAllAsync(lease => lease.AdoptCreatedDirectoryAsync(proof, rollback, cancellationToken));

        public ValueTask<Result> ValidateDirectoryRemovalAsync(WorkspaceLeaseDirectoryRemoval preparation, bool rollback, CancellationToken cancellationToken) =>
            ForwardAsync(lease => lease.ValidateDirectoryRemovalAsync(preparation, rollback, cancellationToken));

        public ValueTask<Result> AdoptRemovedDirectoryAsync(WorkspaceLeaseRemovedDirectory proof, bool rollback, CancellationToken cancellationToken) =>
            AdoptAllAsync(lease => lease.AdoptRemovedDirectoryAsync(proof, rollback, cancellationToken));

        private async ValueTask<Result> AdoptAllAsync(Func<IWorkspaceMutationLease, ValueTask<Result>> operation)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return new Error(ErrorCodes.Guild.LeaseNotHeld, "The physical mutation lease has been disposed.");
            }

            Error? failure = null;

            foreach (IWorkspaceMutationLease lease in leases)
            {
                try
                {
                    Result adopted = await operation(lease).ConfigureAwait(false);

                    if (adopted.IsFailure) failure ??= adopted.Error;
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    failure ??= new(ErrorCodes.Guild.WorkspaceIdentityUnavailable,
                        "An affected participant could not observe the completed physical effect.");
                }
            }

            if (Volatile.Read(ref _disposed) != 0)
                failure ??= new(ErrorCodes.Guild.LeaseNotHeld, "The physical mutation lease has been disposed.");

            if (failure is not null) MarkUncertain(state, participants);

            return failure is null ? Result.Success() : failure;
        }

        private async ValueTask<Result> ForwardAsync(Func<IWorkspaceMutationLease, ValueTask<Result>> operation)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return new Error(ErrorCodes.Guild.LeaseNotHeld, "The physical mutation lease has been disposed.");
            }

            foreach (IWorkspaceMutationLease lease in leases)
            {
                Result result = await operation(lease).ConfigureAwait(false);

                if (result.IsFailure)
                {
                    return result;
                }
            }

            return Volatile.Read(ref _disposed) == 0
                ? Result.Success()
                : new Error(ErrorCodes.Guild.LeaseNotHeld, "The physical mutation lease has been disposed.");
        }

        public async ValueTask<Result> RetainRecoveryAsync(string reasonCode, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_disposed != 0)
                {
                    return new Error(ErrorCodes.Guild.LeaseNotHeld, "The physical mutation lease has been disposed.");
                }

                MarkUncertain(state, participants);
            }

            Error? firstFailure = null;

            foreach (IWorkspaceMutationLease lease in leases)
            {
                try
                {
                    Result retained = await lease.RetainRecoveryAsync(reasonCode, cancellationToken).ConfigureAwait(false);

                    if (retained.IsFailure)
                    {
                        firstFailure ??= retained.Error;
                    }
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    firstFailure ??= new(ErrorCodes.Guild.WorkspaceIdentityUnavailable,
                        "The physical mutation recovery hold could not be persisted.");
                }
            }

            return firstFailure is null ? Result.Success() : firstFailure;
        }

        public IDisposable Enter()
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed != 0, this);

                List<IDisposable> entered = [];

                try
                {
                    foreach (IWorkspaceMutationLease lease in leases)
                    {
                        entered.Add(lease.Enter());
                    }

                    return new CompositeScope(entered.ToArray());
                }
                catch (Exception entryFailure)
                {
                    try
                    {
                        DisposeScopes(entered);
                    }
                    catch (Exception cleanupFailure)
                    {
                        throw new AggregateException(entryFailure, cleanupFailure);
                    }

                    throw;
                }
            }
        }

        public ValueTask DisposeAsync()
        {
            lock (_gate)
            {
                _disposed = 1;

                return new ValueTask(_disposal ??= DisposeAndReleaseAsync(state, participants, leases));
            }
        }
    }

    private static void DisposeScopes(IReadOnlyList<IDisposable> scopes)
    {
        List<Exception>? failures = null;

        for (int index = scopes.Count - 1; index >= 0; index--)
        {
            try
            {
                scopes[index].Dispose();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                (failures ??= []).Add(exception);
            }
        }

        if (failures is not null)
        {
            throw new AggregateException("Workspace mutation scopes could not be fully unwound.", failures);
        }
    }

    private sealed class CompositeScope(IDisposable[] scopes) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            DisposeScopes(scopes);
        }
    }
}
