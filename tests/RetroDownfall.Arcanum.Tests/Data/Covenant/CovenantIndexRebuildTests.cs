using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Operations;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// The resumable base rebuild: its one entry point, its captured identity, and its terminal phases.
/// </summary>
public sealed class CovenantIndexRebuildTests
{
    private static CancellationToken Token => CancellationToken.None;

    [Fact]
    public void The_rebuilder_exposes_one_batch_method_and_no_whole_operation_shortcut()
    {
        System.Reflection.MethodInfo[] declared = [.. typeof(CovenantIndexRebuilder)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.DeclaredOnly)];

        System.Reflection.MethodInfo only = Assert.Single(declared);

        Assert.Equal(nameof(CovenantIndexRebuilder.AdvanceBatchAsync), only.Name);

        Assert.Equal(
            [
                typeof(CovenantIndexRebuildProgress),
                typeof(CovenantAcceleratorLease),
                typeof(CancellationToken),
            ],
            only.GetParameters().Select(static parameter => parameter.ParameterType));
    }

    [Fact]
    public void Rebuild_phase_codes_are_immutable()
    {
        Assert.Equal((byte)1, (byte)CovenantIndexRebuildPhase.BaseScan);

        Assert.Equal((byte)2, (byte)CovenantIndexRebuildPhase.DeltaCatchUp);

        Assert.Equal((byte)3, (byte)CovenantIndexRebuildPhase.Verifying);

        Assert.Equal((byte)4, (byte)CovenantIndexRebuildPhase.Completed);

        Assert.Equal((byte)5, (byte)CovenantIndexRebuildPhase.RestartRequired);

        Assert.Equal(5, Enum.GetValues<CovenantIndexRebuildPhase>().Length);
    }

    [Fact]
    public void Progress_invariants_reject_impossible_checkpoints()
    {
        _ = Assert.Throws<ArgumentException>(
            () => Progress(Guid.Empty, 1, 0, CovenantIndexRebuildPhase.BaseScan));

        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => Progress(Guid.NewGuid(), 0, 0, CovenantIndexRebuildPhase.BaseScan));

        // Zero is not a valid base-scan cursor: row IDs start at one, so zero would be
        // indistinguishable from "no rows committed yet".
        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => Progress(Guid.NewGuid(), 1, 0, CovenantIndexRebuildPhase.BaseScan) with
            {
                BaseScanAfterSearchRowId = 0,
            });
    }

    [Fact]
    public async Task A_start_captures_its_identity_and_clears_the_old_projection()
    {
        await using CovenantCanonicalFixture fixture = await CovenantSearchFixture.CreateAsync(Token);

        _ = await fixture.SeedHeadAsync(
            CovenantScope.Global,
            null,
            "global.key",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Body.",
            Token);

        _ = await CovenantSearchFixture.SynchronizeAsync(fixture, Token);

        Assert.Equal(1, await Count(fixture, "covenant_search_documents"));

        CovenantIndexRebuildProgress started = await AdvanceAsync(fixture, null);

        Assert.Equal(CovenantIndexRebuildPhase.BaseScan, started.Phase);

        Assert.Equal(await fixture.ReadDatasetGenerationAsync(Token), started.DatasetGeneration);

        Assert.Null(started.BaseScanAfterSearchRowId);

        Assert.Equal(0, started.BaseHeadsProcessed);

        Assert.Equal(1, started.BaseHeadsTotal);

        Assert.Equal(started.BaseTargetSearchSequence, started.LastContiguousAppliedSequence);

        // The stale projection is gone and the applied tuple is null, so nothing partial is eligible.
        Assert.Equal(0, await Count(fixture, "covenant_search_documents"));

        Assert.Equal(0, await Scalar(fixture, "SELECT COUNT(AppliedSearchSequence) FROM covenant_state;"));
    }

    [Fact]
    public async Task A_rebuild_runs_to_completion_and_publishes_eligibility_once()
    {
        await using CovenantCanonicalFixture fixture = await CovenantSearchFixture.CreateAsync(Token);

        for (int index = 0; index < 3; index++)
        {
            _ = await fixture.SeedHeadAsync(
                CovenantScope.Global,
                null,
                $"global.key{index}",
                CovenantLane.Confirmed,
                CovenantOperation.Set,
                $"Body {index}.",
                Token);
        }

        CovenantIndexRebuildProgress progress = await AdvanceAsync(fixture, null);

        int guard = 0;

        while (!progress.IsTerminal && guard++ < 32)
        {
            Assert.Equal(
                0,
                await Scalar(fixture, "SELECT COUNT(AppliedSearchSequence) FROM covenant_state;"));

            progress = await AdvanceAsync(fixture, progress);
        }

        Assert.Equal(CovenantIndexRebuildPhase.Completed, progress.Phase);

        Assert.Equal(3, progress.BaseHeadsProcessed);

        Assert.Equal(3, await Count(fixture, "covenant_search_documents"));

        Assert.Equal(
            await Scalar(fixture, "SELECT CanonicalSearchSequence FROM covenant_state;"),
            await Scalar(fixture, "SELECT AppliedSearchSequence FROM covenant_state;"));

        Assert.Equal(1, await Scalar(fixture, "SELECT RebuildStateCode FROM covenant_state;"));

        // Completion is idempotent.
        Assert.Equal(progress, await AdvanceAsync(fixture, progress));
    }

    [Fact]
    public async Task A_mutation_during_the_base_scan_is_recovered_from_the_post_target_outbox()
    {
        await using CovenantCanonicalFixture fixture = await CovenantSearchFixture.CreateAsync(Token);

        SeededHead head = await fixture.SeedHeadAsync(
            CovenantScope.Global,
            null,
            "global.moving",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Original marker.",
            Token);

        CovenantIndexRebuildProgress progress = await AdvanceAsync(fixture, null);

        progress = await AdvanceAsync(fixture, progress);

        Assert.Equal(1, progress.BaseHeadsProcessed);

        // A write to an already-passed key, after the captured base target.
        _ = await fixture.SeedHeadAsync(
            CovenantScope.Global,
            null,
            "global.moving",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Replacement marker.",
            Token,
            entryId: head.EntryId,
            laneRevision: 2,
            predecessorVersionId: head.VersionId);

        int guard = 0;

        while (!progress.IsTerminal && guard++ < 32)
        {
            progress = await AdvanceAsync(fixture, progress);
        }

        Assert.Equal(CovenantIndexRebuildPhase.Completed, progress.Phase);

        Assert.True(progress.DeltaRowsProcessed > 0);

        Assert.Equal(
            "Replacement marker.",
            await StringAsync(fixture, "SELECT AuthoredContent FROM covenant_search_documents;"));
    }

    [Fact]
    public async Task A_resume_from_a_stale_cursor_does_not_end_the_base_scan()
    {
        await using CovenantCanonicalFixture fixture = await CovenantSearchFixture.CreateAsync(Token);

        int heads = CovenantIndexRebuildProgress.BaseBatchHeads + 44;

        for (int index = 0; index < heads; index++)
        {
            _ = await fixture.SeedHeadAsync(
                CovenantScope.Global,
                null,
                $"global.key{index}",
                CovenantLane.Confirmed,
                CovenantOperation.Set,
                $"Body {index}.",
                Token);
        }

        CovenantIndexRebuildProgress staleStart = await AdvanceAsync(fixture, null);

        // The first batch commits, but the process stops before its checkpoint is saved, so the
        // resume starts from the cursor the start returned.
        CovenantIndexRebuildProgress firstBatch = await AdvanceAsync(fixture, staleStart);

        Assert.Equal(CovenantIndexRebuildPhase.BaseScan, firstBatch.Phase);

        Assert.Equal(CovenantIndexRebuildProgress.BaseBatchHeads, await Count(fixture, "covenant_search_documents"));

        CovenantIndexRebuildProgress resumed = await AdvanceAsync(fixture, staleStart);

        // The re-selected batch inserts nothing new, which is not the end of the scan.
        Assert.Equal(CovenantIndexRebuildPhase.BaseScan, resumed.Phase);

        Assert.Equal(firstBatch.BaseScanAfterSearchRowId, resumed.BaseScanAfterSearchRowId);

        CovenantIndexRebuildProgress progress = resumed;

        int guard = 0;

        while (!progress.IsTerminal && guard++ < 32)
        {
            progress = await AdvanceAsync(fixture, progress);
        }

        Assert.Equal(CovenantIndexRebuildPhase.Completed, progress.Phase);

        Assert.Equal(heads, await Count(fixture, "covenant_search_documents"));

        Assert.Equal(heads, await Count(fixture, "covenant_heads"));
    }

    [Fact]
    public async Task The_coordinator_saves_the_checkpoint_after_a_committed_batch_without_the_callers_token()
    {
        await using CovenantCanonicalFixture fixture = await CovenantSearchFixture.CreateAsync(Token);

        _ = await fixture.SeedHeadAsync(
            CovenantScope.Global,
            null,
            "global.key",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Body.",
            Token);

        CheckpointRecordingCoordinator operations = new();

        CovenantIndexRebuildCoordinator coordinator = new(
            operations,
            new FakeLongRunningOperationStore(TimeProvider.System),
            CovenantOperationGateFixture.CreateGate(await CovenantSearchFixture.LiveAvailabilityAsync(fixture, Token)),
            new CovenantIndexRebuilder(new FixedCovenantConnectionSource(fixture.Connection)),
            TimeProvider.System);

        using CancellationTokenSource caller = new();

        LongRunningOperation operation = RebuildOperation();

        Result<CovenantIndexRebuildProgress> advanced = await coordinator.AdvanceAsync(
            operation,
            "owner",
            caller.Token);

        Assert.True(advanced.IsSuccess, advanced.IsFailure ? advanced.Error.Message : null);

        // The batch has committed by the time the cursor is saved, so the save is bookkeeping for
        // work that already happened and a cancellation of the caller must not be able to skip it.
        Assert.Equal(1, operations.Checkpoints);

        Assert.False(operations.LastToken.CanBeCanceled);
    }

    [Fact]
    public async Task The_coordinator_completes_a_finished_rebuild_without_the_callers_token()
    {
        await using CovenantCanonicalFixture fixture = await CovenantSearchFixture.CreateAsync(Token);

        _ = await fixture.SeedHeadAsync(
            CovenantScope.Global,
            null,
            "global.key",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Body.",
            Token);

        CovenantIndexRebuildProgress verifying = await AdvanceAsync(fixture, null);

        int guard = 0;

        while (verifying.Phase != CovenantIndexRebuildPhase.Verifying && guard++ < 32)
        {
            verifying = await AdvanceAsync(fixture, verifying);
        }

        Assert.Equal(CovenantIndexRebuildPhase.Verifying, verifying.Phase);

        LongRunningOperation operation = RebuildOperation(verifying);

        TokenRecordingStore store = new(operation);

        CheckpointRecordingCoordinator operations = new() { AllowsTerminalBookkeeping = true };

        using CancellationTokenSource caller = new();

        Result<CovenantIndexRebuildProgress> advanced = await new CovenantIndexRebuildCoordinator(
                operations,
                store,
                CovenantOperationGateFixture.CreateGate(await CovenantSearchFixture.LiveAvailabilityAsync(fixture, Token)),
                new CovenantIndexRebuilder(new FixedCovenantConnectionSource(fixture.Connection)),
                TimeProvider.System)
            .AdvanceAsync(operation, "owner", caller.Token);

        Assert.True(advanced.IsSuccess, advanced.IsFailure ? advanced.Error.Message : null);

        Assert.Equal(CovenantIndexRebuildPhase.Completed, advanced.Value.Phase);

        // The projection is published by the time the operation is closed, so closing it is bookkeeping
        // for work that already happened: neither the read of the current revision nor the completion
        // may carry a token the caller can cancel, or a shutdown here would leave a finished rebuild
        // running forever.
        Assert.Equal(1, operations.Completions);

        Assert.False(operations.LastCompleteToken.CanBeCanceled);

        Assert.Equal(1, store.Reads);

        Assert.False(store.LastReadToken.CanBeCanceled);
    }

    [Fact]
    public async Task The_coordinator_abandons_a_stale_rebuild_and_starts_its_replacement_without_the_callers_token()
    {
        await using CovenantCanonicalFixture fixture = await CovenantSearchFixture.CreateAsync(Token);

        _ = await fixture.SeedHeadAsync(
            CovenantScope.Global,
            null,
            "global.key",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Body.",
            Token);

        CovenantIndexRebuildProgress scanned = await AdvanceAsync(fixture, null);

        // The dataset the rebuild captured is no longer the one in the database.
        LongRunningOperation operation = RebuildOperation(scanned with { DatasetGeneration = Guid.NewGuid() });

        TokenRecordingStore store = new(operation);

        CheckpointRecordingCoordinator operations = new()
        {
            AllowsTerminalBookkeeping = true,
            Replacement = operation with { Id = Guid.Parse("66666666-6666-4666-8666-666666666666") },
        };

        using CancellationTokenSource caller = new();

        Result<CovenantIndexRebuildProgress> advanced = await new CovenantIndexRebuildCoordinator(
                operations,
                store,
                CovenantOperationGateFixture.CreateGate(await CovenantSearchFixture.LiveAvailabilityAsync(fixture, Token)),
                new CovenantIndexRebuilder(new FixedCovenantConnectionSource(fixture.Connection)),
                TimeProvider.System)
            .AdvanceAsync(operation, "owner", caller.Token);

        Assert.True(advanced.IsSuccess, advanced.IsFailure ? advanced.Error.Message : null);

        Assert.Equal(CovenantIndexRebuildPhase.RestartRequired, advanced.Value.Phase);

        // The stale operation is closed and its replacement started on tokens the caller cannot cancel:
        // cancelling between the two would leave a rebuild owed with nothing running it.
        Assert.Equal(1, store.Transitions);

        Assert.Equal(LongRunningOperationState.Abandoned, store.LastTransitionState);

        Assert.False(store.LastReadToken.CanBeCanceled);

        Assert.False(store.LastTransitionToken.CanBeCanceled);

        Assert.Equal(1, operations.Starts);

        Assert.False(operations.LastStartToken.CanBeCanceled);
    }

    [Fact]
    public async Task Verification_refuses_to_publish_a_projection_that_does_not_cover_every_head()
    {
        await using CovenantCanonicalFixture fixture = await CovenantSearchFixture.CreateAsync(Token);

        for (int index = 0; index < 3; index++)
        {
            _ = await fixture.SeedHeadAsync(
                CovenantScope.Global,
                null,
                $"global.key{index}",
                CovenantLane.Confirmed,
                CovenantOperation.Set,
                $"Body {index}.",
                Token);
        }

        CovenantIndexRebuildProgress progress = await AdvanceAsync(fixture, null);

        int guard = 0;

        while (progress.Phase != CovenantIndexRebuildPhase.Verifying && guard++ < 32)
        {
            progress = await AdvanceAsync(fixture, progress);
        }

        Assert.Equal(CovenantIndexRebuildPhase.Verifying, progress.Phase);

        // A document that went missing after the scan passed it and that no delta names.
        await ExecuteAsync(
            fixture,
            "DELETE FROM covenant_search_documents WHERE SearchRowId = (SELECT MIN(SearchRowId) FROM covenant_search_documents);");

        FakeCovenantAvailability availability = await CovenantSearchFixture.LiveAvailabilityAsync(fixture, Token);

        await using CovenantAcceleratorLease lease =
            (await CovenantOperationGateFixture.CreateGate(availability).AcquireAcceleratorAsync(Token)).Value;

        Result<CovenantIndexRebuildProgress> refused = await new CovenantIndexRebuilder(
                new FixedCovenantConnectionSource(fixture.Connection))
            .AdvanceBatchAsync(progress, lease, Token);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, refused.Error.Code);

        Assert.Equal(0, await Scalar(fixture, "SELECT COUNT(AppliedSearchSequence) FROM covenant_state;"));
    }

    [Fact]
    public async Task A_changed_dataset_generation_restarts_rather_than_publishing()
    {
        await using CovenantCanonicalFixture fixture = await CovenantSearchFixture.CreateAsync(Token);

        _ = await fixture.SeedHeadAsync(
            CovenantScope.Global,
            null,
            "global.key",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Body.",
            Token);

        CovenantIndexRebuildProgress progress = await AdvanceAsync(fixture, null);

        CovenantIndexRebuildProgress stale = progress with { DatasetGeneration = Guid.NewGuid() };

        CovenantIndexRebuildProgress restarted = await AdvanceAsync(fixture, stale);

        Assert.Equal(CovenantIndexRebuildPhase.RestartRequired, restarted.Phase);

        // The stale captured identity travels with the terminal checkpoint.
        Assert.Equal(stale.DatasetGeneration, restarted.DatasetGeneration);

        Assert.True(restarted.IsTerminal);

        Assert.Equal(restarted, await AdvanceAsync(fixture, restarted));
    }

    [Fact]
    public async Task A_gap_in_the_post_target_outbox_restarts()
    {
        await using CovenantCanonicalFixture fixture = await CovenantSearchFixture.CreateAsync(Token);

        _ = await fixture.SeedHeadAsync(
            CovenantScope.Global,
            null,
            "global.key",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Body.",
            Token);

        CovenantIndexRebuildProgress progress = await AdvanceAsync(fixture, null);

        // Canonical advances but its deltas are gone, which is what an overflowed outbox looks like.
        await ExecuteAsync(
            fixture,
            "UPDATE covenant_state SET CanonicalSearchSequence = CanonicalSearchSequence + 2 WHERE StateKey = 1;");

        int guard = 0;

        while (!progress.IsTerminal && guard++ < 32)
        {
            progress = await AdvanceAsync(fixture, progress);
        }

        Assert.Equal(CovenantIndexRebuildPhase.RestartRequired, progress.Phase);

        Assert.Equal(0, await Scalar(fixture, "SELECT COUNT(AppliedSearchSequence) FROM covenant_state;"));
    }

    [Fact]
    public async Task A_revoked_accelerator_lease_advances_nothing()
    {
        await using CovenantCanonicalFixture fixture = await CovenantSearchFixture.CreateAsync(Token);

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate(
            await CovenantSearchFixture.LiveAvailabilityAsync(fixture, Token));

        CovenantAcceleratorLease lease = (await gate.AcquireAcceleratorAsync(Token)).Value;

        await lease.DisposeAsync();

        Result<CovenantIndexRebuildProgress> refused = await new CovenantIndexRebuilder(
                new FixedCovenantConnectionSource(fixture.Connection))
            .AdvanceBatchAsync(null, lease, Token);

        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, refused.Error.Code);
    }

    private static CovenantIndexRebuildProgress Progress(
        Guid generation,
        ulong epoch,
        long target,
        CovenantIndexRebuildPhase phase) =>
        new(generation, epoch, target, 0, phase, null, target, 0, null, 0);

    private static LongRunningOperation RebuildOperation(CovenantIndexRebuildProgress? checkpoint = null) =>
        new(
            Guid.Parse("55555555-5555-4555-8555-555555555555"),
            LongRunningOperationKinds.CovenantIndexRebuild,
            LongRunningOperationState.Running,
            LongRunningOperationRecoveryPolicy.ResumeFromCheckpoint,
            RootOperationId: null,
            ParentOperationId: null,
            SessionId: null,
            RunId: null,
            InferenceRunId: null,
            BudgetReservationId: null,
            IdempotencyClaimId: null,
            DateTimeOffset.UnixEpoch,
            StartedAt: null,
            HeartbeatAt: null,
            CompletedAt: null,
            LeaseOwner: null,
            LeaseExpiresAt: null,
            AttemptCount: 1,
            CheckpointVersion: checkpoint is null ? 0 : CovenantIndexRebuildCheckpointV1.CurrentVersion,
            CheckpointPayload: checkpoint is null
                ? null
                : CovenantRecoveryCheckpointCodec.Encode(CovenantIndexRebuildCoordinator.ToCheckpoint(checkpoint)),
            CheckpointReference: null,
            "rebuild",
            TerminalErrorCode: null,
            Revision: 1);

    private static async Task<CovenantIndexRebuildProgress> AdvanceAsync(
        CovenantCanonicalFixture fixture,
        CovenantIndexRebuildProgress? progress)
    {
        FakeCovenantAvailability availability = await CovenantSearchFixture.LiveAvailabilityAsync(fixture, Token);

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate(availability);

        await using CovenantAcceleratorLease lease = (await gate.AcquireAcceleratorAsync(Token)).Value;

        Result<CovenantIndexRebuildProgress> advanced = await new CovenantIndexRebuilder(
                new FixedCovenantConnectionSource(fixture.Connection))
            .AdvanceBatchAsync(progress, lease, Token);

        Assert.True(advanced.IsSuccess, advanced.IsFailure ? advanced.Error.Message : null);

        return advanced.Value;
    }

    private static Task<long> Count(CovenantCanonicalFixture fixture, string table) =>
        CovenantCapacityFixture.ScalarAsync(fixture, $"SELECT COUNT(*) FROM {table};", Token);

    private static Task<long> Scalar(CovenantCanonicalFixture fixture, string sql) =>
        CovenantCapacityFixture.ScalarAsync(fixture, sql, Token);

    private static async Task<string?> StringAsync(CovenantCanonicalFixture fixture, string sql)
    {
        await using Microsoft.Data.Sqlite.SqliteCommand command = fixture.Connection.CreateCommand();

        command.CommandText = sql;

        object? value = await command.ExecuteScalarAsync(Token);

        return value is null or DBNull ? null : Convert.ToString(value);
    }

    private static async Task ExecuteAsync(CovenantCanonicalFixture fixture, string sql)
    {
        await using Microsoft.Data.Sqlite.SqliteCommand command = fixture.Connection.CreateCommand();

        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync(Token);
    }

    private sealed class CheckpointRecordingCoordinator : ILongRunningOperationCoordinator
    {
        /// <summary>
        /// Whether the rebuild is expected to reach its terminal bookkeeping. A mid-scan batch starts and
        /// completes nothing, so by default either call fails the test.
        /// </summary>
        internal bool AllowsTerminalBookkeeping { get; init; }

        internal LongRunningOperation? Replacement { get; init; }

        internal int Checkpoints { get; private set; }

        internal int Completions { get; private set; }

        internal int Starts { get; private set; }

        internal CancellationToken LastToken { get; private set; }

        internal CancellationToken LastCompleteToken { get; private set; }

        internal CancellationToken LastStartToken { get; private set; }

        public Task<LongRunningOperationLeaseResult> StartAsync(
            LongRunningOperationCreateRequest request,
            string ownerId,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken)
        {
            if (!AllowsTerminalBookkeeping || Replacement is null)
            {
                throw new NotSupportedException("A mid-scan batch starts nothing.");
            }

            Starts++;

            LastStartToken = cancellationToken;

            return Task.FromResult(new LongRunningOperationLeaseResult(true, Replacement));
        }

        public Task<Result<LongRunningOperationRequestIdentityResult>> StartWithRequestIdentityAsync(
            LongRunningOperationCreateRequest request,
            LongRunningOperationRequestIdentity identity,
            string ownerId,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("A rebuild is never named by a caller.");

        public Task<bool> HeartbeatAsync(
            Guid operationId,
            string ownerId,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<bool> CheckpointAsync(
            Guid operationId,
            string ownerId,
            int expectedCheckpointVersion,
            int checkpointVersion,
            byte[]? checkpointPayload,
            string? checkpointReference,
            string publicSummary,
            CancellationToken cancellationToken)
        {
            Checkpoints++;

            LastToken = cancellationToken;

            return Task.FromResult(true);
        }

        public Task<bool> CompleteAsync(
            Guid operationId,
            string ownerId,
            long expectedRevision,
            CancellationToken cancellationToken)
        {
            if (!AllowsTerminalBookkeeping)
            {
                throw new NotSupportedException("A mid-scan batch completes nothing.");
            }

            Completions++;

            LastCompleteToken = cancellationToken;

            return Task.FromResult(true);
        }

        public Task<bool> FailAsync(
            Guid operationId,
            string ownerId,
            long expectedRevision,
            string errorCode,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("A mid-scan batch fails nothing.");
    }

    /// <summary>
    /// A store that answers the one operation it was given and records the token of every call the
    /// coordinator's terminal bookkeeping makes; anything else it is never asked for.
    /// </summary>
    private sealed class TokenRecordingStore(LongRunningOperation operation) : ILongRunningOperationStore
    {
        internal int Reads { get; private set; }

        internal int Transitions { get; private set; }

        internal CancellationToken LastReadToken { get; private set; }

        internal CancellationToken LastTransitionToken { get; private set; }

        internal LongRunningOperationState LastTransitionState { get; private set; }

        public Task<LongRunningOperation?> GetAsync(Guid operationId, CancellationToken cancellationToken = default)
        {
            Reads++;

            LastReadToken = cancellationToken;

            return Task.FromResult<LongRunningOperation?>(operation);
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
            Transitions++;

            LastTransitionToken = cancellationToken;

            LastTransitionState = state;

            return Task.FromResult(true);
        }

        public Task<LongRunningOperation> CreateAsync(
            LongRunningOperationCreateRequest request,
            CancellationToken cancellationToken = default) => throw Unexpected();

        public Task<LongRunningOperationRequestIdentityResult> ResolveOrCreateAsync(
            LongRunningOperationCreateRequest request,
            LongRunningOperationRequestIdentity identity,
            CancellationToken cancellationToken = default) => throw Unexpected();

        public Task<LongRunningOperation?> TryStartSingleFlightAsync(
            LongRunningOperationCreateRequest request,
            string ownerId,
            DateTimeOffset utcNow,
            DateTimeOffset leaseExpiresAt,
            CancellationToken cancellationToken = default) => throw Unexpected();

        public Task<LongRunningOperationRequestIdentity?> FindRequestIdentityAsync(
            Guid operationId,
            CancellationToken cancellationToken = default) => throw Unexpected();

        public Task<LongRunningOperationRequestIdentityMatch?> FindByRequestedOperationIdAsync(
            Guid requestedOperationId,
            CancellationToken cancellationToken = default) => throw Unexpected();

        public Task<IReadOnlyList<LongRunningOperation>> ListAsync(
            LongRunningOperationQuery query,
            CancellationToken cancellationToken = default) => throw Unexpected();

        public Task<IReadOnlyList<LongRunningOperation>> FindExpiredAsync(
            DateTimeOffset utcNow,
            int limit,
            CancellationToken cancellationToken = default) => throw Unexpected();

        public Task<LongRunningOperationLeaseResult> TryAcquireLeaseAsync(
            Guid operationId,
            string ownerId,
            DateTimeOffset utcNow,
            DateTimeOffset leaseExpiresAt,
            CancellationToken cancellationToken = default) => throw Unexpected();

        public Task<bool> HeartbeatAsync(
            Guid operationId,
            string ownerId,
            DateTimeOffset utcNow,
            DateTimeOffset leaseExpiresAt,
            CancellationToken cancellationToken = default) => throw Unexpected();

        public Task<bool> SaveCheckpointAsync(
            Guid operationId,
            string ownerId,
            int expectedCheckpointVersion,
            int checkpointVersion,
            byte[]? checkpointPayload,
            string? checkpointReference,
            string publicSummary,
            DateTimeOffset utcNow,
            CancellationToken cancellationToken = default) => throw Unexpected();

        public Task<bool> RequestCancellationAsync(
            Guid operationId,
            long expectedRevision,
            DateTimeOffset utcNow,
            CancellationToken cancellationToken = default) => throw Unexpected();

        public Task<bool> ResetForRetryAsync(
            Guid operationId,
            long expectedRevision,
            DateTimeOffset utcNow,
            CancellationToken cancellationToken = default) => throw Unexpected();

        public Task<IReadOnlyList<LongRunningOperationCount>> GetCountsAsync(
            CancellationToken cancellationToken = default) => throw Unexpected();

        private static NotSupportedException Unexpected() =>
            new("The coordinator's terminal bookkeeping reads and transitions one operation and nothing else.");
    }
}
