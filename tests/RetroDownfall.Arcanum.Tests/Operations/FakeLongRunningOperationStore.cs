using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Operations;

/// <summary>
/// In-memory <see cref="ILongRunningOperationStore"/> with the same compare-and-swap semantics the
/// SQL store enforces, so reconciler behaviour can be exercised without a Grimoire.
/// </summary>
internal sealed class FakeLongRunningOperationStore(TimeProvider timeProvider) : ILongRunningOperationStore
{
    private readonly ConcurrentDictionary<Guid, LongRunningOperation> _operations = new();

    private readonly Lock _gate = new();

    private readonly Dictionary<Guid, LongRunningOperationRequestIdentity> _requestIdentities = [];

    private int _requestIdentityLookupCount;

    private int _listCallCount;

    private readonly ConcurrentQueue<LeaseAcquisition> _leaseAcquisitions = new();

    internal Func<LongRunningOperation?, LongRunningOperation?>? GetOverride { get; set; }

    internal Func<LongRunningOperation?, bool?>? TryTransitionOverride { get; set; }

    public IReadOnlyCollection<LongRunningOperation> Operations => [.. _operations.Values];

    /// <summary>How many paging round-trips callers have made, for tests that assert a lookup is cheap.</summary>
    public int ListCallCount => Volatile.Read(ref _listCallCount);

    /// <summary>
    /// Every <see cref="TryAcquireLeaseAsync"/> call, pairing the clock reading observed at the moment of
    /// the call with the arguments the caller supplied — so a test can assert a lease was stamped from a
    /// current timestamp rather than one captured before a long pass began.
    /// </summary>
    public IReadOnlyList<LeaseAcquisition> LeaseAcquisitions => [.. _leaseAcquisitions];

    internal readonly record struct LeaseAcquisition(
        DateTimeOffset ObservedNow,
        DateTimeOffset SuppliedUtcNow,
        DateTimeOffset SuppliedExpiresAt);

    public LongRunningOperation Seed(
        string kind,
        LongRunningOperationRecoveryPolicy policy,
        LongRunningOperationState state = LongRunningOperationState.Running,
        int checkpointVersion = 0,
        DateTimeOffset? leaseExpiresAt = null,
        Guid? budgetReservationId = null,
        Guid? inferenceRunId = null,
        Guid? runId = null)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();

        LongRunningOperation operation = new(
            Guid.NewGuid(),
            kind,
            state,
            policy,
            RootOperationId: null,
            ParentOperationId: null,
            SessionId: null,
            RunId: runId,
            InferenceRunId: inferenceRunId,
            BudgetReservationId: budgetReservationId,
            IdempotencyClaimId: null,
            CreatedAt: now,
            StartedAt: now,
            HeartbeatAt: now,
            CompletedAt: null,
            LeaseOwner: "dead-process",
            LeaseExpiresAt: leaseExpiresAt ?? now.AddMinutes(-5),
            AttemptCount: 1,
            CheckpointVersion: checkpointVersion,
            CheckpointPayload: null,
            CheckpointReference: null,
            PublicSummary: $"Seeded {kind}.",
            TerminalErrorCode: null,
            Revision: 1);

        _operations[operation.Id] = operation;

        return operation;
    }

    internal void Add(LongRunningOperation operation)
    {

        ArgumentNullException.ThrowIfNull(operation);

        _operations[operation.Id] = operation;

    }

    public Task<LongRunningOperation> CreateAsync(
        LongRunningOperationCreateRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Seed(request.Kind, request.RecoveryPolicy, LongRunningOperationState.Pending));

    /// <summary>
    /// Mirrors the SQL store: one name maps to one operation forever, the same digest replays it, and
    /// a different digest under the same name creates nothing.
    /// </summary>
    public Task<LongRunningOperationRequestIdentityResult> ResolveOrCreateAsync(
        LongRunningOperationCreateRequest request,
        LongRunningOperationRequestIdentity identity,
        CancellationToken cancellationToken = default)
    {

        lock (_gate)
        {

            // Keyed by the durable operation id, because that is the direction
            // FindRequestIdentityAsync reads. The requested name is still unique, so resolving it
            // is one scan over a map that never holds more than a handful of rows in a test.
            if (_requestIdentities.FirstOrDefault(
                    pair => pair.Value.RequestedOperationId == identity.RequestedOperationId)
                is { Value: not null } existing)
            {

                return Task.FromResult(
                    existing.Value.ApplyRequestDigest == identity.ApplyRequestDigest
                        ? new LongRunningOperationRequestIdentityResult(
                            LongRunningOperationRequestIdentityOutcome.Replayed,
                            _operations[existing.Key])
                        : new LongRunningOperationRequestIdentityResult(
                            LongRunningOperationRequestIdentityOutcome.DigestConflict,
                            Operation: null));

            }

            LongRunningOperation created = Seed(
                request.Kind,
                request.RecoveryPolicy,
                LongRunningOperationState.Pending);

            _requestIdentities[created.Id] = identity;

            return Task.FromResult(
                new LongRunningOperationRequestIdentityResult(
                    LongRunningOperationRequestIdentityOutcome.Created,
                    created));

        }

    }

    public Task<LongRunningOperation?> TryStartSingleFlightAsync(
        LongRunningOperationCreateRequest request,
        string ownerId,
        DateTimeOffset utcNow,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<LongRunningOperation?>(Seed(request.Kind, request.RecoveryPolicy));

    public Task<LongRunningOperation?> GetAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        LongRunningOperation? operation =
            _operations.TryGetValue(operationId, out LongRunningOperation? current) ? current : null;

        return Task.FromResult(
            GetOverride is { } readOverride
                ? readOverride(operation)
                : operation);
    }

    /// <summary>
    /// How many times a caller asked for a request-identity row, so a test can prove the all-null
    /// requested arm never reads one that was never written.
    /// </summary>
    public int RequestIdentityLookupCount => Volatile.Read(ref _requestIdentityLookupCount);

    public Task<LongRunningOperationRequestIdentity?> FindRequestIdentityAsync(
        Guid operationId,
        CancellationToken cancellationToken = default)
    {

        Interlocked.Increment(ref _requestIdentityLookupCount);

        lock (_gate)
        {

            return Task.FromResult(
                _requestIdentities.TryGetValue(
                    operationId,
                    out LongRunningOperationRequestIdentity? identity)
                    ? identity
                    : null);

        }

    }

    public Task<LongRunningOperationRequestIdentityMatch?> FindByRequestedOperationIdAsync(
        Guid requestedOperationId,
        CancellationToken cancellationToken = default)
    {

        if (requestedOperationId == Guid.Empty)
        {

            throw new ArgumentException(
                "A requested operation identity cannot be empty.",
                nameof(requestedOperationId));

        }

        lock (_gate)
        {

            KeyValuePair<Guid, LongRunningOperationRequestIdentity> match =
                _requestIdentities.FirstOrDefault(
                    pair => pair.Value.RequestedOperationId == requestedOperationId);

            return Task.FromResult<LongRunningOperationRequestIdentityMatch?>(
                match.Value is null
                    ? null
                    : new LongRunningOperationRequestIdentityMatch(
                        _operations[match.Key],
                        match.Value));

        }

    }

    public Task<IReadOnlyList<LongRunningOperation>> ListAsync(
        LongRunningOperationQuery query,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _listCallCount);

        return Task.FromResult<IReadOnlyList<LongRunningOperation>>([.. _operations.Values]);
    }

    public Task<IReadOnlyList<LongRunningOperation>> FindExpiredAsync(
        DateTimeOffset utcNow,
        int limit,
        CancellationToken cancellationToken = default)
    {
        LongRunningOperation[] expired =
        [
            .. _operations.Values
                .Where(operation => operation.State is LongRunningOperationState.Running
                    or LongRunningOperationState.Waiting
                    || IsRecoverableAttention(operation))
                .Where(operation => operation.LeaseExpiresAt is null || operation.LeaseExpiresAt <= utcNow)
                .OrderBy(static operation => operation.CreatedAt)
                .Take(limit),
        ];

        return Task.FromResult<IReadOnlyList<LongRunningOperation>>(expired);
    }

    public Task<LongRunningOperationLeaseResult> TryAcquireLeaseAsync(
        Guid operationId,
        string ownerId,
        DateTimeOffset utcNow,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default)
    {
        _leaseAcquisitions.Enqueue(new LeaseAcquisition(timeProvider.GetUtcNow(), utcNow, leaseExpiresAt));

        lock (_gate)
        {
            if (!_operations.TryGetValue(operationId, out LongRunningOperation? current))
            {
                throw new InvalidOperationException($"Operation {operationId} is not seeded.");
            }

            bool claimable = current.State is LongRunningOperationState.Pending
                || (current.State is LongRunningOperationState.Running or LongRunningOperationState.Waiting
                    && (current.LeaseExpiresAt is null || current.LeaseExpiresAt <= utcNow))
                || (IsRecoverableAttention(current)
                    && (current.LeaseExpiresAt is null || current.LeaseExpiresAt <= utcNow));

            if (!claimable)
            {
                return Task.FromResult(new LongRunningOperationLeaseResult(false, current));
            }

            LongRunningOperation leased = current with
            {
                State = LongRunningOperationState.Running,
                LeaseOwner = ownerId,
                LeaseExpiresAt = leaseExpiresAt,
                AttemptCount = current.AttemptCount + 1,
                Revision = current.Revision + 1,
            };

            _operations[operationId] = leased;

            return Task.FromResult(new LongRunningOperationLeaseResult(true, leased));
        }
    }

    public Task<bool> HeartbeatAsync(
        Guid operationId,
        string ownerId,
        DateTimeOffset utcNow,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(true);

    public Task<bool> SaveCheckpointAsync(
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
        lock (_gate)
        {
            if (!_operations.TryGetValue(operationId, out LongRunningOperation? current)
                || current.CheckpointVersion != expectedCheckpointVersion
                || current.LeaseOwner != ownerId)
            {
                return Task.FromResult(false);
            }

            _operations[operationId] = current with
            {
                CheckpointVersion = checkpointVersion,
                CheckpointPayload = checkpointPayload,
                CheckpointReference = checkpointReference,
                PublicSummary = publicSummary,
                Revision = current.Revision + 1,
            };

            return Task.FromResult(true);
        }
    }

    public Task<bool> TryTransitionAsync(
        Guid operationId,
        long expectedRevision,
        string? ownerId,
        LongRunningOperationState state,
        DateTimeOffset utcNow,
        string? terminalErrorCode = null,
        CancellationToken cancellationToken = default)
    {
        LongRunningOperation? observed =
            _operations.TryGetValue(operationId, out LongRunningOperation? value) ? value : null;

        if (TryTransitionOverride?.Invoke(observed) is { } overridden)
        {
            return Task.FromResult(overridden);
        }

        lock (_gate)
        {
            if (!_operations.TryGetValue(operationId, out LongRunningOperation? current)
                || current.Revision != expectedRevision
                || (ownerId is not null
                    && !string.Equals(current.LeaseOwner, ownerId, StringComparison.Ordinal)))
            {
                return Task.FromResult(false);
            }

            bool releasesLease = state is LongRunningOperationState.Completed
                or LongRunningOperationState.Failed
                or LongRunningOperationState.Abandoned
                or LongRunningOperationState.ReconciliationRequired;

            bool completed = state is LongRunningOperationState.Completed
                or LongRunningOperationState.Failed
                or LongRunningOperationState.Abandoned;

            _operations[operationId] = current with
            {
                State = state,
                TerminalErrorCode = terminalErrorCode,
                CompletedAt = completed ? utcNow : null,
                LeaseOwner = releasesLease ? null : current.LeaseOwner,
                LeaseExpiresAt = releasesLease ? null : current.LeaseExpiresAt,
                Revision = current.Revision + 1,
            };

            return Task.FromResult(true);
        }
    }

    public Task<bool> RequestCancellationAsync(
        Guid operationId,
        long expectedRevision,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default) =>
        TryTransitionAsync(
            operationId,
            expectedRevision,
            ownerId: null,
            LongRunningOperationState.Cancelling,
            utcNow,
            cancellationToken: cancellationToken);

    public Task<bool> ResetForRetryAsync(
        Guid operationId,
        long expectedRevision,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default) =>
        TryTransitionAsync(
            operationId,
            expectedRevision,
            ownerId: null,
            LongRunningOperationState.Pending,
            utcNow,
            cancellationToken: cancellationToken);

    public Task<IReadOnlyList<LongRunningOperationCount>> GetCountsAsync(CancellationToken cancellationToken = default)
    {
        LongRunningOperationCount[] counts =
        [
            .. _operations.Values
                .GroupBy(static operation => (operation.Kind, operation.State))
                .Select(static group => new LongRunningOperationCount(group.Key.Kind, group.Key.State, group.LongCount())),
        ];

        return Task.FromResult<IReadOnlyList<LongRunningOperationCount>>(counts);
    }

    private static bool IsRecoverableAttention(LongRunningOperation operation) =>
        operation.State == LongRunningOperationState.ReconciliationRequired
        && ((operation.Kind is LongRunningOperationKinds.DataRetentionPrune
                    or LongRunningOperationKinds.DataRetentionMutation
                    or LongRunningOperationKinds.DataRetentionFactoryReset
                && string.Equals(
                    operation.TerminalErrorCode,
                    ErrorCodes.Data.ReconciliationFailed,
                    StringComparison.Ordinal))
            || (operation.Kind is LongRunningOperationKinds.DataRetentionMutation
                    or LongRunningOperationKinds.DataRetentionFactoryReset
                && string.Equals(
                    operation.TerminalErrorCode,
                    ErrorCodes.Covenant.MaintenanceFailed,
                    StringComparison.Ordinal)));
}

/// <summary>Recording handler that lets a test choose the recovery outcome per kind.</summary>
internal sealed class RecordingRecoveryHandler(
    string kind,
    int supportedCheckpointVersion,
    Func<LongRunningOperation, LongRunningOperationRecoveryResult>? outcome = null)
    : ILongRunningOperationRecoveryHandler
{
    private readonly ConcurrentQueue<Guid> _invocations = new();

    public string Kind => kind;

    public int SupportedCheckpointVersion => supportedCheckpointVersion;

    public IReadOnlyCollection<Guid> Invocations => [.. _invocations];

    public Task<LongRunningOperationRecoveryResult> RecoverAsync(
        LongRunningOperation operation,
        CancellationToken cancellationToken)
    {
        _invocations.Enqueue(operation.Id);

        return Task.FromResult(
            outcome?.Invoke(operation) ?? LongRunningOperationRecoveryResult.Completed());
    }
}

/// <summary>
/// The reconciler's outer scope may only page the expiry query. Every per-operation call throws so a
/// regression that shares one store — and therefore one SQLite connection — across concurrent
/// recovery workers fails the test instead of racing an unsynchronized command list.
/// </summary>
internal sealed class PagingOnlyOperationStore(ILongRunningOperationStore inner) : ILongRunningOperationStore
{
    public Task<IReadOnlyList<LongRunningOperation>> FindExpiredAsync(
        DateTimeOffset utcNow,
        int limit,
        CancellationToken cancellationToken = default) =>
        inner.FindExpiredAsync(utcNow, limit, cancellationToken);

    public Task<LongRunningOperation> CreateAsync(
        LongRunningOperationCreateRequest request,
        CancellationToken cancellationToken = default) =>
        throw OutsideItsScope();

    public Task<LongRunningOperationRequestIdentityResult> ResolveOrCreateAsync(
        LongRunningOperationCreateRequest request,
        LongRunningOperationRequestIdentity identity,
        CancellationToken cancellationToken = default) =>
        throw OutsideItsScope();

    public Task<LongRunningOperation?> TryStartSingleFlightAsync(
        LongRunningOperationCreateRequest request,
        string ownerId,
        DateTimeOffset utcNow,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default) =>
        throw OutsideItsScope();

    public Task<LongRunningOperation?> GetAsync(
        Guid operationId,
        CancellationToken cancellationToken = default) =>
        throw OutsideItsScope();

    public Task<LongRunningOperationRequestIdentity?> FindRequestIdentityAsync(
        Guid operationId,
        CancellationToken cancellationToken = default) =>
        throw OutsideItsScope();

    public Task<LongRunningOperationRequestIdentityMatch?> FindByRequestedOperationIdAsync(
        Guid requestedOperationId,
        CancellationToken cancellationToken = default) =>
        throw OutsideItsScope();

    public Task<IReadOnlyList<LongRunningOperation>> ListAsync(
        LongRunningOperationQuery query,
        CancellationToken cancellationToken = default) =>
        throw OutsideItsScope();

    public Task<LongRunningOperationLeaseResult> TryAcquireLeaseAsync(
        Guid operationId,
        string ownerId,
        DateTimeOffset utcNow,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default) =>
        throw OutsideItsScope();

    public Task<bool> HeartbeatAsync(
        Guid operationId,
        string ownerId,
        DateTimeOffset utcNow,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default) =>
        throw OutsideItsScope();

    public Task<bool> SaveCheckpointAsync(
        Guid operationId,
        string ownerId,
        int expectedCheckpointVersion,
        int checkpointVersion,
        byte[]? checkpointPayload,
        string? checkpointReference,
        string publicSummary,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default) =>
        throw OutsideItsScope();

    public Task<bool> TryTransitionAsync(
        Guid operationId,
        long expectedRevision,
        string? ownerId,
        LongRunningOperationState state,
        DateTimeOffset utcNow,
        string? terminalErrorCode = null,
        CancellationToken cancellationToken = default) =>
        throw OutsideItsScope();

    public Task<bool> RequestCancellationAsync(
        Guid operationId,
        long expectedRevision,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default) =>
        throw OutsideItsScope();

    public Task<bool> ResetForRetryAsync(
        Guid operationId,
        long expectedRevision,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default) =>
        throw OutsideItsScope();

    public Task<IReadOnlyList<LongRunningOperationCount>> GetCountsAsync(
        CancellationToken cancellationToken = default) =>
        throw OutsideItsScope();

    private static InvalidOperationException OutsideItsScope() =>
        new("Per-operation recovery must run in its own DI scope, not the reconciler's own scope.");
}

/// <summary>
/// Hands each recovered operation its own scope, and counts them so a test can assert the fan-out
/// really is one scope per operation rather than one shared connection.
/// </summary>
internal sealed class RecordingServiceScopeFactory(
    ILongRunningOperationStore store,
    params ILongRunningOperationRecoveryHandler[] handlers) : IServiceScopeFactory
{
    private readonly ILongRunningOperationStore _store = store;

    private readonly ILongRunningOperationRecoveryHandler[] _handlers = handlers;

    private int _created;

    private int _disposed;

    public int Created => Volatile.Read(ref _created);

    public int Disposed => Volatile.Read(ref _disposed);

    public IServiceScope CreateScope()
    {
        _ = Interlocked.Increment(ref _created);

        return new Scope(this);
    }

    private sealed class Scope(RecordingServiceScopeFactory owner) : IServiceScope, IServiceProvider
    {
        public IServiceProvider ServiceProvider => this;

        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(ILongRunningOperationStore))
            {
                return owner._store;
            }

            return serviceType == typeof(IEnumerable<ILongRunningOperationRecoveryHandler>)
                ? owner._handlers
                : null;
        }

        public void Dispose() => Interlocked.Increment(ref owner._disposed);
    }
}
