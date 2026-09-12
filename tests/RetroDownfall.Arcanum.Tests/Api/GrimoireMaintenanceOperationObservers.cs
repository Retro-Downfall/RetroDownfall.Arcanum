using System.Collections.Concurrent;

using System.Collections.Immutable;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Infrastructure.Operations;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Backup;

namespace RetroDownfall.Arcanum.Tests.Api;

internal sealed class MaintenanceAdoptionObservation
{
    private int _calls;

    private int _pauses;

    internal bool PauseAfterAcquisition { get; set; }

    internal MaintenanceCheckpoint Checkpoint { get; } = new();

    internal int Calls => Volatile.Read(ref _calls);

    internal int Pauses => Volatile.Read(ref _pauses);

    internal LongRunningOperationLeaseResult? Result { get; private set; }

    internal async Task ObserveAsync(LongRunningOperationLeaseResult result, CancellationToken cancellationToken)
    {
        Result = result;

        Interlocked.Increment(ref _calls);

        if (result.Acquired && PauseAfterAcquisition)
        {
            Interlocked.Increment(ref _pauses);

            await Checkpoint.PauseAsync(cancellationToken);
        }
    }
}

internal sealed class PausingMaintenanceLeaseAdoption(
    LongRunningOperationStore inner,
    MaintenanceAdoptionObservation observation) : ILongRunningOperationMaintenanceLeaseAdoption
{
    internal LongRunningOperationStore Inner => inner;

    public async Task<LongRunningOperationLeaseResult> AdoptUnderInstallationLockAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        LongRunningOperationRecoveryFingerprint expected,
        string ownerId,
        DateTimeOffset utcNow,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default)
    {
        LongRunningOperationLeaseResult result = await ((ILongRunningOperationMaintenanceLeaseAdoption)inner)
            .AdoptUnderInstallationLockAsync(heldInstallationLock, guardedDirectory, expected, ownerId,
                utcNow, leaseExpiresAt, cancellationToken);

        await observation.ObserveAsync(result, cancellationToken);

        return result;
    }
}

internal sealed record RecordedMaintenanceCheckpoint(
    Guid OperationId,
    string OwnerId,
    int ExpectedVersion,
    int Version,
    ImmutableArray<byte>? Payload,
    string? Reference,
    string PublicSummary,
    DateTimeOffset UtcNow);

internal sealed record RecordedMaintenanceTransition(
    string Method,
    Guid OperationId,
    long ExpectedRevision,
    string? OwnerId,
    LongRunningOperationState State,
    DateTimeOffset UtcNow,
    string? TerminalErrorCode);

internal sealed class MaintenanceOperationObservations
{
    private readonly ConcurrentQueue<RecordedMaintenanceCheckpoint> _checkpoints = new();

    private readonly ConcurrentQueue<RecordedMaintenanceTransition> _transitions = new();

    internal IReadOnlyList<RecordedMaintenanceCheckpoint> Checkpoints => _checkpoints.ToArray();

    internal IReadOnlyList<RecordedMaintenanceTransition> Transitions => _transitions.ToArray();

    internal void Record(RecordedMaintenanceCheckpoint checkpoint) => _checkpoints.Enqueue(checkpoint);

    internal void Record(RecordedMaintenanceTransition transition) => _transitions.Enqueue(transition);
}

// This scoped decorator never performs an extra read. Closed-period assertions consume only
// immutable successful call snapshots; the concrete store and its other authorities remain intact.
internal sealed class RecordingLongRunningOperationStore(
    LongRunningOperationStore inner,
    MaintenanceOperationObservations observations) : ILongRunningOperationStore
{
    internal LongRunningOperationStore Inner => inner;

    public Task<LongRunningOperation> CreateAsync(LongRunningOperationCreateRequest request, CancellationToken cancellationToken = default) =>
        inner.CreateAsync(request, cancellationToken);

    public Task<LongRunningOperationRequestIdentityResult> ResolveOrCreateAsync(
        LongRunningOperationCreateRequest request,
        LongRunningOperationRequestIdentity identity,
        CancellationToken cancellationToken = default) =>
        inner.ResolveOrCreateAsync(request, identity, cancellationToken);

    public Task<LongRunningOperation?> TryStartSingleFlightAsync(
        LongRunningOperationCreateRequest request,
        string ownerId,
        DateTimeOffset utcNow,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default) =>
        inner.TryStartSingleFlightAsync(request, ownerId, utcNow, leaseExpiresAt, cancellationToken);

    public Task<LongRunningOperation?> GetAsync(Guid operationId, CancellationToken cancellationToken = default) =>
        inner.GetAsync(operationId, cancellationToken);

    public Task<LongRunningOperationRequestIdentity?> FindRequestIdentityAsync(Guid operationId, CancellationToken cancellationToken = default) =>
        inner.FindRequestIdentityAsync(operationId, cancellationToken);

    public Task<LongRunningOperationRequestIdentityMatch?> FindByRequestedOperationIdAsync(Guid requestedOperationId, CancellationToken cancellationToken = default) =>
        inner.FindByRequestedOperationIdAsync(requestedOperationId, cancellationToken);

    public Task<IReadOnlyList<LongRunningOperation>> ListAsync(LongRunningOperationQuery query, CancellationToken cancellationToken = default) =>
        inner.ListAsync(query, cancellationToken);

    public Task<IReadOnlyList<LongRunningOperation>> FindExpiredAsync(DateTimeOffset utcNow, int limit, CancellationToken cancellationToken = default) =>
        inner.FindExpiredAsync(utcNow, limit, cancellationToken);

    public Task<LongRunningOperationLeaseResult> TryAcquireLeaseAsync(
        Guid operationId,
        string ownerId,
        DateTimeOffset utcNow,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default) =>
        inner.TryAcquireLeaseAsync(operationId, ownerId, utcNow, leaseExpiresAt, cancellationToken);

    public Task<bool> HeartbeatAsync(
        Guid operationId,
        string ownerId,
        DateTimeOffset utcNow,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default) =>
        inner.HeartbeatAsync(operationId, ownerId, utcNow, leaseExpiresAt, cancellationToken);

    public Task<bool> RenewLeaseAsync(
        Guid operationId,
        string ownerId,
        DateTimeOffset utcNow,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default) =>
        inner.RenewLeaseAsync(operationId, ownerId, utcNow, leaseExpiresAt, cancellationToken);

    public async Task<bool> SaveCheckpointAsync(
        Guid operationId,
        string ownerId,
        int expectedCheckpointVersion,
        int checkpointVersion,
        byte[]? checkpointPayload,
        string? checkpointReference,
        string publicSummary,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default)
    {
        RecordedMaintenanceCheckpoint snapshot = new(operationId, ownerId, expectedCheckpointVersion,
            checkpointVersion, checkpointPayload is null ? null : ImmutableArray.CreateRange(checkpointPayload),
            checkpointReference, publicSummary, utcNow);

        bool result = await inner.SaveCheckpointAsync(operationId, ownerId, expectedCheckpointVersion,
            checkpointVersion, checkpointPayload, checkpointReference, publicSummary, utcNow, cancellationToken);

        if (result)
        {
            observations.Record(snapshot);
        }

        return result;
    }

    public async Task<bool> TryTransitionAsync(
        Guid operationId,
        long expectedRevision,
        string? ownerId,
        LongRunningOperationState state,
        DateTimeOffset utcNow,
        string? terminalErrorCode = null,
        CancellationToken cancellationToken = default)
    {
        bool result = await inner.TryTransitionAsync(operationId, expectedRevision, ownerId, state, utcNow, terminalErrorCode, cancellationToken);

        if (result)
        {
            observations.Record(new RecordedMaintenanceTransition(nameof(TryTransitionAsync), operationId,
                expectedRevision, ownerId, state, utcNow, terminalErrorCode));
        }

        return result;
    }

    public async Task<bool> RequestCancellationAsync(Guid operationId, long expectedRevision, DateTimeOffset utcNow, CancellationToken cancellationToken = default)
    {
        bool result = await inner.RequestCancellationAsync(operationId, expectedRevision, utcNow, cancellationToken);

        if (result)
        {
            observations.Record(new RecordedMaintenanceTransition(nameof(RequestCancellationAsync), operationId,
                expectedRevision, null, LongRunningOperationState.Cancelling, utcNow, null));
        }

        return result;
    }

    public async Task<bool> ResetForRetryAsync(Guid operationId, long expectedRevision, DateTimeOffset utcNow, CancellationToken cancellationToken = default)
    {
        bool result = await inner.ResetForRetryAsync(operationId, expectedRevision, utcNow, cancellationToken);

        if (result)
        {
            observations.Record(new RecordedMaintenanceTransition(nameof(ResetForRetryAsync), operationId,
                expectedRevision, null, LongRunningOperationState.Pending, utcNow, null));
        }

        return result;
    }

    public Task<IReadOnlyList<LongRunningOperationCount>> GetCountsAsync(CancellationToken cancellationToken = default) =>
        inner.GetCountsAsync(cancellationToken);
}
