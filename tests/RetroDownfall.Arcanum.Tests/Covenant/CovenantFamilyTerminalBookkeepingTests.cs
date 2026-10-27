using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Tests.Support;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Data;

namespace RetroDownfall.Arcanum.Tests.Covenant;

[Collection("Grimoire")]
public sealed partial class CovenantFamilyTerminalBookkeepingTests(GrimoireFixture fixture) : IAsyncLifetime
{
    private string _path = string.Empty;

    private ArcanumDbContext? _db;

    public Task InitializeAsync()
    {
        _path = fixture.CopyDatabase();

        _db = fixture.CreateContext(_path);

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

        if (File.Exists(_path)) File.Delete(_path);
    }

    [SkippableFact]
    public async Task Family_native_start_obtains_a_supported_durable_owned_lease()
    {
        RequireNative();

        using NativeHarness harness = new(_db!);

        LongRunningOperation operation = await harness.StartAsync();

        LongRunningOperation current = Assert.IsType<LongRunningOperation>(await harness.Store.GetAsync(operation.Id));

        Assert.Equal(LongRunningOperationState.Running, current.State);

        Assert.Equal(NativeHarness.Owner, current.LeaseOwner);

        Assert.True(current.LeaseExpiresAt > current.CreatedAt);

        Assert.NotNull(current.HeartbeatAt);

        Assert.True(current.LeaseExpiresAt - current.HeartbeatAt <= TimeSpan.FromMinutes(15));
    }

    [SkippableFact]
    public async Task Family_native_start_and_run_durably_complete_the_owned_operation()
    {
        RequireNative();

        using NativeHarness harness = new(_db!);

        LongRunningOperation operation = await harness.StartAsync();

        Result<CovenantExclusiveLeaseDisposition> result = await harness.Coordinator.RunAsync(
            operation, Checkpoint(operation.Id), NativeHarness.Owner, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(CovenantExclusiveLeaseDisposition.CommitAndReopen, result.Value);

        LongRunningOperation current = Assert.IsType<LongRunningOperation>(await harness.Store.GetAsync(operation.Id));

        Assert.Equal(LongRunningOperationState.Completed, current.State);

        Assert.NotNull(current.CompletedAt);

        Assert.True(current.Revision > operation.Revision);

        Assert.Null(current.LeaseOwner);
    }

    [SkippableFact]
    public async Task Cancellation_after_publication_cannot_abandon_terminal_bookkeeping()
    {
        RequireNative();

        using NativeHarness harness = new(_db!);

        using CancellationTokenSource caller = new();

        harness.Transition.OnPublish = caller.Cancel;

        LongRunningOperation operation = await harness.StartAsync();

        Result<CovenantExclusiveLeaseDisposition> result = await harness.Coordinator.RunAsync(
            operation, Checkpoint(operation.Id), NativeHarness.Owner, caller.Token);

        Assert.True(caller.IsCancellationRequested);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(1, harness.Operations.CompleteCalls);

        Assert.False(harness.Operations.CompletionToken.CanBeCanceled);

        Assert.Equal(LongRunningOperationState.Completed, (await harness.Store.GetAsync(operation.Id))!.State);
    }

    [SkippableTheory]
    [InlineData("publish")]
    [InlineData("writer")]
    [InlineData("gate")]
    public async Task An_unfinished_suffix_never_terminalizes_the_operation(string refused)
    {
        RequireNative();

        using NativeHarness harness = new(_db!);

        harness.Transition.Refused = refused;

        LongRunningOperation operation = await harness.StartAsync();

        Result<CovenantExclusiveLeaseDisposition> result = await harness.Coordinator.RunAsync(
            operation, Checkpoint(operation.Id), NativeHarness.Owner, CancellationToken.None);

        if (refused == "gate") Assert.True(result.IsFailure);
        else Assert.Equal(CovenantExclusiveLeaseDisposition.KeepClosed, result.Value);

        Assert.Equal(0, harness.Operations.CompleteCalls);

        Assert.Equal(LongRunningOperationState.Running, (await harness.Store.GetAsync(operation.Id))!.State);

        Result<CovenantInstallationReadLease> read = await harness.Gate.AcquireInstallationReadAsync(CancellationToken.None);

        Assert.True(read.IsFailure);
    }

    [SkippableTheory]
    [InlineData("refused")]
    [InlineData("exception")]
    [InlineData("successor-before-read")]
    [InlineData("successor-after-read")]
    [InlineData("revision-after-read")]
    [InlineData("missing")]
    public async Task Unconfirmed_bookkeeping_discloses_committed_effects_and_preserves_the_durable_owner(string fault)
    {
        RequireNative();

        using NativeHarness harness = new(_db!);

        LongRunningOperation operation = await harness.StartAsync();

        LongRunningOperation? protectedRow = null;

        async Task ChangeOwnerAsync()
        {
            DateTimeOffset future = DateTimeOffset.UtcNow.AddMinutes(16);

            LongRunningOperationLeaseResult taken = await harness.Store.TryAcquireLeaseAsync(
                operation.Id, "successor-owner", future, future.AddMinutes(5));

            Assert.NotNull(taken.Operation);

            Assert.Equal("successor-owner", taken.Operation.LeaseOwner);

            protectedRow = taken.Operation;
        }

        if (fault == "refused") harness.Operations.Refuse = true;
        if (fault == "exception") harness.Operations.Throw = true;
        if (fault == "successor-before-read") harness.Transition.AfterWriter = ChangeOwnerAsync;
        if (fault == "successor-after-read") harness.Operations.BeforeComplete = ChangeOwnerAsync;
        if (fault == "revision-after-read") harness.Operations.BeforeComplete = async () =>
        {
            Assert.True(await harness.Operations.Inner.HeartbeatAsync(operation.Id, NativeHarness.Owner,
                TimeSpan.FromMinutes(15)));

            protectedRow = await harness.Store.GetAsync(operation.Id);
        };
        if (fault == "missing") harness.Transition.AfterWriter = async () =>
        {
            await _db!.Database.OpenConnectionAsync();

            await using var command = _db.Database.GetDbConnection().CreateCommand();

            command.CommandText = "DELETE FROM LongRunningOperations WHERE Id = @id";

            command.Parameters.Add(new SqliteParameter("@id", operation.Id.ToString("N")));

            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        };

        Result<CovenantExclusiveLeaseDisposition> result = await harness.Coordinator.RunAsync(
            operation, Checkpoint(operation.Id), NativeHarness.Owner, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, result.Error.Code);

        Assert.Contains("generation is committed and admission is reopened", result.Error.Message);

        Assert.Contains(operation.Id.ToString("D"), result.Error.Message);

        Assert.Contains("completion is unconfirmed", result.Error.Message);

        Assert.DoesNotContain("private-bookkeeping", result.Error.Message);

        Assert.Equal(fault == "missing" ? 0 : 1, harness.Operations.CompleteCalls);

        Assert.Equal(fault != "missing", harness.Operations.ReachedDisposition);

        if (fault != "missing") Assert.Equal(NativeHarness.Owner, harness.Operations.CompletionOwner);

        if (fault is "successor-before-read" or "successor-after-read" or "revision-after-read") Assert.NotNull(protectedRow);

        LongRunningOperation? current = await harness.Store.GetAsync(operation.Id);

        if (fault == "missing") Assert.Null(current);
        else
        {
            Assert.Equal(LongRunningOperationState.Running, current!.State);

            Assert.Null(current.CompletedAt);

            if (protectedRow is not null)
            {
                Assert.Equal(protectedRow.Revision, current.Revision);

                Assert.Equal(protectedRow.LeaseOwner, current.LeaseOwner);
            }
        }

        Result<CovenantInstallationReadLease> read = await harness.Gate.AcquireInstallationReadAsync(CancellationToken.None);

        Assert.True(read.IsSuccess);

        await read.Value.DisposeAsync();
    }

    private static CovenantFamilyReinitializeCheckpointV1 Checkpoint(Guid id) =>
        new(1, id, "6F1C0B2E-9A44-4E1D-8B7A-2C5D3F6A8E90", 1,
            CovenantOperationGateFixture.Digest(1).ToString(), CovenantOperationGateFixture.Digest(2).ToString(),
            CovenantOperationGateFixture.Digest(3).ToString(), CovenantOperationGateFixture.DatasetGeneration,
            null, CovenantFamilyReinitializePhase.Planned, 0, false, false, false, null, 0, null);

    private static void RequireNative() =>
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

    private sealed class NativeHarness : IDisposable
    {
        internal const string Owner = "family-terminal-owner";

        internal LongRunningOperationStore Store { get; }

        internal CovenantFamilyReinitializeCoordinator Coordinator { get; }

        internal CovenantOperationGate Gate { get; } = CovenantOperationGateFixture.CreateGate();

        internal SuccessfulTransition Transition { get; } = new();

        internal RecordingOperations Operations { get; }

        internal TestCapturingLogger<CovenantFamilyReinitializeCoordinator> Logger { get; } = new();

        internal NativeHarness(ArcanumDbContext db)
        {
            Store = new(db, TestOrdinaryConnectionFactory.For(db));

            Operations = new(new LongRunningOperationCoordinator(Store, TimeProvider.System), Gate, Transition, Store);

            Coordinator = new(new CovenantRequestedOperationStarter(Operations), Operations, Store,
                Gate, new EmptyKernel(), new EmptyKernel(), new EmptySource(), Transition, TimeProvider.System, Logger);
        }

        internal async Task<LongRunningOperation> StartAsync()
        {
            Result<LongRunningOperationRequestIdentityResult> result = await Coordinator.StartAsync(
                Guid.NewGuid(), CovenantOperationGateFixture.Digest(1), CovenantOperationGateFixture.Digest(3),
                Owner, CancellationToken.None);

            Assert.True(result.IsSuccess, result.Error.Message);

            Assert.Equal(LongRunningOperationRequestIdentityOutcome.Created, result.Value.Outcome);

            return Assert.IsType<LongRunningOperation>(result.Value.Operation);
        }

        public void Dispose() => Store.Dispose();
    }

    private sealed class EmptySource : ICovenantReinitializeErasureSource
    {
        public Task<Result<CovenantReinitializeErasureWork>> EnumerateAsync(Guid operationId, Guid generation, CancellationToken cancellationToken) =>
            Task.FromResult(Result<CovenantReinitializeErasureWork>.Success(new([], [])));
    }

    private sealed class EmptyKernel : ICovenantProtectedArtifactErasureKernel, ICovenantManagedFileErasureKernel
    {
        public ValueTask<Result<CovenantArtifactErasureProgress>> ErasePageAsync(CovenantProtectedArtifactErasurePage page, CovenantArtifactErasureAuthority authority, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("An empty native fixture has no protected artifact page.");

        public ValueTask<Result<CovenantArtifactErasureProgress>> EraseAsync(CovenantManagedFileErasureRequest request, CovenantArtifactErasureAuthority authority, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("An empty native fixture has no managed file.");
    }

    private sealed class SuccessfulTransition : ICovenantFamilyReinitializeTransition
    {
        internal string? Refused { get; set; }

        internal Action? OnPublish { get; set; }

        internal Func<Task>? AfterWriter { get; set; }

        internal bool Published { get; private set; }

        internal bool WriterReopened { get; private set; }

        private ICovenantExclusiveOperationLease? _lease;

        internal string RefusalCode { get; set; } = ErrorCodes.Covenant.MaintenanceFailed;

        public Task<Result> CloseHandlesAsync(CancellationToken token) => Task.FromResult(
            Refused == "handles" ? Result.Failure(new Error(RefusalCode, "private-step-payload")) : Result.Success());

        public Task<Result> ClearEvaluationEvidenceAsync(CancellationToken token) => Task.FromResult(Result.Success());

        public Task<Result> DropFamilyAsync(CancellationToken token) => Task.FromResult(Result.Success());

        public Task<Result<CovenantDigest>> CompactAsync(CancellationToken token) => Task.FromResult(Result<CovenantDigest>.Success(CovenantOperationGateFixture.Digest(4)));

        public Task<Result<Guid>> InstallTiersAsync(CancellationToken token) => Task.FromResult(Result<Guid>.Success(Guid.Parse("88888888-8888-4888-8888-888888888888")));

        public Task<Result> TruncateAndVerifySidecarsAsync(CancellationToken token) => Task.FromResult(Result.Success());

        public Task<Result> VerifyReopenAsync(CancellationToken token) => Task.FromResult(Result.Success());

        public Task<Result> PublishCommittedAsync(ICovenantExclusiveOperationLease lease, Guid generation, CancellationToken token)
        {
            _lease = lease;

            Published = Refused != "publish";

            OnPublish?.Invoke();

            return Task.FromResult(Published ? Result.Success() : Result.Failure(new Error(ErrorCodes.Covenant.MaintenanceFailed, "private-publication")));
        }

        public async Task<Result> ReopenDisclosureWriterAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            Assert.False(token.CanBeCanceled);

            WriterReopened = Refused != "writer";

            if (Refused == "gate") Assert.True((await _lease!.CompleteAsync(CovenantExclusiveLeaseDisposition.KeepClosed, token)).IsSuccess);

            if (AfterWriter is not null) await AfterWriter();

            return WriterReopened ? Result.Success() : Result.Failure(new Error(ErrorCodes.Covenant.MaintenanceFailed, "private-writer"));
        }
    }

    private sealed class RecordingOperations(
        ILongRunningOperationCoordinator inner,
        CovenantOperationGate gate,
        SuccessfulTransition transition,
        LongRunningOperationStore store) : ILongRunningOperationCoordinator
    {
        internal ILongRunningOperationCoordinator Inner => inner;

        internal int CompleteCalls { get; private set; }

        internal CancellationToken CompletionToken { get; private set; }

        internal bool ReachedDisposition { get; private set; }

        internal string? CompletionOwner { get; private set; }

        internal bool RefuseCheckpoint { get; set; }

        internal bool Refuse { get; set; }

        internal bool Throw { get; set; }

        internal Func<Task>? BeforeComplete { get; set; }

        public Task<LongRunningOperationLeaseResult> StartAsync(LongRunningOperationCreateRequest request, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default) =>
            inner.StartAsync(request, ownerId, leaseDuration, cancellationToken);

        public Task<Result<LongRunningOperationRequestIdentityResult>> StartWithRequestIdentityAsync(LongRunningOperationCreateRequest request, LongRunningOperationRequestIdentity identity, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default) =>
            inner.StartWithRequestIdentityAsync(request, identity, ownerId, leaseDuration, cancellationToken);

        public Task<bool> HeartbeatAsync(Guid operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default) =>
            inner.HeartbeatAsync(operationId, ownerId, leaseDuration, cancellationToken);

        public Task<bool> CheckpointAsync(Guid operationId, string ownerId, int expectedCheckpointVersion, int checkpointVersion, byte[]? checkpointPayload, string? checkpointReference, string publicSummary, CancellationToken cancellationToken = default) =>
            RefuseCheckpoint ? Task.FromResult(false) : inner.CheckpointAsync(operationId, ownerId, expectedCheckpointVersion, checkpointVersion, checkpointPayload, checkpointReference, publicSummary, cancellationToken);

        public async Task<bool> CompleteAsync(Guid operationId, string ownerId, long expectedRevision, CancellationToken cancellationToken = default)
        {
            CompleteCalls++;

            CompletionToken = cancellationToken;

            Assert.True(transition.Published);

            Assert.True(transition.WriterReopened);

            CompletionOwner = ownerId;

            Assert.Equal((await store.GetAsync(operationId))!.Revision, expectedRevision);

            Result<CovenantInstallationReadLease> read = await gate.AcquireInstallationReadAsync(CancellationToken.None);

            Assert.True(read.IsSuccess);

            await read.Value.DisposeAsync();

            ReachedDisposition = true;

            if (BeforeComplete is not null) await BeforeComplete();

            if (Throw) throw new IOException("private-bookkeeping-error");

            return !Refuse && await inner.CompleteAsync(operationId, ownerId, expectedRevision, cancellationToken);
        }

        public Task<bool> FailAsync(Guid operationId, string ownerId, long expectedRevision, string errorCode, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Committed publication cannot be rewritten as failed.");
    }
}
