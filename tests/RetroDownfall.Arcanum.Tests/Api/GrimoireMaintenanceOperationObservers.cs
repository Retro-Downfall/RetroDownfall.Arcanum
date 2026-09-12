using System.Collections.Concurrent;

using System.Collections.Immutable;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Infrastructure.Operations;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Tests.Api;

internal sealed record RecordedMaintenancePublication(
    GrimoireOfflineTransitionJournalPublication Raw,
    IGrimoireOfflineTransitionPayload Payload,
    int AcceptedGrimoireDispositions,
    Guid? PublishedDatasetGeneration,
    bool MaintenanceLaneClosed);

internal sealed class MaintenanceJournalObservations
{
    private readonly ConcurrentQueue<RecordedMaintenancePublication> _publications = new();

    private readonly ConcurrentQueue<string> _steps = new();

    internal IReadOnlyList<RecordedMaintenancePublication> Publications => _publications.ToArray();

    internal IReadOnlyList<string> Steps => _steps.ToArray();

    internal Action<string>? AfterStep { get; set; }

    internal CovenantVerifiedCandidateState? VerifiedCandidate { get; set; }

    internal bool PublishedExactCandidate { get; set; }

    internal byte[]? InitialJournalKeyFingerprint { get; set; }

    internal List<int> DispositionsAtRetirementSteps { get; } = [];

    internal void Record(RecordedMaintenancePublication publication) => _publications.Enqueue(publication);

    internal void RecordStep(string step)
    {
        _steps.Enqueue(step);

        AfterStep?.Invoke(step);
    }
}

// Successful publications are copied after the real authenticated write/readback. These callbacks
// observe the production file/credential protocol and never mint a disposition or open a connection.
internal sealed class ObservingMaintenanceJournal : IGrimoireOfflineTransitionJournalStore
{
    private readonly GrimoireOfflineTransitionJournalStore _inner;

    private readonly GrimoireMaintenanceAdmissionObserver _admission;

    private readonly CovenantRuntimeGenerationProvider _runtime;

    private readonly MaintenanceJournalObservations _observations;

    private readonly IOsCredentialStore _credentials;

    internal ObservingMaintenanceJournal(IOsCredentialStore credentials, GrimoireMaintenanceAdmissionObserver admission,
        CovenantRuntimeGenerationProvider runtime, MaintenanceJournalObservations observations)
    {
        _admission = admission;

        _runtime = runtime;

        _observations = observations;

        _credentials = credentials;

        _inner = new GrimoireOfflineTransitionJournalStore(credentials,
            new GrimoireOfflineTransitionJournalFileStore(afterStep: RecordStep),
            new GrimoireOfflineTransitionJournalAnchorStore(credentials, afterStep: RecordStep),
            afterStep: RecordStep);
    }

    private void RecordStep(string step)
    {
        _observations.DispositionsAtRetirementSteps.Add(_admission.ClosedLeases.Sum(lease => lease.Dispositions.Count));

        _observations.RecordStep(step);
    }

    public async Task<Result<GrimoireOfflineTransitionJournalPublication>> BeginAsync(ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory, Guid installationId, Guid operationId, GrimoireOfflineTransitionKind kind,
        byte payloadVersion, ReadOnlyMemory<byte> payloadBytes, CancellationToken cancellationToken) =>
        await ObserveAsync(await _inner.BeginAsync(heldInstallationLock, guardedDirectory, installationId, operationId, kind,
            payloadVersion, payloadBytes, cancellationToken), cancellationToken);

    public async Task<Result<GrimoireOfflineTransitionJournalPublication>> BeginBoundAsync(ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory, Guid installationId, Guid operationId, GrimoireOfflineTransitionKind kind,
        byte payloadVersion, GrimoireOfflineTransitionJournalPayloadFactory payloadFactory, CancellationToken cancellationToken) =>
        await ObserveAsync(await _inner.BeginBoundAsync(heldInstallationLock, guardedDirectory, installationId, operationId, kind,
            payloadVersion, payloadFactory, cancellationToken), cancellationToken);

    public async Task<Result<GrimoireOfflineTransitionJournalPublication>> AdvanceAsync(ArcanumMaintenanceLock heldInstallationLock,
        GrimoireOfflineTransitionJournalPublication current, ReadOnlyMemory<byte> payloadBytes, CancellationToken cancellationToken) =>
        await ObserveAsync(await _inner.AdvanceAsync(heldInstallationLock, current, payloadBytes, cancellationToken), cancellationToken);

    public Task<Result<GrimoireOfflineTransitionJournalRecoveryState>> RecoverAsync(ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory, CancellationToken cancellationToken) =>
        _inner.RecoverAsync(heldInstallationLock, guardedDirectory, cancellationToken);

    public Task<Result> RetireAsync(ArcanumMaintenanceLock heldInstallationLock,
        GrimoireOfflineTransitionJournalPublication terminal, CancellationToken cancellationToken) =>
        _inner.RetireAsync(heldInstallationLock, terminal, cancellationToken);

    private async Task<Result<GrimoireOfflineTransitionJournalPublication>> ObserveAsync(Result<GrimoireOfflineTransitionJournalPublication> result,
        CancellationToken cancellationToken)
    {
        if (result.IsSuccess)
        {
            GrimoireOfflineTransitionJournalPublication publication = result.Value;

            IGrimoireOfflineTransitionPayload payload = GrimoireOfflineTransitionHandlerRegistry.Production.DecodeAuthenticated(
                publication.Envelope.Kind, publication.Envelope.PayloadVersion, publication.PayloadBytes,
                publication.Envelope.OperationId, publication.Envelope.SlotEpoch).Value.Payload;

            _observations.InitialJournalKeyFingerprint ??= JournalKeyFingerprint(_credentials, publication.Location.ProfileNamespace);

            bool laneClosed = _admission.LastMaintenanceLane is { } lane
                && (await lane.RevalidateDurableOwnerAsync((_, _, _) => ValueTask.FromResult(true), cancellationToken)).IsFailure;

            _observations.Record(new(publication with { PayloadBytes = publication.PayloadBytes.ToArray() }, payload,
                _admission.ClosedLeases.Sum(lease => lease.Dispositions.Count), _runtime.Current.Availability.DatasetGeneration, laneClosed));
        }

        return result;
    }

    internal static byte[] JournalKeyFingerprint(IOsCredentialStore credentials, BackupRestoreProfileNamespace profile)
    {
        using var key = new GrimoireOfflineTransitionJournalKeyProvider(credentials).OpenExisting(profile).Value;

        Assert.True(key.TryTakeKey(out byte[]? bytes));

        try
        {
            return System.Security.Cryptography.SHA256.HashData(bytes);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
        }
    }
}

// A forwarding adapter over the actual scoped storage owner. The observation occurs only after
// immutable verification returns and before the exact candidate is passed to real publication.
internal sealed class ObservingMaintenanceTransition(CovenantErasureTransition inner,
    CovenantRuntimeGenerationProvider runtime, MaintenanceJournalObservations observations) : ICovenantErasureTransition
{
    public Task<Result<Guid>> ApplyCanonicalErasureAsync(CovenantExclusiveOperation operation,
        CovenantCanonicalDatasetTransition dataset, CovenantClosedPeriodAuthority authority, CancellationToken token) =>
        inner.ApplyCanonicalErasureAsync(operation, dataset, authority, token);

    public Task<Result> CloseHandlesAsync(CovenantClosedPeriodAuthority authority, CancellationToken token) => inner.CloseHandlesAsync(authority, token);

    public Task<Result> TruncateWalAsync(CovenantClosedPeriodAuthority authority, CancellationToken token) => inner.TruncateWalAsync(authority, token);

    public Task<Result<bool>> CompactAsync(CovenantClosedPeriodAuthority authority, CancellationToken token) => inner.CompactAsync(authority, token);

    public Task<Result<CovenantDigest>> StageCandidateAsync(CovenantClosedPeriodAuthority authority, CancellationToken token) => inner.StageCandidateAsync(authority, token);

    public Task<Result<CovenantDigest>> ProveStagedCandidateAsync(CovenantClosedPeriodAuthority authority, CovenantDigest identity, CancellationToken token) => inner.ProveStagedCandidateAsync(authority, identity, token);

    public Task<Result> InstallCompactionReplacementAsync(CovenantClosedPeriodAuthority authority, CovenantDigest identity, CovenantDigest content, CovenantDigest destination, CancellationToken token) => inner.InstallCompactionReplacementAsync(authority, identity, content, destination, token);

    public Task<Result<CovenantDigest>> ReadCanonicalIdentityAsync(CovenantClosedPeriodAuthority authority, CancellationToken token) => inner.ReadCanonicalIdentityAsync(authority, token);

    public Task<Result> InitializeAcceleratorAsync(CovenantClosedPeriodAuthority authority, CancellationToken token) => inner.InitializeAcceleratorAsync(authority, token);

    public Task<Result> VerifySidecarAbsenceAsync(CovenantClosedPeriodAuthority authority, CancellationToken token) => inner.VerifySidecarAbsenceAsync(authority, token);

    public async Task<Result<CovenantVerifiedCandidateState>> VerifyReopenAsync(CovenantClosedPeriodAuthority authority, CancellationToken token)
    {
        var before = runtime.Current;

        var result = await inner.VerifyReopenAsync(authority, token);

        if (result.IsSuccess)
        {
            Assert.Same(before, runtime.Current);

            observations.VerifiedCandidate = result.Value;

            observations.RecordStep("transition:verified");
        }

        return result;
    }

    public async Task<Result> PublishCommittedAsync(ICovenantExclusiveOperationLease lease, CovenantVerifiedCandidateState candidate, CancellationToken token)
    {
        observations.PublishedExactCandidate = ReferenceEquals(observations.VerifiedCandidate, candidate);

        var result = await inner.PublishCommittedAsync(lease, candidate, token);

        if (result.IsSuccess)
        {
            observations.RecordStep("transition:published");
        }

        return result;
    }
}

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
