using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data;

[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class LongRunningOperationStoreTests : IAsyncLifetime
{
    private readonly GrimoireFixture _fixture;
    private string _dbPath = string.Empty;
    private ArcanumDbContext? _db;

    public LongRunningOperationStoreTests(GrimoireFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync()
    {
        _dbPath = _fixture.CopyDatabase();
        _db = _fixture.CreateContext(_dbPath);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_db is not null)
        {
            SqliteConnection connection = (SqliteConnection)_db.Database.GetDbConnection();
            await _db.DisposeAsync();
            SqliteConnection.ClearPool(connection);
        }

        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    [SkippableFact]
    public async Task TryAcquireLeaseAsync_OnlyOneWorkerWinsUntilLeaseExpires()
    {
        RequireSqlCipher();
        LongRunningOperationStore store = new(_db!);
        DateTimeOffset now = new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
        LongRunningOperation operation = await CreateAsync(store, now);

        LongRunningOperationLeaseResult first = await store.TryAcquireLeaseAsync(
            operation.Id,
            "worker-a",
            now,
            now.AddMinutes(1));
        LongRunningOperationLeaseResult competing = await store.TryAcquireLeaseAsync(
            operation.Id,
            "worker-b",
            now.AddSeconds(30),
            now.AddMinutes(2));
        LongRunningOperationLeaseResult recovered = await store.TryAcquireLeaseAsync(
            operation.Id,
            "worker-b",
            now.AddMinutes(1),
            now.AddMinutes(2));

        Assert.True(first.Acquired);
        Assert.False(competing.Acquired);
        Assert.True(recovered.Acquired);
        Assert.Equal("worker-b", recovered.Operation.LeaseOwner);
        Assert.Equal(2, recovered.Operation.AttemptCount);
        Assert.Equal(LongRunningOperationState.Running, recovered.Operation.State);
    }

    [SkippableFact]
    public async Task SaveCheckpointAsync_IsMonotonicAndRejectsDuplicateVersion()
    {
        RequireSqlCipher();
        LongRunningOperationStore store = new(_db!);
        DateTimeOffset now = new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
        LongRunningOperation operation = await CreateAsync(store, now);
        _ = await store.TryAcquireLeaseAsync(operation.Id, "worker", now, now.AddMinutes(1));

        bool first = await store.SaveCheckpointAsync(
            operation.Id,
            "worker",
            expectedCheckpointVersion: 0,
            checkpointVersion: 1,
            checkpointPayload: [1, 2, 3],
            checkpointReference: null,
            publicSummary: "Indexed 10 of 50 safe paths.",
            now.AddSeconds(10));
        bool duplicate = await store.SaveCheckpointAsync(
            operation.Id,
            "worker",
            expectedCheckpointVersion: 0,
            checkpointVersion: 1,
            checkpointPayload: [9, 9, 9],
            checkpointReference: null,
            publicSummary: "must not overwrite",
            now.AddSeconds(11));

        LongRunningOperation persisted = Assert.IsType<LongRunningOperation>(
            await store.GetAsync(operation.Id));
        Assert.True(first);
        Assert.False(duplicate);
        Assert.Equal(1, persisted.CheckpointVersion);
        Assert.Equal([1, 2, 3], persisted.CheckpointPayload);
        Assert.Equal("Indexed 10 of 50 safe paths.", persisted.PublicSummary);
    }

    [SkippableFact]
    public async Task RequestCancellationAsync_UsesCompareAndSwapAndIsIdempotent()
    {
        RequireSqlCipher();
        LongRunningOperationStore store = new(_db!);
        DateTimeOffset now = new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
        LongRunningOperation operation = await CreateAsync(store, now);
        LongRunningOperationLeaseResult leased = await store.TryAcquireLeaseAsync(
            operation.Id,
            "worker",
            now,
            now.AddMinutes(1));

        bool first = await store.RequestCancellationAsync(
            operation.Id,
            leased.Operation.Revision,
            now.AddSeconds(5));
        bool staleDuplicate = await store.RequestCancellationAsync(
            operation.Id,
            leased.Operation.Revision,
            now.AddSeconds(6));

        LongRunningOperation persisted = Assert.IsType<LongRunningOperation>(
            await store.GetAsync(operation.Id));
        Assert.True(first);
        Assert.False(staleDuplicate);
        Assert.Equal(LongRunningOperationState.Cancelling, persisted.State);
    }

    [SkippableFact]
    public async Task CreateAsync_PreservesRootParentAndRecoveryLinks()
    {
        RequireSqlCipher();
        LongRunningOperationStore store = new(_db!);
        DateTimeOffset now = new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
        Guid sessionId = Guid.NewGuid();
        Guid runId = Guid.NewGuid();
        Guid reservationId = Guid.NewGuid();
        Guid claimId = Guid.NewGuid();
        LongRunningOperation parent = await CreateAsync(store, now);

        LongRunningOperation child = await store.CreateAsync(new LongRunningOperationCreateRequest(
            Kind: LongRunningOperationKinds.Apprentice,
            RecoveryPolicy: LongRunningOperationRecoveryPolicy.ResumeFromCheckpoint,
            PublicSummary: "Child apprentice recovery.",
            CreatedAt: now.AddSeconds(1),
            RootOperationId: parent.Id,
            ParentOperationId: parent.Id,
            SessionId: sessionId,
            RunId: runId,
            InferenceRunId: runId,
            BudgetReservationId: reservationId,
            IdempotencyClaimId: claimId));

        LongRunningOperation persisted = Assert.IsType<LongRunningOperation>(
            await store.GetAsync(child.Id));
        Assert.Equal(parent.Id, persisted.RootOperationId);
        Assert.Equal(parent.Id, persisted.ParentOperationId);
        Assert.Equal(sessionId, persisted.SessionId);
        Assert.Equal(runId, persisted.RunId);
        Assert.Equal(runId, persisted.InferenceRunId);
        Assert.Equal(reservationId, persisted.BudgetReservationId);
        Assert.Equal(claimId, persisted.IdempotencyClaimId);
    }

    [SkippableFact]
    public async Task ReconcileAsync_ExpiredOperation_IsClaimedOnceAndCompleted()
    {
        RequireSqlCipher();
        LongRunningOperationStore store = new(_db!);
        DateTimeOffset now = new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
        LongRunningOperation operation = await CreateAsync(store, now.AddMinutes(-5));
        _ = await store.TryAcquireLeaseAsync(
            operation.Id,
            "dead-host",
            now.AddMinutes(-5),
            now.AddMinutes(-4));
        CompletingRecoveryHandler handler = new(LongRunningOperationKinds.WorkspaceIndex);
        LongRunningOperationReconciler reconciler = new(
            store,
            [handler],
            TimeProvider.System,
            NullLogger<LongRunningOperationReconciler>.Instance);

        LongRunningOperationReconciliationSummary summary = await reconciler.ReconcileAsync(
            now,
            ownerId: "startup-worker",
            maxOperations: 10,
            maxConcurrency: 2);

        LongRunningOperation persisted = Assert.IsType<LongRunningOperation>(
            await store.GetAsync(operation.Id));
        Assert.Equal(1, summary.Completed);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal(LongRunningOperationState.Completed, persisted.State);
    }

    [SkippableFact]
    public async Task ReconcileAsync_UnsupportedOrCorruptCheckpoint_RequiresOperatorRepair()
    {
        RequireSqlCipher();
        LongRunningOperationStore store = new(_db!);
        DateTimeOffset now = new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
        LongRunningOperation unsupported = await CreateAsync(store, now.AddMinutes(-5));
        _ = await store.TryAcquireLeaseAsync(
            unsupported.Id,
            "dead-host",
            now.AddMinutes(-5),
            now.AddMinutes(-4));
        _ = await store.SaveCheckpointAsync(
            unsupported.Id,
            "dead-host",
            expectedCheckpointVersion: 0,
            checkpointVersion: 7,
            checkpointPayload: [1],
            checkpointReference: null,
            publicSummary: "Safe checkpoint summary.",
            now.AddMinutes(-4));
        ThrowingRecoveryHandler handler = new(LongRunningOperationKinds.WorkspaceIndex, supportedCheckpointVersion: 1);
        LongRunningOperationReconciler reconciler = new(
            store,
            [handler],
            TimeProvider.System,
            NullLogger<LongRunningOperationReconciler>.Instance);

        LongRunningOperationReconciliationSummary unsupportedSummary = await reconciler.ReconcileAsync(
            now,
            ownerId: "startup-worker",
            maxOperations: 10,
            maxConcurrency: 1);
        LongRunningOperation afterUnsupported = Assert.IsType<LongRunningOperation>(
            await store.GetAsync(unsupported.Id));

        Assert.Equal(1, unsupportedSummary.RequiresAttention);
        Assert.Equal(LongRunningOperationState.ReconciliationRequired, afterUnsupported.State);
        Assert.Equal(LongRunningOperationErrorCodes.UnsupportedCheckpointVersion, afterUnsupported.TerminalErrorCode);
        Assert.Equal(0, handler.CallCount);

        LongRunningOperation corrupt = await CreateAsync(store, now.AddMinutes(-3));
        _ = await store.TryAcquireLeaseAsync(
            corrupt.Id,
            "dead-host",
            now.AddMinutes(-3),
            now.AddMinutes(-2));
        ThrowingRecoveryHandler corruptHandler = new(LongRunningOperationKinds.WorkspaceIndex, supportedCheckpointVersion: 0);
        LongRunningOperationReconciler corruptReconciler = new(
            store,
            [corruptHandler],
            TimeProvider.System,
            NullLogger<LongRunningOperationReconciler>.Instance);

        LongRunningOperationReconciliationSummary corruptSummary = await corruptReconciler.ReconcileAsync(
            now,
            ownerId: "startup-worker-2",
            maxOperations: 10,
            maxConcurrency: 1);
        LongRunningOperation afterCorrupt = Assert.IsType<LongRunningOperation>(
            await store.GetAsync(corrupt.Id));

        Assert.Equal(1, corruptSummary.RequiresAttention);
        Assert.Equal(LongRunningOperationState.ReconciliationRequired, afterCorrupt.State);
        Assert.Equal(LongRunningOperationErrorCodes.CorruptCheckpoint, afterCorrupt.TerminalErrorCode);
    }

    [SkippableFact]
    public async Task BudgetReservationRecovery_ReleasesStrandedReservationIdempotently()
    {
        RequireSqlCipher();
        LongRunningOperationStore store = new(_db!);
        Guid reservationId = Guid.NewGuid();
        LongRunningOperation operation = await store.CreateAsync(new LongRunningOperationCreateRequest(
            LongRunningOperationKinds.BudgetReservation,
            LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
            "Release stranded reservation.",
            DateTimeOffset.UtcNow,
            BudgetReservationId: reservationId));
        RecordingBudgetReservationService reservations = new();
        BudgetReservationRecoveryHandler handler = new(reservations);

        LongRunningOperationRecoveryResult first = await handler.RecoverAsync(operation, default);
        LongRunningOperationRecoveryResult duplicate = await handler.RecoverAsync(operation, default);

        Assert.Equal(LongRunningOperationState.Completed, first.State);
        Assert.Equal(LongRunningOperationState.Completed, duplicate.State);
        Assert.Equal([reservationId, reservationId], reservations.Released);
    }

    private static Task<LongRunningOperation> CreateAsync(
        LongRunningOperationStore store,
        DateTimeOffset createdAt) =>
        store.CreateAsync(new LongRunningOperationCreateRequest(
            Kind: LongRunningOperationKinds.WorkspaceIndex,
            RecoveryPolicy: LongRunningOperationRecoveryPolicy.RestartIdempotently,
            PublicSummary: "Indexing workspace.",
            CreatedAt: createdAt));

    private static void RequireSqlCipher() =>
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

    private sealed class CompletingRecoveryHandler(string kind) : ILongRunningOperationRecoveryHandler
    {
        public string Kind => kind;

        public int SupportedCheckpointVersion => int.MaxValue;

        public int CallCount { get; private set; }

        public Task<LongRunningOperationRecoveryResult> RecoverAsync(
            LongRunningOperation operation,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(LongRunningOperationRecoveryResult.Completed());
        }
    }

    private sealed class ThrowingRecoveryHandler(
        string kind,
        int supportedCheckpointVersion) : ILongRunningOperationRecoveryHandler
    {
        public string Kind => kind;

        public int SupportedCheckpointVersion => supportedCheckpointVersion;

        public int CallCount { get; private set; }

        public Task<LongRunningOperationRecoveryResult> RecoverAsync(
            LongRunningOperation operation,
            CancellationToken cancellationToken)
        {
            CallCount++;
            throw new InvalidDataException("checkpoint could not be decoded");
        }
    }

    private sealed class RecordingBudgetReservationService : IBudgetReservationService
    {
        public List<Guid> Released { get; } = [];

        public Task<Result<BudgetReservation>> ReserveAsync(
            BudgetReservationRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result> AdjustAsync(
            Guid reservationId,
            decimal reservedUsd,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ReconcileAsync(
            Guid reservationId,
            decimal actualCostUsd,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ReleaseAsync(
            Guid reservationId,
            CancellationToken cancellationToken = default)
        {
            Released.Add(reservationId);
            return Task.CompletedTask;
        }

        public Task<decimal> GetTodayCommittedSpendAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0m);

        public Task<decimal> GetTodayOutstandingReservationsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0m);

        public Task<int> SweepExpiredAsync(
            DateTimeOffset utcNow,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }
}
