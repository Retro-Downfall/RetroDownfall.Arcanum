using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class DataRetentionServiceTests
{
    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Request_recovery_retains_the_exact_journal_when_rollback_restore_is_refused(bool session)
    {
        RequireSqlCipher();

        (Guid sessionId, Guid entryId) = await SeedSessionAsync(pinned: false);

        SeededAttachment attachment = await SeedAttachmentAsync(sessionId, entryId);

        await CreateDeferredCommitFailureAsync(session ? "Sessions" : "SessionAttachments", session ? "SessionId" : "AttachmentId", "refused_restore_commit");

        LongRunningOperationStore store = new(_db!, TestOrdinaryConnectionFactory.For(_db!));

        DataRetentionService service = CreateBoundaryService(new ArcanumSettings(), static (_, _) => Task.CompletedTask, operationStore: store);

        DataRetentionRequest request = new(session ? DataRetentionOperation.DeleteSession : DataRetentionOperation.DeleteAttachment, session ? sessionId : attachment.AttachmentId);

        DataRetentionPlan plan = await service.PlanAsync(request);

        Assert.True(IdentityOwnedFileSystemCleanup.TryCapturePath(attachment.AbsolutePath, FileSystemObjectKind.RegularFile, out IdentityOwnedFileSystemArtifact original));

        Func<string, FileHandleMetadata?>? previous = FileHandleIdentityInterop.TryGetPathMetadataNoFollowForTests;

        bool restoreRefused = false;

        Result<DataRetentionApplyResult> result;

        try
        {
            FileHandleIdentityInterop.TryGetPathMetadataNoFollowForTests = path =>
            {
                if (path == attachment.AbsolutePath && !File.Exists(path))
                {
                    restoreRefused = true;

                    return original.Metadata;
                }

                return ReadActualNoFollowMetadata(path);
            };

            result = await service.ApplyAsync(new(request, plan.PlanId));
        }
        finally
        {
            FileHandleIdentityInterop.TryGetPathMetadataNoFollowForTests = previous;
        }

        Assert.True(restoreRefused);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.ReconciliationFailed, result.Error.Code);

        Assert.False(File.Exists(attachment.AbsolutePath));

        Assert.Equal(1, await CountAllAsync("Sessions"));

        Assert.Equal(1, await CountAllAsync("SessionAttachments"));

        LongRunningOperation pending = Assert.Single(await store.ListAsync(new(Limit: 10)));

        Assert.Equal(2, pending.CheckpointVersion);

        Assert.Equal("retention-mutation:" + pending.Id.ToString("N"), pending.CheckpointReference);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, pending.State);

        Assert.Equal(DataRetentionService.RetentionRecoveryTerminalCode, pending.TerminalErrorCode);

        Assert.Null(pending.LeaseOwner);

        Assert.Null(pending.LeaseExpiresAt);

        Assert.Contains(await store.FindExpiredAsync(DateTimeOffset.UtcNow, 10), row => row.Id == pending.Id);

        byte[] checkpoint = pending.CheckpointPayload!.ToArray();

        await using ArcanumDbContext freshDb = _fixture.CreateContext(_dbPath);

        Assert.NotSame(_db!.Database.GetDbConnection(), freshDb.Database.GetDbConnection());

        LongRunningOperationStore freshStore = new(freshDb, TestOrdinaryConnectionFactory.For(freshDb));

        DataRetentionService freshService = FreshRequestRecoveryService(freshDb);

        LongRunningOperationReconciler reconciler = new(freshStore, [new DataRetentionMutationRecoveryHandler(freshService)], TimeProvider.System,
            NullLogger<LongRunningOperationReconciler>.Instance, new LongRunningOperationOwnership());

        LongRunningOperationReconciliationSummary summary = await reconciler.ReconcileNowAsync("refused-request-restore", 10, 1, CancellationToken.None);

        Assert.Equal(1, summary.Failed);

        Assert.Equal(0, summary.Completed);

        Assert.Equal(0, summary.RequiresAttention);

        Assert.Equal(attachment.Bytes, await File.ReadAllBytesAsync(attachment.AbsolutePath));

        Assert.Equal(checkpoint, (await store.GetAsync(pending.Id))!.CheckpointPayload);

        Assert.DoesNotContain(Directory.EnumerateDirectories(_attachmentsRoot, "*", SearchOption.AllDirectories), path => Path.GetFileName(path).StartsWith(".arcanum-retention-", StringComparison.Ordinal));
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Request_recovery_preserves_primary_cancellation_when_rollback_restore_is_refused(bool session)
    {
        RequireSqlCipher();

        (Guid sessionId, Guid entryId) = await SeedSessionAsync(pinned: false);

        SeededAttachment attachment = await SeedAttachmentAsync(sessionId, entryId);

        LongRunningOperationStore store = new(_db!, TestOrdinaryConnectionFactory.For(_db!));

        DataRetentionService service = CreateBoundaryService(new ArcanumSettings(), static (_, _) => Task.CompletedTask, operationStore: store);

        DataRetentionRequest request = new(session ? DataRetentionOperation.DeleteSession : DataRetentionOperation.DeleteAttachment, session ? sessionId : attachment.AttachmentId);

        DataRetentionPlan plan = await service.PlanAsync(request);

        Assert.True(IdentityOwnedFileSystemCleanup.TryCapturePath(attachment.AbsolutePath, FileSystemObjectKind.RegularFile, out IdentityOwnedFileSystemArtifact original));

        Func<string, FileHandleMetadata?>? previous = FileHandleIdentityInterop.TryGetPathMetadataNoFollowForTests;

        bool restoreRefused = false;

        using CancellationTokenSource cancellation = new();

        OperationCanceledException cancelled;

        try
        {
            FileHandleIdentityInterop.TryGetPathMetadataNoFollowForTests = path =>
            {
                if (path.Contains(".arcanum-retention-", StringComparison.Ordinal) && File.Exists(path)) cancellation.Cancel();

                if (path == attachment.AbsolutePath && !File.Exists(path))
                {
                    restoreRefused = true;

                    return original.Metadata;
                }

                return ReadActualNoFollowMetadata(path);
            };

            cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await service.ApplyAsync(new(request, plan.PlanId), cancellation.Token));
        }
        finally
        {
            FileHandleIdentityInterop.TryGetPathMetadataNoFollowForTests = previous;
        }

        Assert.True(restoreRefused);

        Assert.Equal(cancellation.Token, cancelled.CancellationToken);

        Assert.False(File.Exists(attachment.AbsolutePath));

        Assert.Equal(1, await CountAllAsync("Sessions"));

        Assert.Equal(1, await CountAllAsync("SessionAttachments"));

        LongRunningOperation pending = Assert.Single(await store.ListAsync(new(Limit: 10)));

        Assert.Equal(2, pending.CheckpointVersion);

        Assert.Equal("retention-mutation:" + pending.Id.ToString("N"), pending.CheckpointReference);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, pending.State);

        Assert.Equal(DataRetentionService.RetentionRecoveryTerminalCode, pending.TerminalErrorCode);

        Assert.Null(pending.LeaseOwner);

        Assert.Null(pending.LeaseExpiresAt);

        Assert.Contains(await store.FindExpiredAsync(DateTimeOffset.UtcNow, 10), row => row.Id == pending.Id);

        byte[] checkpoint = pending.CheckpointPayload!.ToArray();

        await using ArcanumDbContext freshDb = _fixture.CreateContext(_dbPath);

        Assert.NotSame(_db!.Database.GetDbConnection(), freshDb.Database.GetDbConnection());

        LongRunningOperationStore freshStore = new(freshDb, TestOrdinaryConnectionFactory.For(freshDb));

        DataRetentionService freshService = FreshRequestRecoveryService(freshDb);

        LongRunningOperationReconciler reconciler = new(freshStore, [new DataRetentionMutationRecoveryHandler(freshService)], TimeProvider.System,
            NullLogger<LongRunningOperationReconciler>.Instance, new LongRunningOperationOwnership());

        LongRunningOperationReconciliationSummary summary = await reconciler.ReconcileNowAsync("refused-request-restore", 10, 1, CancellationToken.None);

        Assert.Equal(1, summary.Failed);

        Assert.Equal(0, summary.Completed);

        Assert.Equal(0, summary.RequiresAttention);

        Assert.Equal(attachment.Bytes, await File.ReadAllBytesAsync(attachment.AbsolutePath));

        Assert.Equal(checkpoint, (await store.GetAsync(pending.Id))!.CheckpointPayload);

        Assert.DoesNotContain(Directory.EnumerateDirectories(_attachmentsRoot, "*", SearchOption.AllDirectories), path => Path.GetFileName(path).StartsWith(".arcanum-retention-", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task Request_recovery_prune_retains_pending_quarantine_before_cancellation_can_stop_next_work()
    {
        RequireSqlCipher();

        const bool session = false;

        (Guid sessionId, Guid entryId) = await SeedSessionAsync(pinned: false);

        SeededAttachment attachment = await SeedAttachmentAsync(sessionId, entryId);

        await CreateDeferredCommitFailureAsync(session ? "Sessions" : "SessionAttachments", session ? "SessionId" : "AttachmentId", "refused_restore_commit");

        LongRunningOperationStore store = new(_db!, TestOrdinaryConnectionFactory.For(_db!));

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.Attachments = EnabledRule();

        DataRetentionService service = CreateBoundaryService(settings, static (_, _) => Task.CompletedTask, operationStore: store);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(request);

        Assert.True(IdentityOwnedFileSystemCleanup.TryCapturePath(attachment.AbsolutePath, FileSystemObjectKind.RegularFile, out IdentityOwnedFileSystemArtifact original));

        Func<string, FileHandleMetadata?>? previous = FileHandleIdentityInterop.TryGetPathMetadataNoFollowForTests;

        bool restoreRefused = false;

        Result<DataRetentionApplyResult> result;

        try
        {
            FileHandleIdentityInterop.TryGetPathMetadataNoFollowForTests = path =>
            {
                if (path == attachment.AbsolutePath && !File.Exists(path))
                {
                    restoreRefused = true;

                    return original.Metadata;
                }

                return ReadActualNoFollowMetadata(path);
            };

            result = await service.ApplyAsync(new(request, plan.PlanId));
        }
        finally
        {
            FileHandleIdentityInterop.TryGetPathMetadataNoFollowForTests = previous;
        }

        Assert.True(restoreRefused);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.ReconciliationFailed, result.Error.Code);

        Assert.False(File.Exists(attachment.AbsolutePath));

        Assert.Equal(1, await CountAllAsync("Sessions"));

        Assert.Equal(1, await CountAllAsync("SessionAttachments"));

        LongRunningOperation pending = Assert.Single(await store.ListAsync(new(Limit: 10)));

        Assert.Equal(2, pending.CheckpointVersion);

        Assert.Equal("retention-prune:" + pending.Id.ToString("N"), pending.CheckpointReference);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, pending.State);

        Assert.Equal(DataRetentionService.RetentionRecoveryTerminalCode, pending.TerminalErrorCode);

        Assert.Null(pending.LeaseOwner);

        Assert.Null(pending.LeaseExpiresAt);

        Assert.Contains(await store.FindExpiredAsync(DateTimeOffset.UtcNow, 10), row => row.Id == pending.Id);

        string checkpoint = System.Text.Encoding.UTF8.GetString(pending.CheckpointPayload!);

        Assert.Equal("0", checkpoint.Split('\n')[2]);

        Assert.Contains("\nP:", checkpoint, StringComparison.Ordinal);

        Assert.True(await store.RequestCancellationAsync(pending.Id, pending.Revision, DateTimeOffset.UtcNow));

        await using ArcanumDbContext freshDb = _fixture.CreateContext(_dbPath);

        Assert.NotSame(_db!.Database.GetDbConnection(), freshDb.Database.GetDbConnection());

        LongRunningOperationStore freshStore = new(freshDb, TestOrdinaryConnectionFactory.For(freshDb));

        DataRetentionService freshService = FreshRequestRecoveryService(freshDb);

        LongRunningOperationReconciler reconciler = new(freshStore, [new DataRetentionRecoveryHandler(freshService)], TimeProvider.System,
            NullLogger<LongRunningOperationReconciler>.Instance, new LongRunningOperationOwnership());

        LongRunningOperationReconciliationSummary summary = await reconciler.ReconcileNowAsync("refused-request-restore", 10, 1, CancellationToken.None);

        Assert.Equal(0, summary.Failed);

        Assert.Equal(1, summary.Abandoned);

        Assert.Equal(0, summary.Completed);

        Assert.Equal(0, summary.RequiresAttention);

        Assert.Equal(attachment.Bytes, await File.ReadAllBytesAsync(attachment.AbsolutePath));

        Assert.DoesNotContain(Directory.EnumerateDirectories(_attachmentsRoot, "*", SearchOption.AllDirectories), path => Path.GetFileName(path).StartsWith(".arcanum-retention-", StringComparison.Ordinal));
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Request_recovery_never_restores_on_an_unproved_rollback_before_fresh_database_authority(bool actuallyCommitted)
    {
        RequireSqlCipher();

        (Guid sessionId, Guid entryId) = await SeedSessionAsync(pinned: false);

        SeededAttachment attachment = await SeedAttachmentAsync(sessionId, entryId);

        LongRunningOperationStore store = new(_db!, TestOrdinaryConnectionFactory.For(_db!));

        DataRetentionService service = CreateService(operationStore: store);

        DataRetentionRequest request = new(DataRetentionOperation.DeleteAttachment, attachment.AttachmentId);

        DataRetentionPlan plan = await service.PlanAsync(request);

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        Func<string, FileHandleMetadata?>? previous = FileHandleIdentityInterop.TryGetPathMetadataNoFollowForTests;

        bool nativeBoundary = false;

        Result<DataRetentionApplyResult> result;

        try
        {
            FileHandleIdentityInterop.TryGetPathMetadataNoFollowForTests = path =>
            {
                if (!nativeBoundary && Path.GetFileName(path) == Path.GetFileName(attachment.AbsolutePath)
                    && path.Contains(".arcanum-retention-", StringComparison.Ordinal) && File.Exists(path))
                {
                    nativeBoundary = true;

                    // End the real native transaction after the file moved, without updating the ADO
                    // transaction wrapper. Its later COMMIT and ROLLBACK both report no active native
                    // transaction. Identical failures must not guess which native outcome occurred.
                    Assert.Equal(SQLitePCL.raw.SQLITE_OK, SQLitePCL.raw.sqlite3_exec(connection.Handle, actuallyCommitted ? "COMMIT" : "ROLLBACK"));
                }

                return ReadActualNoFollowMetadata(path);
            };

            result = await service.ApplyAsync(new(request, plan.PlanId));
        }
        finally
        {
            FileHandleIdentityInterop.TryGetPathMetadataNoFollowForTests = previous;
        }

        Assert.True(nativeBoundary);

        Assert.True(result.IsFailure);

        Assert.Equal(actuallyCommitted ? 0 : 1, await CountAllAsync("SessionAttachments"));

        Assert.False(File.Exists(attachment.AbsolutePath));

        LongRunningOperation pending = Assert.Single(await store.ListAsync(new(Limit: 10)));

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, pending.State);

        Assert.Equal(DataRetentionService.RetentionRecoveryTerminalCode, pending.TerminalErrorCode);

        Assert.Contains(await store.FindExpiredAsync(DateTimeOffset.UtcNow, 10), row => row.Id == pending.Id);

        await using ArcanumDbContext freshDb = _fixture.CreateContext(_dbPath);

        Assert.NotSame(_db!.Database.GetDbConnection(), freshDb.Database.GetDbConnection());

        LongRunningOperationStore freshStore = new(freshDb, TestOrdinaryConnectionFactory.For(freshDb));

        DataRetentionService freshService = FreshRequestRecoveryService(freshDb);

        LongRunningOperationReconciler reconciler = new(freshStore, [new DataRetentionMutationRecoveryHandler(freshService)], TimeProvider.System,
            NullLogger<LongRunningOperationReconciler>.Instance, new LongRunningOperationOwnership());

        LongRunningOperationReconciliationSummary summary = await reconciler.ReconcileNowAsync("unproved-request-rollback", 10, 1, CancellationToken.None);

        Assert.Equal(actuallyCommitted ? 1 : 0, summary.Completed);

        Assert.Equal(actuallyCommitted ? 0 : 1, summary.Failed);

        Assert.Equal(0, summary.RequiresAttention);

        Assert.Equal(!actuallyCommitted, File.Exists(attachment.AbsolutePath));

        if (!actuallyCommitted) Assert.Equal(attachment.Bytes, await File.ReadAllBytesAsync(attachment.AbsolutePath));
    }
}
