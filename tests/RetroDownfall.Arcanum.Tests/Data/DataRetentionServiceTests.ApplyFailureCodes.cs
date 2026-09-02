using Microsoft.AspNetCore.Http;

using RetroDownfall.Arcanum.Api.Primitives;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Operations;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

/// <summary>
/// What an apply failure tells a client it may do next.
/// </summary>
/// <remarks>
/// One code for every unhappy ending is one code too few. "The rows are gone and the operation row
/// could not be closed" and "the rows are gone and quarantined bytes are still on disk" are opposite
/// instructions — retry the first, do not touch the second — and a client reading a single
/// <c>Data.ReconciliationFailed</c> off both cannot tell them apart, nor either from "this failed and
/// somebody has to look". The distinguishing detail survives in the message text, so an operator
/// reading prose was never blind; a programmatic client was.
/// </remarks>
public sealed partial class DataRetentionServiceTests
{

    /// <summary>
    /// A prune that deleted its rows but could not close its operation row is not the same failure
    /// as a prune whose deletion did not reconcile.
    /// </summary>
    /// <remarks>
    /// Both are driven through <see cref="IDataRetentionService.ApplyAsync"/> on the same fixture, so
    /// what separates them is the service's own classification rather than the test's setup. The
    /// first is forced by refusing exactly one durable transition — the completion — and letting
    /// every other store call through; the second by a trigger that puts the deleted row back, which
    /// is how this suite already forces post-delete reconciliation to fail.
    /// </remarks>
    [SkippableFact]

    public async Task ApplyAsync_DistinguishesAnUnfinalizedOperationFromAFailedReconciliation()
    {

        RequireSqlCipher();

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.UploadedFiles = EnabledRule();

        // The reconciliation failure runs first, because it terminalizes its own durable operation.
        // The refused-completion case deliberately leaves its operation row open, which is the state
        // the next apply would refuse as an already-active operation.
        (Guid sessionId, _) = await SeedSessionAsync(pinned: false);

        await ExecuteAsync(
            """
            CREATE TRIGGER retain_deleted_session_for_codes
            AFTER DELETE ON "Sessions"
            BEGIN
                INSERT INTO "Sessions" ("Id", "Status", "CreatedAt", "UpdatedAt")
                VALUES (OLD."Id", OLD."Status", OLD."CreatedAt", OLD."UpdatedAt");
            END;
            """);

        DataRetentionService unreconciled = CreateService(settings);

        DataRetentionRequest deleteSession = new(
            DataRetentionOperation.DeleteSession,
            TargetId: sessionId);

        DataRetentionPlan unreconciledPlan = await unreconciled.PlanAsync(
            deleteSession,
            CancellationToken.None);

        Result<DataRetentionApplyResult> reconciliationFailed = await unreconciled.ApplyAsync(
            new DataRetentionApplyRequest(deleteSession, unreconciledPlan.PlanId),
            CancellationToken.None);

        Assert.True(reconciliationFailed.IsFailure);

        Assert.Equal(
            ErrorCodes.Data.ReconciliationFailed,
            reconciliationFailed.Error.Code);

        await ExecuteAsync("DROP TRIGGER retain_deleted_session_for_codes");

        Guid finalizeFileId = Guid.NewGuid();

        await File.WriteAllBytesAsync(
            Path.Combine(_filesRoot, finalizeFileId.ToString("N")),
            [1, 2, 3]);

        await SeedUploadedFileAsync(finalizeFileId, 3);

        RefusesCompletionOperationStore refusingStore = new(
            new LongRunningOperationStore(_db!, TestOrdinaryConnectionFactory.For(_db!)));

        DataRetentionService unfinalized = CreateService(
            settings,
            operationStore: refusingStore);

        DataRetentionRequest prune = new(DataRetentionOperation.Prune);

        DataRetentionPlan unfinalizedPlan = await unfinalized.PlanAsync(
            prune,
            CancellationToken.None);

        Result<DataRetentionApplyResult> notFinalized = await unfinalized.ApplyAsync(
            new DataRetentionApplyRequest(prune, unfinalizedPlan.PlanId),
            CancellationToken.None);

        Assert.True(notFinalized.IsFailure);

        Assert.True(refusingStore.RefusedCompletion);

        // The rows really did go: this is the ending where a retry is safe precisely because the work
        // is done and only the bookkeeping is open.
        Assert.Equal(0, await CountAllAsync("UploadedFiles"));

        Assert.Equal(
            ErrorCodes.Data.OperationNotFinalized,
            notFinalized.Error.Code);

        Assert.NotEqual(
            reconciliationFailed.Error.Code,
            notFinalized.Error.Code);

    }

    /// <summary>
    /// The three endings resolve to different HTTP answers, which is the whole point of splitting them.
    /// </summary>
    /// <remarks>
    /// A quarantine recovery is a 500 because the database mutation committed and bytes an operator
    /// owns are still on disk; an unfinalized operation is a 409 because nothing is wrong with the
    /// data and the caller may simply try again. Mapping them to one status is what made a client
    /// unable to tell "retry" from "stop and call somebody".
    /// </remarks>
    [Fact]

    public void The_three_retention_endings_do_not_share_one_status()
    {

        int quarantine = ArcanumErrorMapper.ResolveStatusCode(
            ErrorCodes.Data.QuarantineRecoveryRequired);

        int notFinalized = ArcanumErrorMapper.ResolveStatusCode(
            ErrorCodes.Data.OperationNotFinalized);

        int reconciliation = ArcanumErrorMapper.ResolveStatusCode(
            ErrorCodes.Data.ReconciliationFailed);

        Assert.Equal(StatusCodes.Status500InternalServerError, quarantine);

        Assert.Equal(StatusCodes.Status409Conflict, notFinalized);

        Assert.Equal(StatusCodes.Status500InternalServerError, reconciliation);

        Assert.NotEqual(reconciliation, notFinalized);

    }

    /// <summary>
    /// A row left for durable recovery is a row the recovery machinery actually adopts.
    /// </summary>
    /// <remarks>
    /// The quarantine ending tells its caller "quarantined bytes will be finalized by durable
    /// recovery", and that sentence is only true if the durable row it leaves behind is one
    /// <see cref="ILongRunningOperationStore"/> re-selects. Its recovery predicate matches on one
    /// exact <c>TerminalErrorCode</c>, and <c>TryStartSingleFlightAsync</c> refuses every new
    /// retention operation while a <c>ReconciliationRequired</c> retention row exists — so a row
    /// stamped with a code the predicate does not match is not merely unrecovered, it blocks prune,
    /// delete-session, reset-memory and factory-reset indefinitely until a person resets it.
    ///
    /// <para>The stamp is taken from the service's own constant rather than restated here, so this
    /// fails if the service ever stamps something the store cannot adopt. Both halves of the
    /// contract are exercised through the store's production API: the reconciler's
    /// <see cref="ILongRunningOperationStore.FindExpiredAsync"/> sweep, and the
    /// <see cref="ILongRunningOperationStore.TryAcquireLeaseAsync"/> claim that follows it.</para>
    ///
    /// <para>The gap this leaves is recorded rather than hidden: the quarantine arm itself cannot be
    /// forced from a test — <c>TryDeleteQuarantined</c> fails only if the quarantined file's identity
    /// or link count changes between the rename and the delete, and only a database commit sits
    /// between them — so what is pinned here is the argument that arm passes, not the arm running.
    /// </para>
    /// </remarks>
    [SkippableFact]

    public async Task A_retention_row_left_for_durable_recovery_is_adopted_by_the_store()
    {

        RequireSqlCipher();

        LongRunningOperationStore operations = new(
            _db!,
            TestOrdinaryConnectionFactory.For(_db!));

        DateTimeOffset started = DateTimeOffset.UtcNow.AddMinutes(-10);

        LongRunningOperation operation = await operations.CreateAsync(
            new LongRunningOperationCreateRequest(
                LongRunningOperationKinds.DataRetentionPrune,
                LongRunningOperationRecoveryPolicy.RestartIdempotently,
                "Quarantine recovery contract.",
                started));

        LongRunningOperationLeaseResult lease = await operations.TryAcquireLeaseAsync(
            operation.Id,
            "quarantine-contract-test",
            started,
            started.AddMinutes(5));

        Assert.True(lease.Acquired);

        // Exactly what the quarantine ending does to its row, with the code it actually stamps.
        Assert.True(
            await operations.TryTransitionAsync(
                operation.Id,
                lease.Operation.Revision,
                "quarantine-contract-test",
                LongRunningOperationState.ReconciliationRequired,
                started,
                DataRetentionService.RetentionRecoveryTerminalCode,
                CancellationToken.None));

        DateTimeOffset afterLease = started.AddMinutes(30);

        IReadOnlyList<LongRunningOperation> expired = await operations.FindExpiredAsync(
            afterLease,
            limit: 50);

        Assert.Contains(expired, candidate => candidate.Id == operation.Id);

        LongRunningOperationLeaseResult adopted = await operations.TryAcquireLeaseAsync(
            operation.Id,
            "durable-recovery",
            afterLease,
            afterLease.AddMinutes(5));

        Assert.True(adopted.Acquired);

        // The caller-facing code stays distinct on purpose: the operator is told which ending this
        // was, and the row carries the one code recovery matches on.
        Assert.NotEqual(
            ErrorCodes.Data.QuarantineRecoveryRequired,
            DataRetentionService.RetentionRecoveryTerminalCode);

    }

    /// <summary>
    /// A store that refuses exactly one transition: the one that closes a successful operation.
    /// </summary>
    /// <remarks>
    /// Everything else is delegated to the real store, so the operation is created, leased,
    /// heartbeaten and checkpointed exactly as it is in production. Refusing more than the completion
    /// would change which arm the service takes rather than which ending it reports.
    /// </remarks>
    private sealed class RefusesCompletionOperationStore(ILongRunningOperationStore inner)
        : ILongRunningOperationStore
    {

        public bool RefusedCompletion { get; private set; }

        public Task<LongRunningOperation> CreateAsync(
            LongRunningOperationCreateRequest request,
            CancellationToken cancellationToken = default) =>
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
            inner.TryStartSingleFlightAsync(
                request,
                ownerId,
                utcNow,
                leaseExpiresAt,
                cancellationToken);

        public Task<LongRunningOperation?> GetAsync(
            Guid operationId,
            CancellationToken cancellationToken = default) =>
            inner.GetAsync(operationId, cancellationToken);

        public Task<LongRunningOperationRequestIdentity?> FindRequestIdentityAsync(
            Guid operationId,
            CancellationToken cancellationToken = default) =>
            inner.FindRequestIdentityAsync(operationId, cancellationToken);

        public Task<LongRunningOperationRequestIdentityMatch?> FindByRequestedOperationIdAsync(
            Guid requestedOperationId,
            CancellationToken cancellationToken = default) =>
            inner.FindByRequestedOperationIdAsync(requestedOperationId, cancellationToken);

        public Task<IReadOnlyList<LongRunningOperation>> ListAsync(
            LongRunningOperationQuery query,
            CancellationToken cancellationToken = default) =>
            inner.ListAsync(query, cancellationToken);

        public Task<IReadOnlyList<LongRunningOperation>> FindExpiredAsync(
            DateTimeOffset utcNow,
            int limit,
            CancellationToken cancellationToken = default) =>
            inner.FindExpiredAsync(utcNow, limit, cancellationToken);

        public Task<LongRunningOperationLeaseResult> TryAcquireLeaseAsync(
            Guid operationId,
            string ownerId,
            DateTimeOffset utcNow,
            DateTimeOffset leaseExpiresAt,
            CancellationToken cancellationToken = default) =>
            inner.TryAcquireLeaseAsync(
                operationId,
                ownerId,
                utcNow,
                leaseExpiresAt,
                cancellationToken);

        public Task<bool> HeartbeatAsync(
            Guid operationId,
            string ownerId,
            DateTimeOffset utcNow,
            DateTimeOffset leaseExpiresAt,
            CancellationToken cancellationToken = default) =>
            inner.HeartbeatAsync(
                operationId,
                ownerId,
                utcNow,
                leaseExpiresAt,
                cancellationToken);

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
            inner.SaveCheckpointAsync(
                operationId,
                ownerId,
                expectedCheckpointVersion,
                checkpointVersion,
                checkpointPayload,
                checkpointReference,
                publicSummary,
                utcNow,
                cancellationToken);

        public Task<bool> TryTransitionAsync(
            Guid operationId,
            long expectedRevision,
            string? ownerId,
            LongRunningOperationState state,
            DateTimeOffset utcNow,
            string? terminalErrorCode = null,
            CancellationToken cancellationToken = default)
        {

            if (state is LongRunningOperationState.Completed)
            {

                RefusedCompletion = true;

                return Task.FromResult(false);

            }

            return inner.TryTransitionAsync(
                operationId,
                expectedRevision,
                ownerId,
                state,
                utcNow,
                terminalErrorCode,
                cancellationToken);

        }

        public Task<bool> RequestCancellationAsync(
            Guid operationId,
            long expectedRevision,
            DateTimeOffset utcNow,
            CancellationToken cancellationToken = default) =>
            inner.RequestCancellationAsync(
                operationId,
                expectedRevision,
                utcNow,
                cancellationToken);

        public Task<bool> ResetForRetryAsync(
            Guid operationId,
            long expectedRevision,
            DateTimeOffset utcNow,
            CancellationToken cancellationToken = default) =>
            inner.ResetForRetryAsync(
                operationId,
                expectedRevision,
                utcNow,
                cancellationToken);

        public Task<IReadOnlyList<LongRunningOperationCount>> GetCountsAsync(
            CancellationToken cancellationToken = default) =>
            inner.GetCountsAsync(cancellationToken);

    }

}
