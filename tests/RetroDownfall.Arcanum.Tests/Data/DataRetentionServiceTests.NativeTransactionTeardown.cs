using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class DataRetentionServiceTests
{
    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Request_recovery_uploaded_prune_does_not_restore_from_an_ended_native_transaction(bool actuallyCommitted)
    {
        RequireSqlCipher();

        Guid fileId = Guid.NewGuid();

        byte[] bytes = [1, 2, 3, 4];

        string original = Path.Combine(_filesRoot, fileId.ToString("N"));

        await File.WriteAllBytesAsync(original, bytes);

        await SeedUploadedFileAsync(fileId, bytes.Length);

        var settings = CreatePruneSettings();

        settings.Retention.UploadedFiles = EnabledRule();

        LongRunningOperationStore store = new(_db!, TestOrdinaryConnectionFactory.For(_db!));

        DataRetentionService service = CreateService(settings, operationStore: store);

        var plan = await service.PlanAsync(new(DataRetentionOperation.Prune));

        Assert.Single(plan.CandidateIds);

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        Func<string, FileHandleMetadata?>? previous = FileHandleIdentityInterop.TryGetPathMetadataNoFollowForTests;

        bool nativeEnded = false;

        try
        {
            FileHandleIdentityInterop.TryGetPathMetadataNoFollowForTests = path =>
            {
                if (!nativeEnded && Path.GetFileName(path) == Path.GetFileName(original)
                    && path.Contains(".arcanum-retention-", StringComparison.Ordinal) && File.Exists(path))
                {
                    nativeEnded = true;

                    Assert.Equal(0, SQLitePCL.raw.sqlite3_get_autocommit(connection.Handle));

                    Assert.Equal(SQLitePCL.raw.SQLITE_OK, SQLitePCL.raw.sqlite3_exec(connection.Handle, actuallyCommitted ? "COMMIT" : "ROLLBACK"));
                }

                return ReadActualNoFollowMetadata(path);
            };

            var result = await service.ApplyAsync(new(new(DataRetentionOperation.Prune), plan.PlanId));

            Assert.True(result.IsFailure);
        }
        finally
        {
            FileHandleIdentityInterop.TryGetPathMetadataNoFollowForTests = previous;
        }

        Assert.True(nativeEnded);

        Assert.Equal(actuallyCommitted ? 0 : 1, await CountAllAsync("UploadedFiles"));

        Assert.False(File.Exists(original));

        LongRunningOperation pending = Assert.Single(await store.ListAsync(new(Limit: 10)));

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, pending.State);

        Assert.Equal(DataRetentionService.RetentionRecoveryTerminalCode, pending.TerminalErrorCode);

        Assert.Contains(await store.FindExpiredAsync(DateTimeOffset.UtcNow, 10), row => row.Id == pending.Id);

        Assert.True(await store.RequestCancellationAsync(pending.Id, pending.Revision, DateTimeOffset.UtcNow));

        await using ArcanumDbContext freshDb = _fixture.CreateContext(_dbPath);

        Assert.NotSame(_db!.Database.GetDbConnection(), freshDb.Database.GetDbConnection());

        LongRunningOperationStore freshStore = new(freshDb, TestOrdinaryConnectionFactory.For(freshDb));

        DataRetentionService freshService = FreshRequestRecoveryService(freshDb);

        LongRunningOperationReconciler reconciler = new(freshStore, [new DataRetentionRecoveryHandler(freshService)], TimeProvider.System,
            NullLogger<LongRunningOperationReconciler>.Instance, new LongRunningOperationOwnership());

        var summary = await reconciler.ReconcileNowAsync("native-uploaded-cleanup", 10, 1, CancellationToken.None);

        Assert.Equal(1, summary.Abandoned);

        Assert.Equal(0, summary.Completed);

        Assert.Equal(0, summary.RequiresAttention);

        Assert.Equal(!actuallyCommitted, File.Exists(original));

        if (!actuallyCommitted) Assert.Equal(bytes, await File.ReadAllBytesAsync(original));
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Request_recovery_legacy_factory_does_not_restore_from_an_ended_native_transaction(bool actuallyCommitted)
    {
        RequireSqlCipher();

        (Guid sessionId, Guid entryId) = await SeedSessionAsync(pinned: false);

        SeededAttachment attachment = await SeedAttachmentAsync(sessionId, entryId);

        DataRetentionService service = CreateService();

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        Func<string, FileHandleMetadata?>? previous = FileHandleIdentityInterop.TryGetPathMetadataNoFollowForTests;

        bool nativeEnded = false;

        LongRunningOperationReconciliationSummary first;

        try
        {
            FileHandleIdentityInterop.TryGetPathMetadataNoFollowForTests = path =>
            {
                if (!nativeEnded && Path.GetFileName(path) == Path.GetFileName(attachment.AbsolutePath)
                    && path.Contains(".arcanum-cleanup-", StringComparison.Ordinal) && File.Exists(path))
                {
                    nativeEnded = true;

                    Assert.Equal(0, SQLitePCL.raw.sqlite3_get_autocommit(connection.Handle));

                    Assert.Equal(SQLitePCL.raw.SQLITE_OK, SQLitePCL.raw.sqlite3_exec(connection.Handle, actuallyCommitted ? "COMMIT" : "ROLLBACK"));
                }

                return ReadActualNoFollowMetadata(path);
            };

            (first, _) = await ReconcileFactoryResetV0Async(service, "native-factory-uncertainty");
        }
        finally
        {
            FileHandleIdentityInterop.TryGetPathMetadataNoFollowForTests = previous;
        }

        Assert.True(nativeEnded);

        Assert.Equal(actuallyCommitted ? 0 : 1, await CountAllAsync("SessionAttachments"));

        Assert.Equal(0, first.Completed);

        Assert.Equal(1, first.RequiresAttention);

        Assert.False(File.Exists(attachment.AbsolutePath));

        LongRunningOperationStore store = new(_db!, TestOrdinaryConnectionFactory.For(_db!));

        LongRunningOperation pending = Assert.Single(await store.ListAsync(new(Limit: 10)));

        Assert.Equal(DataRetentionService.RetentionRecoveryTerminalCode, pending.TerminalErrorCode);

        Assert.Contains(await store.FindExpiredAsync(DateTimeOffset.UtcNow, 10), row => row.Id == pending.Id);

        // V0's existing whole-factory intent may restart idempotently; unlike an ordinary mutation,
        // it does not adopt a fresh per-file journal or infer a narrow deletion from this failure.
        await using ArcanumDbContext freshDb = _fixture.CreateContext(_dbPath);

        Assert.NotSame(_db!.Database.GetDbConnection(), freshDb.Database.GetDbConnection());

        LongRunningOperationStore freshStore = new(freshDb, TestOrdinaryConnectionFactory.For(freshDb));

        DataRetentionService freshService = FreshRequestRecoveryService(freshDb);

        LongRunningOperationReconciler reconciler = new(freshStore, [new DataRetentionFactoryResetRecoveryHandler(freshService)], TimeProvider.System,
            NullLogger<LongRunningOperationReconciler>.Instance, new LongRunningOperationOwnership());

        var summary = await reconciler.ReconcileNowAsync("native-factory-fresh-recovery", 10, 1, CancellationToken.None);

        Assert.Equal(1, summary.Completed);

        Assert.Equal(0, summary.RequiresAttention);

        Assert.False(File.Exists(attachment.AbsolutePath));

        Assert.Equal(0, await CountAllAsync("SessionAttachments"));
    }
}
