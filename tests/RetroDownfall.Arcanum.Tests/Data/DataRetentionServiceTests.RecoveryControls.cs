using System.Data;
using System.Data.Common;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class DataRetentionServiceTests
{
    private DataRetentionService FreshRequestRecoveryService(ArcanumDbContext context) =>
        new(context, new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()),
            new LongRunningOperationStore(context, TestOrdinaryConnectionFactory.For(context)), TimeProvider.System,
            NullLogger<DataRetentionService>.Instance, FixtureLabeledArtifactGuard.For(context), _attachmentsRoot, _filesRoot, _logsRoot);

    [SkippableTheory]
    [InlineData("transition")]
    [InlineData("evidence")]
    [InlineData("adopter")]
    public async Task Request_recovery_never_claims_a_handoff_or_overwrites_an_adopter_after_settlement_failure(string fault)
    {
        RequireSqlCipher();

        (Guid sessionId, Guid entryId) = await SeedSessionAsync(pinned: false);

        SeededAttachment attachment = await SeedAttachmentAsync(sessionId, entryId);

        await CreateDeferredCommitFailureAsync("SessionAttachments", "AttachmentId", "settlement_failure_commit");

        LongRunningOperationStore inner = new(_db!, TestOrdinaryConnectionFactory.For(_db!));

        bool restoreRefused = false;

        bool injected = false;

        const string secret = "PRIVATE-RECOVERY-TEXT /private/operator/file";

        HeartbeatCountingOperationStore store = new(inner)
        {
            BeforeGetAsync = (_, _) => fault == "evidence" && restoreRefused ? Task.FromException(new IOException(secret)) : Task.CompletedTask,
            BeforeTransition = state =>
            {
                if (state != LongRunningOperationState.ReconciliationRequired || injected) return;

                injected = true;

                if (fault == "transition") throw new IOException(secret);

                if (fault == "adopter")
                {
                    LongRunningOperation row = Assert.Single(inner.ListAsync(new(Limit: 10)).GetAwaiter().GetResult());

                    Assert.True(inner.TryTransitionAsync(row.Id, row.Revision, row.LeaseOwner, LongRunningOperationState.ReconciliationRequired,
                        DateTimeOffset.UtcNow, DataRetentionService.RetentionRecoveryTerminalCode).GetAwaiter().GetResult());

                    Assert.True(inner.TryAcquireLeaseAsync(row.Id, "independent-adopter", DateTimeOffset.UtcNow,
                        DateTimeOffset.UtcNow.AddMinutes(5)).GetAwaiter().GetResult().Acquired);
                }
            },
        };

        TestCapturingLogger<DataRetentionService> log = new();

        DataRetentionService service = CreateService(operationStore: store, logger: log);

        DataRetentionRequest request = new(DataRetentionOperation.DeleteAttachment, attachment.AttachmentId);

        DataRetentionPlan plan = await service.PlanAsync(request);

        Assert.True(IdentityOwnedFileSystemCleanup.TryCapturePath(attachment.AbsolutePath, FileSystemObjectKind.RegularFile, out IdentityOwnedFileSystemArtifact original));

        Func<string, FileHandleMetadata?>? previous = FileHandleIdentityInterop.TryGetPathMetadataNoFollowForTests;

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

        Assert.Contains("could not be proved", result.Error.Message, StringComparison.Ordinal);

        Assert.DoesNotContain(secret, result.Error.Message, StringComparison.Ordinal);

        LongRunningOperation pending = Assert.Single(await inner.ListAsync(new(Limit: 10)));

        Assert.Equal(2, pending.CheckpointVersion);

        Assert.NotEqual(LongRunningOperationState.Completed, pending.State);

        if (fault == "adopter") Assert.Equal("independent-adopter", pending.LeaseOwner);

        else Assert.StartsWith("data-retention:", pending.LeaseOwner, StringComparison.Ordinal);

        Assert.NotNull(pending.LeaseOwner);

        Assert.False(File.Exists(attachment.AbsolutePath));

        Assert.Equal(1, await CountAllAsync("SessionAttachments"));

        TestLogEntry warning = Assert.Single(log.Entries, entry => entry.Level == LogLevel.Warning);

        Assert.Null(warning.Exception);

        Assert.Contains(warning.Properties!, item => item.Key == "Settled" && Equals(item.Value, false));

        Assert.Contains(warning.Properties!, item => item.Key == "SqliteCode" && Equals(item.Value, 19));

        Assert.Contains(warning.Properties!, item => item.Key == "SqliteExtendedCode" && Equals(item.Value, 787));

        Assert.Contains(warning.Properties!, item => item.Key == "Failure" && item.Value?.ToString() == "Sqlite");

        Assert.DoesNotContain(secret, warning.Message, StringComparison.Ordinal);

        Assert.All(warning.Properties!, item => Assert.DoesNotContain(secret, item.Value?.ToString() ?? "", StringComparison.Ordinal));

        if (fault != "adopter") Assert.Contains(await inner.FindExpiredAsync(DateTimeOffset.UtcNow.AddMinutes(10), 10), row => row.Id == pending.Id);
    }

    [SkippableFact]
    public async Task Request_recovery_disposal_failure_is_not_rollback_authority_and_cannot_replace_the_primary()
    {
        RequireSqlCipher();

        string original = Path.Combine(_attachmentsRoot, "disposal-proof.bin");

        await File.WriteAllBytesAsync(original, [1, 2, 3]);

        Assert.True(IdentityOwnedFileSystemCleanup.TryCapturePath(original, FileSystemObjectKind.RegularFile, out IdentityOwnedFileSystemArtifact file));

        Assert.True(IdentityOwnedFileSystemCleanup.TryQuarantine(file, ".arcanum-retention-", out IdentityOwnedFileSystemQuarantine quarantine));

        DbConnection connection = _db!.Database.GetDbConnection();

        await using DbTransaction inner = await connection.BeginTransactionAsync();

        DisposalFailureTransaction wrapper = new(inner);

        Exception primary = new OperationCanceledException("PRIVATE-PRIMARY-CANCEL");

        MethodInfo rollback = typeof(DataRetentionService).GetMethod("RollbackDisposeAndRestoreAsync", BindingFlags.NonPublic | BindingFlags.Static)!;

        Task operation = Assert.IsAssignableFrom<Task>(rollback.Invoke(null, [connection, wrapper, new[] { quarantine }, primary]));

        await operation;

        object failure = operation.GetType().GetProperty("Result")!.GetValue(operation)!;

        Assert.Same(primary, failure.GetType().GetProperty("Primary")!.GetValue(failure));

        Assert.Equal(true, failure.GetType().GetProperty("RollbackProved")!.GetValue(failure));

        Assert.Equal(false, failure.GetType().GetProperty("DisposalProved")!.GetValue(failure));

        Assert.Equal(false, failure.GetType().GetProperty("RestorationProved")!.GetValue(failure));

        Assert.False(File.Exists(original));

        Assert.True(File.Exists(quarantine.Quarantined.Path));

        Assert.True(IdentityOwnedFileSystemCleanup.TryRestoreQuarantined(quarantine));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Request_recovery_preserves_a_durable_prune_cancellation_after_cleanup_failure(bool proveNextWork)
    {
        RequireSqlCipher();

        (Guid sessionId, Guid entryId) = await SeedSessionAsync(pinned: false);

        SeededAttachment attachment = await SeedAttachmentAsync(sessionId, entryId);

        string tail = Path.Combine(_logsRoot, "audit-20010101.jsonl");

        await File.WriteAllBytesAsync(tail, [1, 2, 3]);

        File.SetLastWriteTimeUtc(tail, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        await CreateDeferredCommitFailureAsync("SessionAttachments", "AttachmentId", "cancel_intent_commit");

        LongRunningOperationStore inner = new(_db!, TestOrdinaryConnectionFactory.For(_db!));

        bool restoreRefused = false;

        bool requested = false;

        HeartbeatCountingOperationStore store = new(inner)
        {
            BeforeGetAsync = async (id, _) =>
            {
                if (!restoreRefused || requested) return;

                requested = true;

                LongRunningOperation row = (await inner.GetAsync(id))!;

                Assert.True(await inner.RequestCancellationAsync(id, row.Revision, DateTimeOffset.UtcNow));
            },
        };

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.Attachments = EnabledRule();

        settings.Retention.AuditLogs = EnabledRule();

        DataRetentionService service = CreateService(settings, operationStore: store);

        DataRetentionPlan plan = await service.PlanAsync(new(DataRetentionOperation.Prune));

        Assert.Equal(2, plan.CandidateIds.Length);

        Assert.Equal("attachment:" + attachment.AttachmentId.ToString("D"), plan.CandidateIds[0]);

        Assert.True(IdentityOwnedFileSystemCleanup.TryCapturePath(attachment.AbsolutePath, FileSystemObjectKind.RegularFile, out IdentityOwnedFileSystemArtifact original));

        Func<string, FileHandleMetadata?>? previous = FileHandleIdentityInterop.TryGetPathMetadataNoFollowForTests;

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

            var applied = await service.ApplyAsync(new(new(DataRetentionOperation.Prune), plan.PlanId));

            Assert.True(applied.IsFailure);
        }
        finally
        {
            FileHandleIdentityInterop.TryGetPathMetadataNoFollowForTests = previous;
        }

        Assert.True(requested);

        Assert.True(restoreRefused);

        Assert.True(File.Exists(tail));

        LongRunningOperation pending = Assert.Single(await inner.ListAsync(new(Limit: 10)));

        if (!proveNextWork) Assert.Equal(LongRunningOperationState.Cancelling, pending.State);

        await ExecuteAsync("DROP TRIGGER cancel_intent_commit;");

        await using ArcanumDbContext freshDb = _fixture.CreateContext(_dbPath);

        LongRunningOperationStore freshStore = new(freshDb, TestOrdinaryConnectionFactory.For(freshDb));

        FakeTimeProvider clock = new();

        clock.Advance(TimeSpan.FromMinutes(10));

        LongRunningOperationReconciler reconciler = new(freshStore, [new DataRetentionRecoveryHandler(FreshRequestRecoveryService(freshDb))], clock,
            NullLogger<LongRunningOperationReconciler>.Instance, new LongRunningOperationOwnership());

        var summary = await reconciler.ReconcileNowAsync("durable-prune-cancellation", 10, 1, CancellationToken.None);

        Assert.True(summary.Abandoned == 1, $"{summary}; originalExists={File.Exists(attachment.AbsolutePath)}; nextCandidateExists={File.Exists(tail)}");

        Assert.Equal(0, summary.Completed);

        Assert.Equal(0, summary.RequiresAttention);

        Assert.Equal(attachment.Bytes, await File.ReadAllBytesAsync(attachment.AbsolutePath));

        Assert.True(File.Exists(tail));
    }

    [SkippableTheory]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task Request_recovery_atomic_cancellation_only_preserves_the_exact_prune_recovery_transition(bool prune, bool recoveryCode, bool recoveryTarget)
    {
        RequireSqlCipher();

        LongRunningOperationStore store = new(_db!, TestOrdinaryConnectionFactory.For(_db!));

        DateTimeOffset now = DateTimeOffset.UtcNow;

        var created = await store.CreateAsync(new(prune ? LongRunningOperationKinds.DataRetentionPrune : LongRunningOperationKinds.DataRetentionMutation,
            prune ? LongRunningOperationRecoveryPolicy.RestartIdempotently : LongRunningOperationRecoveryPolicy.ReconcileAndComplete, "Cancellation contract.", now));

        var lease = await store.TryAcquireLeaseAsync(created.Id, "cancel-contract-owner", now, now.AddMinutes(5));

        Assert.True(lease.Acquired);

        Assert.True(await store.RequestCancellationAsync(created.Id, lease.Operation.Revision, now));

        LongRunningOperation cancelled = (await store.GetAsync(created.Id))!;

        LongRunningOperationState target = recoveryTarget ? LongRunningOperationState.ReconciliationRequired : LongRunningOperationState.Failed;

        string code = recoveryCode ? DataRetentionService.RetentionRecoveryTerminalCode : ErrorCodes.Data.Conflict;

        Assert.True(await store.TryTransitionAsync(created.Id, cancelled.Revision, cancelled.LeaseOwner, target, now, code));

        LongRunningOperation actual = (await store.GetAsync(created.Id))!;

        Assert.Equal(prune && recoveryCode && recoveryTarget ? LongRunningOperationState.Cancelling : target, actual.State);

        Assert.Null(actual.LeaseOwner);

        Assert.Null(actual.LeaseExpiresAt);

        Assert.Equal(code, actual.TerminalErrorCode);

        Assert.Equal(cancelled.Revision + 1, actual.Revision);

        if (recoveryTarget) Assert.Null(actual.CompletedAt);

        else Assert.NotNull(actual.CompletedAt);

        if (prune && recoveryCode && recoveryTarget) Assert.Contains(await store.FindExpiredAsync(now, 10), row => row.Id == created.Id);
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Request_recovery_atomic_cancellation_never_bypasses_owner_or_revision(bool wrongOwner)
    {
        RequireSqlCipher();

        LongRunningOperationStore store = new(_db!, TestOrdinaryConnectionFactory.For(_db!));

        DateTimeOffset now = DateTimeOffset.UtcNow;

        var created = await store.CreateAsync(new(LongRunningOperationKinds.DataRetentionPrune, LongRunningOperationRecoveryPolicy.RestartIdempotently,
            "Cancellation CAS fence.", now));

        var lease = await store.TryAcquireLeaseAsync(created.Id, "owner-before-cancel", now, now.AddMinutes(5));

        Assert.True(lease.Acquired);

        Assert.True(await store.RequestCancellationAsync(created.Id, lease.Operation.Revision, now));

        LongRunningOperation before = (await store.GetAsync(created.Id))!;

        Assert.False(await store.TryTransitionAsync(created.Id, wrongOwner ? before.Revision : before.Revision - 1,
            wrongOwner ? "other-owner" : before.LeaseOwner, LongRunningOperationState.ReconciliationRequired,
            now, DataRetentionService.RetentionRecoveryTerminalCode));

        Assert.Equal(before, await store.GetAsync(created.Id));
    }

    [SkippableFact]
    public async Task Request_recovery_keeps_the_process_claim_through_its_durable_settlement_tail()
    {
        RequireSqlCipher();

        (Guid sessionId, Guid entryId) = await SeedSessionAsync(pinned: false);

        SeededAttachment attachment = await SeedAttachmentAsync(sessionId, entryId);

        await CreateDeferredCommitFailureAsync("SessionAttachments", "AttachmentId", "claim_tail_commit");

        LongRunningOperationStore inner = new(_db!, TestOrdinaryConnectionFactory.For(_db!));

        LongRunningOperationOwnership ownership = new();

        bool observed = false;

        HeartbeatCountingOperationStore store = new(inner)
        {
            BeforeTransition = state =>
            {
                if (state != LongRunningOperationState.ReconciliationRequired) return;

                observed = true;

                LongRunningOperation row = Assert.Single(inner.ListAsync(new(Limit: 10)).GetAwaiter().GetResult());

                bool stolen = ownership.TryClaim(row.Id, out Guid token);

                if (stolen) ownership.Release(row.Id, token);

                Assert.False(stolen);
            },
        };

        var result = await CreateService(operationStore: store, operationOwnership: ownership)
            .ApplyAsync(new(new(DataRetentionOperation.DeleteAttachment, attachment.AttachmentId)));

        Assert.True(result.IsFailure);

        Assert.True(observed);

        LongRunningOperation pending = Assert.Single(await inner.ListAsync(new(Limit: 10)));

        Assert.True(ownership.TryClaim(pending.Id, out Guid after));

        Assert.True(ownership.Release(pending.Id, after));
    }

    [SkippableFact]
    public async Task Request_recovery_checkpoint_free_failure_is_not_invented_as_durable_effects()
    {
        RequireSqlCipher();

        (Guid sessionId, Guid entryId) = await SeedSessionAsync(pinned: false);

        SeededAttachment attachment = await SeedAttachmentAsync(sessionId, entryId);

        LongRunningOperationStore inner = new(_db!, TestOrdinaryConnectionFactory.For(_db!));

        HeartbeatCountingOperationStore store = new(inner)
        {
            BeforeSaveCheckpoint = _ => throw new IOException("PRIVATE-UNSAVED-CHECKPOINT"),
        };

        var result = await CreateService(operationStore: store).ApplyAsync(new(new(DataRetentionOperation.DeleteAttachment, attachment.AttachmentId)));

        Assert.True(result.IsFailure);

        LongRunningOperation failed = Assert.Single(await inner.ListAsync(new(Limit: 10)));

        Assert.Equal(LongRunningOperationState.Failed, failed.State);

        Assert.Equal(0, failed.CheckpointVersion);

        Assert.Null(failed.CheckpointPayload);

        Assert.Null(failed.CheckpointReference);

        Assert.DoesNotContain(await inner.FindExpiredAsync(DateTimeOffset.UtcNow.AddMinutes(10), 10), row => row.Id == failed.Id);

        Assert.Equal(attachment.Bytes, await File.ReadAllBytesAsync(attachment.AbsolutePath));

        Assert.Equal(1, await CountAllAsync("SessionAttachments"));
    }

    [SkippableFact]
    public async Task Request_recovery_primary_native_canary_never_enters_new_diagnostic_values()
    {
        RequireSqlCipher();

        (Guid sessionId, Guid entryId) = await SeedSessionAsync(pinned: false);

        SeededAttachment attachment = await SeedAttachmentAsync(sessionId, entryId);

        const string secret = "PRIVATE-NATIVE-ERROR /private/operator/content";

        Microsoft.Data.Sqlite.SqliteConnection connection = (Microsoft.Data.Sqlite.SqliteConnection)_db!.Database.GetDbConnection();

        connection.CreateFunction<long>("private_retention_failure", () => throw new InvalidOperationException(secret));

        await ExecuteAsync("CREATE TEMP TRIGGER private_retention_trigger AFTER DELETE ON SessionAttachments BEGIN SELECT private_retention_failure(); END;");

        TestCapturingLogger<DataRetentionService> log = new();

        var result = await CreateService(logger: log).ApplyAsync(new(new(DataRetentionOperation.DeleteAttachment, attachment.AttachmentId)));

        Assert.True(result.IsFailure);

        Assert.DoesNotContain(secret, result.Error.Message, StringComparison.Ordinal);

        TestLogEntry entry = Assert.Single(log.Entries);

        Assert.Null(entry.Exception);

        Assert.Contains(entry.Properties!, item => item.Key == "SqliteCode" && Equals(item.Value, 1));

        Assert.Contains(entry.Properties!, item => item.Key == "Failure" && item.Value?.ToString() == "Sqlite");

        Assert.DoesNotContain(secret, entry.Message, StringComparison.Ordinal);

        Assert.All(entry.Properties!, item => Assert.DoesNotContain(secret, item.Value?.ToString() ?? "", StringComparison.Ordinal));

        Assert.Equal(attachment.Bytes, await File.ReadAllBytesAsync(attachment.AbsolutePath));

        Assert.Equal(1, await CountAllAsync("SessionAttachments"));
    }

    private sealed class DisposalFailureTransaction(DbTransaction inner) : DbTransaction
    {
        public override IsolationLevel IsolationLevel => inner.IsolationLevel;

        protected override DbConnection? DbConnection => inner.Connection;
        public override void Commit() => inner.Commit();
        public override void Rollback() => inner.Rollback();
        public override Task RollbackAsync(CancellationToken cancellationToken = default) => inner.RollbackAsync(cancellationToken);
        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();

            throw new IOException("PRIVATE-DISPOSAL-TEXT /private/unknown");
        }
    }
}
