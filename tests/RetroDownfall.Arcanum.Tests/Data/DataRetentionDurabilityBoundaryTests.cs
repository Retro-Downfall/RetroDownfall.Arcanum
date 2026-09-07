using System.Globalization;
using System.Text;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Operations;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class DataRetentionServiceTests
{

    [SkippableFact]

    public async Task ApplyAsync_DeleteSession_WhenCommitFails_RestoresQuarantinedBytes()
    {

        RequireSqlCipher();

        (Guid sessionId, Guid entryId) = await SeedSessionAsync(pinned: false);

        SeededAttachment attachment = await SeedAttachmentAsync(sessionId, entryId);

        await CreateDeferredCommitFailureAsync(
            "Sessions",
            "SessionId",
            "fail_session_retention_commit");

        DataRetentionService service = CreateBoundaryService(
            new ArcanumSettings(),
            static (_, _) => Task.CompletedTask);

        DataRetentionRequest request = new(
            DataRetentionOperation.DeleteSession,
            sessionId);

        DataRetentionPlan plan = await service.PlanAsync(request);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId));

        Assert.True(result.IsFailure);

        Assert.True(File.Exists(attachment.AbsolutePath));

        Assert.Equal(attachment.Bytes, await File.ReadAllBytesAsync(attachment.AbsolutePath));

        Assert.Equal(1, await CountAllAsync("Sessions"));

        Assert.Equal(1, await CountAllAsync("SessionAttachments"));

    }

    [SkippableFact]

    public async Task ApplyAsync_DeleteAttachment_WhenCommitFails_RestoresQuarantinedBytes()
    {

        RequireSqlCipher();

        (Guid sessionId, Guid entryId) = await SeedSessionAsync(pinned: false);

        SeededAttachment attachment = await SeedAttachmentAsync(sessionId, entryId);

        await CreateDeferredCommitFailureAsync(
            "SessionAttachments",
            "AttachmentId",
            "fail_attachment_retention_commit");

        DataRetentionService service = CreateBoundaryService(
            new ArcanumSettings(),
            static (_, _) => Task.CompletedTask);

        DataRetentionRequest request = new(
            DataRetentionOperation.DeleteAttachment,
            attachment.AttachmentId);

        DataRetentionPlan plan = await service.PlanAsync(request);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId));

        Assert.True(result.IsFailure);

        Assert.True(File.Exists(attachment.AbsolutePath));

        Assert.Equal(attachment.Bytes, await File.ReadAllBytesAsync(attachment.AbsolutePath));

        Assert.Equal(1, await CountAllAsync("SessionAttachments"));

    }

    [SkippableFact]

    public async Task ApplyAsync_PruneUploadedFile_WhenCommitFails_RestoresQuarantinedBytes()
    {

        RequireSqlCipher();

        Guid fileId = Guid.NewGuid();

        byte[] bytes = [4, 3, 2, 1];

        string path = Path.Combine(_filesRoot, fileId.ToString("N"));

        await File.WriteAllBytesAsync(path, bytes);

        await SeedUploadedFileAsync(fileId, bytes.LongLength);

        await CreateDeferredCommitFailureAsync(
            "UploadedFiles",
            "FileId",
            "fail_uploaded_file_retention_commit");

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.UploadedFiles = EnabledRule();

        DataRetentionService service = CreateBoundaryService(
            settings,
            static (_, _) => Task.CompletedTask);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(request);

        Assert.Contains("file:" + fileId.ToString("D"), plan.CandidateIds);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId));

        Assert.True(result.IsFailure);

        Assert.True(File.Exists(path));

        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));

        Assert.Equal(1, await CountAllAsync("UploadedFiles"));

    }

    [SkippableFact]

    public async Task ApplyAsync_ResetMemory_WhenRowsAppearAfterPreview_PreservesEntireScope()
    {

        RequireSqlCipher();

        (_, Guid firstEntryId) = await SeedSessionAsync(pinned: false);

        (_, Guid secondEntryId) = await SeedSessionAsync(pinned: false);

        await SeedEntryEmbeddingAsync(firstEntryId);

        await ExecuteAsync(
            $"""
            CREATE TRIGGER add_memory_after_retention_start
            AFTER INSERT ON LongRunningOperations
            WHEN NEW.Kind = '{LongRunningOperationKinds.DataRetentionMutation}'
            BEGIN
                INSERT INTO entry_embeddings (EntryId, Embedding, Dim)
                VALUES ('{Canonical(secondEntryId)}', X'0000803F', 1);
            END;
            """);

        DataRetentionService service = CreateBoundaryService(
            new ArcanumSettings(),
            static (_, _) => Task.CompletedTask);

        DataRetentionRequest request = new(
            DataRetentionOperation.ResetMemory,
            MemoryScope: MemoryResetScope.Entry);

        DataRetentionPlan plan = await service.PlanAsync(request);

        Assert.Equal(1, plan.DerivedRecords);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId));

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.Conflict, result.Error.Code);

        Assert.Equal(2, await CountAllAsync("entry_embeddings"));

    }

    [SkippableFact]

    public async Task ApplyAsync_DeleteSession_WhenWatermarkReappears_FailsReconciliation()
    {

        RequireSqlCipher();

        (Guid sessionId, _) = await SeedSessionAsync(pinned: false);

        await ExecuteAsync(
            """
            INSERT INTO saga_extraction_watermarks
                (SessionId, LastExtractedEntryCreatedAt, LastExtractedEntrySequence)
            VALUES (@sessionId, @at, 0)
            """,
            ("@sessionId", sessionId.ToString()),
            ("@at", OldTimestamp));

        await ExecuteAsync(
            """
            CREATE TRIGGER retain_session_watermark
            AFTER DELETE ON saga_extraction_watermarks
            BEGIN
                INSERT INTO saga_extraction_watermarks
                    (SessionId, LastExtractedEntryCreatedAt, LastExtractedEntrySequence)
                VALUES (OLD.SessionId, OLD.LastExtractedEntryCreatedAt, OLD.LastExtractedEntrySequence);
            END;
            """);

        DataRetentionService service = CreateBoundaryService(
            new ArcanumSettings(),
            static (_, _) => Task.CompletedTask);

        DataRetentionRequest request = new(
            DataRetentionOperation.DeleteSession,
            sessionId);

        DataRetentionPlan plan = await service.PlanAsync(request);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId));

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.ReconciliationFailed, result.Error.Code);

        Assert.Equal(1, await CountAllAsync("saga_extraction_watermarks"));

    }

    [SkippableFact]

    public async Task ApplyAsync_DeleteAttachment_WhenEmbeddingReappears_FailsReconciliation()
    {

        RequireSqlCipher();

        (Guid sessionId, Guid entryId) = await SeedSessionAsync(pinned: false);

        SeededAttachment attachment = await SeedAttachmentAsync(sessionId, entryId);

        await ExecuteAsync(
            """
            CREATE TRIGGER retain_attachment_embedding
            AFTER DELETE ON session_attachment_embeddings
            BEGIN
                INSERT INTO session_attachment_embeddings (ChunkId, Embedding, Dim)
                VALUES (OLD.ChunkId, OLD.Embedding, OLD.Dim);
            END;
            """);

        DataRetentionService service = CreateBoundaryService(
            new ArcanumSettings(),
            static (_, _) => Task.CompletedTask);

        DataRetentionRequest request = new(
            DataRetentionOperation.DeleteAttachment,
            attachment.AttachmentId);

        DataRetentionPlan plan = await service.PlanAsync(request);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId));

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.ReconciliationFailed, result.Error.Code);

        Assert.Equal(1, await CountAllAsync("session_attachment_embeddings"));

    }

    [SkippableFact]

    public async Task ApplyAsync_PruneEntry_WhenVectorReappears_FailsReconciliation()
    {

        RequireSqlCipher();

        (_, Guid entryId) = await SeedSessionAsync(pinned: false);

        await ExecuteAsync(
            """
            CREATE TABLE IF NOT EXISTS entry_embeddings_vec
                (EntryId TEXT PRIMARY KEY, Embedding BLOB NOT NULL)
            """);

        await ExecuteAsync(
            "INSERT INTO entry_embeddings_vec (EntryId, Embedding) VALUES (@id, X'0000803F')",
            ("@id", entryId.ToString()));

        await ExecuteAsync(
            """
            CREATE TRIGGER retain_entry_vector
            AFTER DELETE ON entry_embeddings_vec
            BEGIN
                INSERT INTO entry_embeddings_vec (EntryId, Embedding)
                VALUES (OLD.EntryId, OLD.Embedding);
            END;
            """);

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.Entries = EnabledRule();

        DataRetentionService service = CreateBoundaryService(
            settings,
            static (_, _) => Task.CompletedTask);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(request);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId));

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.ReconciliationFailed, result.Error.Code);

        Assert.Equal(1, await CountAllAsync("entry_embeddings_vec"));

    }

    [SkippableFact]

    public async Task PlanAsync_PruneAttachment_WithMaxOne_DoesNotLetBlockedOldestStarveEligible()
    {

        RequireSqlCipher();

        (Guid blockedSessionId, Guid blockedEntryId) = await SeedSessionAsync(pinned: false);

        (Guid eligibleSessionId, Guid eligibleEntryId) = await SeedSessionAsync(pinned: false);

        SeededAttachment blocked = await SeedAttachmentAsync(blockedSessionId, blockedEntryId);

        SeededAttachment eligible = await SeedAttachmentAsync(eligibleSessionId, eligibleEntryId);

        await SeedContextPinAsync(
            blockedSessionId,
            SessionContextPinKind.Attachment,
            blocked.AttachmentId.ToString());

        await ExecuteAsync(
            "UPDATE SessionAttachments SET CreatedAt = @at WHERE lower(replace(Id, '-', '')) = @id",
            ("@at", "1999-01-01T00:00:00.0000000+00:00"),
            ("@id", blocked.AttachmentId.ToString("N")));

        await ExecuteAsync(
            "UPDATE SessionAttachments SET CreatedAt = @at WHERE lower(replace(Id, '-', '')) = @id",
            ("@at", "2000-01-01T00:00:00.0000000+00:00"),
            ("@id", eligible.AttachmentId.ToString("N")));

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.Attachments = EnabledRule();

        DataRetentionService service = CreateBoundaryService(
            settings,
            static (_, _) => Task.CompletedTask);

        DataRetentionPlan plan = await service.PlanAsync(
            new DataRetentionRequest(DataRetentionOperation.Prune));

        Assert.Contains(
            "attachment:" + eligible.AttachmentId.ToString("D"),
            plan.CandidateIds);

    }

    [SkippableFact]
    public async Task RefusedCandidateKeepsPendingJournalAndCandidateCursor()
    {
        RequireSqlCipher();

        HostedPruneHarness harness = await CreateHostedPruneHarnessAsync();

        IGrimoireClosingOwner? closing = null;

        harness.Admission.BeforeEffectGroupAdmission = () =>
            closing = harness.Inner.BeginOrResumeExclusive(MaintenanceOwner()).Value;

        DataRetentionHostedSweepOutcome outcome = await RunHostedPruneAsync(harness);

        DataRetentionHostedSweepContinuation continuation = Assert.IsType<DataRetentionHostedSweepContinuation>(
            outcome.Continuation);

        LongRunningOperation operation = Assert.IsType<LongRunningOperation>(
            await harness.Store.GetAsync(continuation.OperationId));

        Assert.Equal(DataRetentionHostedSweepDisposition.DeferredForMaintenance, outcome.Disposition);

        Assert.Null(outcome.Result);

        Assert.Equal(0, CheckpointCursor(operation));

        Assert.Equal(harness.Candidate, CheckpointCandidate(operation, 0));

        Assert.True(CheckpointHasPendingJournal(operation));

        Assert.True(File.Exists(harness.Path));

        Assert.Equal(1, await CountAllAsync("UploadedFiles"));

        Assert.Equal(1, operation.AttemptCount);

        await ReopenRetentionGateAsync(harness.Inner, Assert.IsAssignableFrom<IGrimoireClosingOwner>(closing));
    }

    [SkippableFact]
    public async Task MaintenanceDeferralIsNotReconciliationRequired()
    {
        RequireSqlCipher();

        HostedPruneHarness harness = await CreateHostedPruneHarnessAsync();

        IGrimoireClosingOwner? closing = null;

        harness.Admission.BeforeEffectGroupAdmission = () =>
            closing = harness.Inner.BeginOrResumeExclusive(MaintenanceOwner()).Value;

        DataRetentionHostedSweepOutcome outcome = await RunHostedPruneAsync(harness);

        DataRetentionHostedSweepContinuation continuation = Assert.IsType<DataRetentionHostedSweepContinuation>(
            outcome.Continuation);

        LongRunningOperation operation = Assert.IsType<LongRunningOperation>(
            await harness.Store.GetAsync(continuation.OperationId));

        Assert.Equal(DataRetentionHostedSweepDisposition.DeferredForMaintenance, outcome.Disposition);

        Assert.Equal(LongRunningOperationState.Running, operation.State);

        Assert.Null(operation.TerminalErrorCode);

        Assert.DoesNotContain(
            harness.Logger.Entries,
            static entry => entry.Message.Contains("reconciliation", StringComparison.OrdinalIgnoreCase));

        await ReopenRetentionGateAsync(harness.Inner, Assert.IsAssignableFrom<IGrimoireClosingOwner>(closing));
    }

    [SkippableFact]
    public async Task HostedPruneRejectsAWorkLeaseForAnotherProducerBeforeStartingAnOperation()
    {
        RequireSqlCipher();

        HostedPruneHarness harness = await CreateHostedPruneHarnessAsync();

        Assert.True(harness.Admission.TryAcquireWorkLease(
            GrimoireWorkKind.WorkspaceIndexing,
            out IGrimoireWorkLease? admitted));

        await using IGrimoireWorkLease lease = admitted!;

        ArgumentException failure = await Assert.ThrowsAsync<ArgumentException>(
            () => harness.Service.ApplyOrResumeHostedPruneAsync(
                null,
                lease,
                CancellationToken.None));

        Assert.Contains("DataRetentionSweep", failure.Message, StringComparison.Ordinal);

        Assert.Empty(await harness.Store.ListAsync(
            new LongRunningOperationQuery(Kind: LongRunningOperationKinds.DataRetentionPrune)));

        Assert.True(File.Exists(harness.Path));

        Assert.Equal(1, await CountAllAsync("UploadedFiles"));
    }

    [SkippableFact]
    public async Task WinningCandidateDrainsThroughFilesystemDisposition()
    {
        RequireSqlCipher();

        HostedPruneHarness harness = await CreateHostedPruneHarnessAsync();

        AsyncCheckpoint groupDisposal = new();

        harness.Admission.BeforeEffectGroupDisposalAsync = groupDisposal.PauseAsync;

        Task<DataRetentionHostedSweepOutcome> running = RunHostedPruneAsync(harness);

        try
        {
            await groupDisposal.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));

            LongRunningOperation operation = Assert.Single(
                await harness.Store.ListAsync(
                    new LongRunningOperationQuery(Kind: LongRunningOperationKinds.DataRetentionPrune)));

            Assert.False(File.Exists(harness.Path));

            Assert.Equal(0, await CountAllAsync("UploadedFiles"));

            Assert.Equal(1, CheckpointCursor(operation));

            Assert.False(CheckpointHasPendingJournal(operation));

            IGrimoireClosingOwner closing = harness.Inner.BeginOrResumeExclusive(MaintenanceOwner()).Value;

            Task<Result> drain = harness.Inner
                .DrainRequestAndWorkAsync(closing, CancellationToken.None)
                .AsTask();

            Assert.False(drain.IsCompleted);

            groupDisposal.Release.TrySetResult();

            DataRetentionHostedSweepOutcome outcome = await running.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(DataRetentionHostedSweepDisposition.Concluded, outcome.Disposition);

            Assert.True(outcome.Result?.IsSuccess);

            Assert.True((await drain.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);

            await ReopenClosedRetentionGateAsync(harness.Inner, closing);
        }
        finally
        {
            groupDisposal.Release.TrySetResult();

            _ = await Record.ExceptionAsync(async () => await running);
        }
    }

    [SkippableFact]
    public async Task ReopenResumesSameOperationWithoutAttemptIncrement()
    {
        RequireSqlCipher();

        HostedPruneHarness harness = await CreateHostedPruneHarnessAsync();

        IGrimoireClosingOwner? closing = null;

        harness.Admission.BeforeEffectGroupAdmission = () =>
            closing = harness.Inner.BeginOrResumeExclusive(MaintenanceOwner()).Value;

        DataRetentionHostedSweepOutcome deferred = await RunHostedPruneAsync(harness);

        DataRetentionHostedSweepContinuation continuation = Assert.IsType<DataRetentionHostedSweepContinuation>(
            deferred.Continuation);

        LongRunningOperation before = Assert.IsType<LongRunningOperation>(
            await harness.Store.GetAsync(continuation.OperationId));

        await ReopenRetentionGateAsync(harness.Inner, Assert.IsAssignableFrom<IGrimoireClosingOwner>(closing));

        harness.Admission.BeforeEffectGroupAdmission = static () => { };

        DataRetentionHostedSweepOutcome resumed = await RunHostedPruneAsync(harness, continuation);

        LongRunningOperation after = Assert.IsType<LongRunningOperation>(
            await harness.Store.GetAsync(continuation.OperationId));

        Assert.Equal(DataRetentionHostedSweepDisposition.Concluded, resumed.Disposition);

        Assert.True(resumed.Result?.IsSuccess);

        Assert.Equal(continuation.OperationId, resumed.Result?.Value.OperationId);

        Assert.Equal(before.AttemptCount, after.AttemptCount);

        Assert.Equal(LongRunningOperationState.Completed, after.State);

        Assert.False(harness.Ownership.IsClaimedBy(continuation.OperationId, continuation.OwnershipToken));

        Assert.Single(await harness.Store.ListAsync(new LongRunningOperationQuery(
            Kind: LongRunningOperationKinds.DataRetentionPrune)));

        Assert.False(File.Exists(harness.Path));

        Assert.Equal(0, await CountAllAsync("UploadedFiles"));
    }

    [SkippableFact]
    public async Task ResumeClearsRefusedJournalWhenCandidateBecomesProtected()
    {
        RequireSqlCipher();

        HostedPruneHarness harness = await CreateHostedPruneHarnessAsync();

        IGrimoireClosingOwner? closing = null;

        harness.Admission.BeforeEffectGroupAdmission = () =>
            closing = harness.Inner.BeginOrResumeExclusive(MaintenanceOwner()).Value;

        DataRetentionHostedSweepOutcome deferred = await RunHostedPruneAsync(harness);

        DataRetentionHostedSweepContinuation continuation = Assert.IsType<DataRetentionHostedSweepContinuation>(
            deferred.Continuation);

        await ReopenRetentionGateAsync(harness.Inner, Assert.IsAssignableFrom<IGrimoireClosingOwner>(closing));

        harness.Admission.BeforeEffectGroupAdmission = static () => { };

        harness.Settings.Retention.UploadedFiles.Enabled = false;

        DataRetentionHostedSweepOutcome resumed = await RunHostedPruneAsync(harness, continuation);

        LongRunningOperation operation = Assert.IsType<LongRunningOperation>(
            await harness.Store.GetAsync(continuation.OperationId));

        Assert.Equal(DataRetentionHostedSweepDisposition.Concluded, resumed.Disposition);

        Assert.True(resumed.Result?.IsSuccess);

        Assert.Equal(LongRunningOperationState.Completed, operation.State);

        Assert.False(CheckpointHasPendingJournal(operation));

        Assert.Equal(0, CheckpointCursor(operation));

        Assert.True(File.Exists(harness.Path));

        Assert.Equal(1, await CountAllAsync("UploadedFiles"));
    }

    [SkippableFact]
    public async Task ExpiredLeaseResumesOnlyWithExactProcessClaim()
    {
        RequireSqlCipher();

        FakeTimeProvider time = new();

        time.SetUtcNow(new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero));

        HostedPruneHarness harness = await CreateHostedPruneHarnessAsync(time);

        IGrimoireClosingOwner? closing = null;

        harness.Admission.BeforeEffectGroupAdmission = () =>
            closing = harness.Inner.BeginOrResumeExclusive(MaintenanceOwner()).Value;

        DataRetentionHostedSweepOutcome deferred = await RunHostedPruneAsync(harness);

        DataRetentionHostedSweepContinuation continuation = Assert.IsType<DataRetentionHostedSweepContinuation>(
            deferred.Continuation);

        await ReopenRetentionGateAsync(harness.Inner, Assert.IsAssignableFrom<IGrimoireClosingOwner>(closing));

        harness.Admission.BeforeEffectGroupAdmission = static () => { };

        time.Advance(TimeSpan.FromMinutes(10));

        LongRunningOperation beforeRefusal = Assert.IsType<LongRunningOperation>(
            await harness.Store.GetAsync(continuation.OperationId));

        DataRetentionHostedSweepContinuation forged = continuation with { OwnershipToken = Guid.NewGuid() };

        DataRetentionHostedSweepOutcome refused = await RunHostedPruneAsync(harness, forged);

        Assert.Equal(DataRetentionHostedSweepDisposition.Concluded, refused.Disposition);

        Assert.True(refused.Result?.IsFailure);

        Assert.Equal(0, harness.Resumption.Calls);

        LongRunningOperation afterRefusal = Assert.IsType<LongRunningOperation>(
            await harness.Store.GetAsync(continuation.OperationId));

        Assert.Equal(beforeRefusal.Revision, afterRefusal.Revision);

        Assert.Equal(beforeRefusal.HeartbeatAt, afterRefusal.HeartbeatAt);

        Assert.Equal(beforeRefusal.LeaseExpiresAt, afterRefusal.LeaseExpiresAt);

        Assert.Equal(beforeRefusal.AttemptCount, afterRefusal.AttemptCount);

        DataRetentionHostedSweepOutcome resumed = await RunHostedPruneAsync(harness, continuation);

        LongRunningOperation after = Assert.IsType<LongRunningOperation>(
            await harness.Store.GetAsync(continuation.OperationId));

        Assert.Equal(DataRetentionHostedSweepDisposition.Concluded, resumed.Disposition);

        Assert.True(resumed.Result?.IsSuccess);

        Assert.Equal(1, harness.Resumption.Calls);

        Assert.Equal(beforeRefusal.AttemptCount, after.AttemptCount);

        Assert.False(harness.Ownership.IsClaimedBy(continuation.OperationId, continuation.OwnershipToken));
    }

    [SkippableFact]
    public async Task HostedResumeValidatesCompleteDurableOwnerBeforeUsingSameOwnerCapability()
    {
        RequireSqlCipher();

        HostedPruneHarness harness = await CreateHostedPruneHarnessAsync();

        IGrimoireClosingOwner? closing = null;

        harness.Admission.BeforeEffectGroupAdmission = () =>
            closing = harness.Inner.BeginOrResumeExclusive(MaintenanceOwner()).Value;

        DataRetentionHostedSweepOutcome deferred = await RunHostedPruneAsync(harness);

        DataRetentionHostedSweepContinuation continuation = Assert.IsType<DataRetentionHostedSweepContinuation>(
            deferred.Continuation);

        await ReopenRetentionGateAsync(harness.Inner, Assert.IsAssignableFrom<IGrimoireClosingOwner>(closing));

        await ExecuteAsync(
            "UPDATE LongRunningOperations SET Kind = @kind WHERE lower(replace(Id, '-', '')) = @id",
            ("@kind", LongRunningOperationKinds.WorkspaceIndex),
            ("@id", continuation.OperationId.ToString("N")));

        LongRunningOperation before = Assert.IsType<LongRunningOperation>(
            await harness.Store.GetAsync(continuation.OperationId));

        DataRetentionHostedSweepOutcome refused = await RunHostedPruneAsync(harness, continuation);

        Assert.Equal(DataRetentionHostedSweepDisposition.Concluded, refused.Disposition);

        Assert.True(refused.Result?.IsFailure);

        Assert.Equal(0, harness.Resumption.Calls);

        LongRunningOperation after = Assert.IsType<LongRunningOperation>(
            await harness.Store.GetAsync(continuation.OperationId));

        Assert.Equal(before.Revision, after.Revision);

        Assert.Equal(before.HeartbeatAt, after.HeartbeatAt);

        Assert.Equal(before.LeaseExpiresAt, after.LeaseExpiresAt);

        Assert.Equal(before.AttemptCount, after.AttemptCount);

        Assert.False(harness.Ownership.IsClaimedBy(
            continuation.OperationId,
            continuation.OwnershipToken));
    }

    [SkippableFact]
    public async Task HostedResumeWithoutSameOwnerCapabilityFailsClosed()
    {
        RequireSqlCipher();

        HostedPruneHarness harness = await CreateHostedPruneHarnessAsync();

        IGrimoireClosingOwner? closing = null;

        harness.Admission.BeforeEffectGroupAdmission = () =>
            closing = harness.Inner.BeginOrResumeExclusive(MaintenanceOwner()).Value;

        DataRetentionHostedSweepOutcome deferred = await RunHostedPruneAsync(harness);

        DataRetentionHostedSweepContinuation continuation = Assert.IsType<DataRetentionHostedSweepContinuation>(
            deferred.Continuation);

        await ReopenRetentionGateAsync(harness.Inner, Assert.IsAssignableFrom<IGrimoireClosingOwner>(closing));

        HeartbeatCountingOperationStore ordinaryOnly = new(harness.Store);

        DataRetentionService missingCapability = CreateBoundaryService(
            harness.Settings,
            static (_, _) => Task.CompletedTask,
            operationStore: ordinaryOnly,
            timeProvider: harness.TimeProvider,
            operationOwnership: harness.Ownership,
            sameOwnerLeaseResumption: null);

        DataRetentionHostedSweepOutcome refused = await RunHostedPruneAsync(
            harness with { Service = missingCapability },
            continuation);

        LongRunningOperation persisted = Assert.IsType<LongRunningOperation>(
            await harness.Store.GetAsync(continuation.OperationId));

        Assert.Equal(DataRetentionHostedSweepDisposition.Concluded, refused.Disposition);

        Assert.True(refused.Result?.IsFailure);

        Assert.Equal(0, ordinaryOnly.Heartbeats);

        Assert.Equal(1, persisted.AttemptCount);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, persisted.State);

        Assert.Equal(DataRetentionService.RetentionRecoveryTerminalCode, persisted.TerminalErrorCode);

        Assert.False(harness.Ownership.IsClaimedBy(
            continuation.OperationId,
            continuation.OwnershipToken));
    }

    [SkippableFact]
    public async Task WinningCandidateCancellationSurrendersForRecoveryBeforeReleasingClaim()
    {
        RequireSqlCipher();

        HostedPruneHarness harness = await CreateHostedPruneHarnessAsync();

        using CancellationTokenSource stopping = new();

        harness.Admission.BeforeEffectGroupAdmission = stopping.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RunHostedPruneAsync(harness, cancellationToken: stopping.Token));

        LongRunningOperation operation = Assert.Single(
            await harness.Store.ListAsync(
                new LongRunningOperationQuery(Kind: LongRunningOperationKinds.DataRetentionPrune)));

        Assert.Equal(1, harness.Admission.EffectGroupAttempts);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, operation.State);

        Assert.Equal(DataRetentionService.RetentionRecoveryTerminalCode, operation.TerminalErrorCode);

        Assert.True(CheckpointHasPendingJournal(operation));

        Assert.False(harness.Ownership.IsClaimed(operation.Id));
    }

    [SkippableFact]
    public async Task WinningCandidateFailureIsRecoverableRatherThanMaintenanceDeferral()
    {
        RequireSqlCipher();

        HostedPruneHarness harness = await CreateHostedPruneHarnessAsync();

        await CreateDeferredCommitFailureAsync(
            "UploadedFiles",
            "FileId",
            "fail_hosted_uploaded_file_retention_commit");

        DataRetentionHostedSweepOutcome outcome = await RunHostedPruneAsync(harness);

        LongRunningOperation operation = Assert.Single(
            await harness.Store.ListAsync(
                new LongRunningOperationQuery(Kind: LongRunningOperationKinds.DataRetentionPrune)));

        Assert.Equal(DataRetentionHostedSweepDisposition.Concluded, outcome.Disposition);

        Assert.Null(outcome.Continuation);

        Assert.True(outcome.Result?.IsFailure);

        Assert.Equal(1, harness.Admission.EffectGroupAttempts);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, operation.State);

        Assert.Equal(DataRetentionService.RetentionRecoveryTerminalCode, operation.TerminalErrorCode);

        Assert.True(CheckpointHasPendingJournal(operation));

        Assert.True(File.Exists(harness.Path));

        Assert.Equal(1, await CountAllAsync("UploadedFiles"));

        Assert.False(harness.Ownership.IsClaimed(operation.Id));
    }

    [SkippableFact]
    public async Task CandidateFailureCannotBeDowngradedWhenDurableSettlementIsTemporarilyUnavailable()
    {
        RequireSqlCipher();

        HostedPruneHarness harness = await CreateHostedPruneHarnessAsync();

        LeaseSurrenderFailingOperationStore temporarilyUnavailable = new(harness.Store);

        temporarilyUnavailable.FailNextGet();

        DataRetentionService service = CreateBoundaryService(
            harness.Settings,
            static (_, _) => Task.CompletedTask,
            operationStore: temporarilyUnavailable,
            timeProvider: harness.TimeProvider,
            operationOwnership: harness.Ownership,
            sameOwnerLeaseResumption: harness.Resumption);

        await CreateDeferredCommitFailureAsync(
            "UploadedFiles",
            "FileId",
            "fail_unsettled_hosted_retention_commit");

        DataRetentionHostedSweepOutcome outcome = await RunHostedPruneAsync(
            harness with { Service = service });

        LongRunningOperation operation = Assert.Single(
            await harness.Store.ListAsync(
                new LongRunningOperationQuery(Kind: LongRunningOperationKinds.DataRetentionPrune)));

        Assert.True(temporarilyUnavailable.GetFailed);

        Assert.Equal(DataRetentionHostedSweepDisposition.Concluded, outcome.Disposition);

        Assert.True(outcome.Result?.IsFailure);

        Assert.Equal(LongRunningOperationState.Running, operation.State);

        Assert.Null(operation.TerminalErrorCode);

        Assert.True(CheckpointHasPendingJournal(operation));

        Assert.False(harness.Ownership.IsClaimed(operation.Id));
    }

    [SkippableFact]
    public async Task EffectGroupDisposalFailureCannotDowngradeACommittedCandidate()
    {
        RequireSqlCipher();

        HostedPruneHarness harness = await CreateHostedPruneHarnessAsync();

        harness.Admission.BeforeEffectGroupDisposalAsync = static () =>
            ValueTask.FromException(new IOException("effect-group disposal failed"));

        DataRetentionHostedSweepOutcome outcome = await RunHostedPruneAsync(harness);

        LongRunningOperation operation = Assert.Single(
            await harness.Store.ListAsync(
                new LongRunningOperationQuery(Kind: LongRunningOperationKinds.DataRetentionPrune)));

        Assert.Equal(DataRetentionHostedSweepDisposition.Concluded, outcome.Disposition);

        Assert.True(outcome.Result?.IsFailure);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, operation.State);

        Assert.Equal(DataRetentionService.RetentionRecoveryTerminalCode, operation.TerminalErrorCode);

        Assert.False(CheckpointHasPendingJournal(operation));

        Assert.False(File.Exists(harness.Path));

        Assert.Equal(0, await CountAllAsync("UploadedFiles"));

        Assert.False(harness.Ownership.IsClaimed(operation.Id));
    }

    private async Task<HostedPruneHarness> CreateHostedPruneHarnessAsync(
        TimeProvider? timeProvider = null)
    {
        Guid fileId = Guid.NewGuid();

        byte[] bytes = [9, 8, 7, 6];

        string path = Path.Combine(_filesRoot, fileId.ToString("N"));

        await File.WriteAllBytesAsync(path, bytes);

        await SeedUploadedFileAsync(fileId, bytes.LongLength);

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.UploadedFiles = EnabledRule();

        TimeProvider clock = timeProvider ?? TimeProvider.System;

        LongRunningOperationStore store = new(
            _db!,
            TestOrdinaryConnectionFactory.For(_db!));

        LongRunningOperationOwnership ownership = new();

        RecordingSameOwnerLeaseResumption resumption = new(store);

        TestCapturingLogger<DataRetentionService> logger = new();

        DataRetentionService service = CreateBoundaryService(
            settings,
            static (_, _) => Task.CompletedTask,
            logger,
            store,
            clock,
            ownership,
            resumption);

        GrimoireConnectionAdmissionGate inner = new(TimeProvider.System);

        RecordingGrimoireWorkAdmissionGate admission = new(inner);

        return new HostedPruneHarness(
            service,
            store,
            ownership,
            resumption,
            inner,
            admission,
            logger,
            settings,
            clock,
            "file:" + fileId.ToString("D"),
            path);
    }

    private static async Task<DataRetentionHostedSweepOutcome> RunHostedPruneAsync(
        HostedPruneHarness harness,
        DataRetentionHostedSweepContinuation? continuation = null,
        CancellationToken cancellationToken = default)
    {
        Assert.True(harness.Admission.TryAcquireWorkLease(
            GrimoireWorkKind.DataRetentionSweep,
            out IGrimoireWorkLease? admitted));

        await using IGrimoireWorkLease lease = admitted!;

        return await harness.Service.ApplyOrResumeHostedPruneAsync(
            continuation,
            lease,
            cancellationToken);
    }

    private static int CheckpointCursor(LongRunningOperation operation)
    {
        string[] lines = CheckpointLines(operation);

        return int.Parse(lines[2], NumberStyles.None, CultureInfo.InvariantCulture);
    }

    private static string CheckpointCandidate(LongRunningOperation operation, int index)
    {
        string encoded = CheckpointLines(operation)
            .Where(static line => line.StartsWith("C:", StringComparison.Ordinal))
            .ElementAt(index)
            .Split(':', 3)[1];

        return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
    }

    private static bool CheckpointHasPendingJournal(LongRunningOperation operation) =>
        CheckpointLines(operation).Any(static line => line.StartsWith("P:", StringComparison.Ordinal));

    private static string[] CheckpointLines(LongRunningOperation operation)
    {
        byte[] payload = Assert.IsType<byte[]>(operation.CheckpointPayload);

        return Encoding.UTF8.GetString(payload).Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    private static async Task ReopenRetentionGateAsync(
        GrimoireConnectionAdmissionGate gate,
        IGrimoireClosingOwner closing)
    {
        Result drained = await gate.DrainRequestAndWorkAsync(closing, CancellationToken.None);

        Assert.True(drained.IsSuccess, drained.IsFailure ? drained.Error.Message : null);

        await ReopenClosedRetentionGateAsync(gate, closing);
    }

    private static async Task ReopenClosedRetentionGateAsync(
        GrimoireConnectionAdmissionGate gate,
        IGrimoireClosingOwner closing)
    {
        Result<IGrimoireExclusiveClosedLease> closed = await gate.CloseConnectionAdmissionAsync(
            closing,
            CancellationToken.None);

        Assert.True(closed.IsSuccess, closed.IsFailure ? closed.Error.Message : null);

        Result reopened = await closed.Value.CompleteAsync(
            CovenantExclusiveLeaseDisposition.RollbackAndReopen,
            CancellationToken.None);

        Assert.True(reopened.IsSuccess, reopened.IsFailure ? reopened.Error.Message : null);

        await closed.Value.DisposeAsync();

        await closing.DisposeAsync();
    }

    private static CovenantExclusiveRecoveryOwner MaintenanceOwner() =>
        new(Guid.NewGuid(), CovenantExclusiveOperation.CovenantReset, new CovenantDigest(new byte[32]));

    private sealed record HostedPruneHarness(
        DataRetentionService Service,
        LongRunningOperationStore Store,
        LongRunningOperationOwnership Ownership,
        RecordingSameOwnerLeaseResumption Resumption,
        GrimoireConnectionAdmissionGate Inner,
        RecordingGrimoireWorkAdmissionGate Admission,
        TestCapturingLogger<DataRetentionService> Logger,
        ArcanumSettings Settings,
        TimeProvider TimeProvider,
        string Candidate,
        string Path);

    private sealed class RecordingSameOwnerLeaseResumption(
        ILongRunningOperationSameOwnerLeaseResumption inner) : ILongRunningOperationSameOwnerLeaseResumption
    {
        internal int Calls { get; private set; }

        public Task<bool> ResumeSameOwnerLeaseAsync(
            Guid operationId,
            string ownerId,
            DateTimeOffset utcNow,
            DateTimeOffset leaseExpiresAt,
            CancellationToken cancellationToken = default)
        {
            Calls++;

            return inner.ResumeSameOwnerLeaseAsync(
                operationId,
                ownerId,
                utcNow,
                leaseExpiresAt,
                cancellationToken);
        }
    }

    private sealed class AsyncCheckpoint
    {
        internal TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal async ValueTask PauseAsync()
        {
            Reached.TrySetResult();

            await Release.Task;
        }
    }

    private Task CreateDeferredCommitFailureAsync(
        string parentTable,
        string foreignKeyColumn,
        string triggerName) =>
        ExecuteAsync(
            $"""
            CREATE TABLE "{triggerName}_guard" (
                Id TEXT PRIMARY KEY,
                "{foreignKeyColumn}" TEXT NOT NULL,
                FOREIGN KEY ("{foreignKeyColumn}") REFERENCES "{parentTable}"(Id)
                    DEFERRABLE INITIALLY DEFERRED
            );

            CREATE TRIGGER "{triggerName}"
            AFTER DELETE ON "{parentTable}"
            BEGIN
                INSERT INTO "{triggerName}_guard" (Id, "{foreignKeyColumn}")
                VALUES (lower(hex(randomblob(16))), OLD.Id);
            END;
            """);

}
