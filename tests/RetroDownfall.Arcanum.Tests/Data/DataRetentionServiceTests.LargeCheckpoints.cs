using System.Globalization;

using System.Text;

using Microsoft.EntityFrameworkCore;

using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Infrastructure.Operations;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class DataRetentionServiceTests
{
    [SkippableFact]
    public async Task RecoverPruneAsync_LegacyLargeCheckpoint_PreservesFrozenSelection()
    {
        RequireSqlCipher();

        await SeedLargeWorkspacePruneAsync(10_001, 0);

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.WorkspaceIndexes = EnabledRule(days: 30);

        DataRetentionService service = CreateService(settings);

        DataRetentionPlan plan = await service.PlanAsync(new(DataRetentionOperation.Prune));

        Assert.Equal(10_001, plan.CandidateIds.Length);

        LongRunningOperationStore operations = new(_db!, TestOrdinaryConnectionFactory.For(_db!));

        DateTimeOffset now = DateTimeOffset.UtcNow;

        const string owner = "large-checkpoint-recovery";

        LongRunningOperation operation = await operations.CreateAsync(new(
            LongRunningOperationKinds.DataRetentionPrune,
            LongRunningOperationRecoveryPolicy.RestartIdempotently,
            "Interrupted large prune.", now));

        Assert.True((await operations.TryAcquireLeaseAsync(operation.Id, owner, now, now.AddMinutes(5))).Acquired);

        StringBuilder checkpoint = new("ARCADATA2\n" + plan.PlanId + "\n10000\nG:" + EncodePruneTime(plan.GeneratedAt) + "\n");

        foreach (string candidate in plan.CandidateIds)
        {
            checkpoint.Append("C:").Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(candidate)))
                .Append(':').Append(EncodePruneTime(plan.GeneratedAt.AddDays(-30))).Append('\n');
        }

        Assert.True(await operations.SaveCheckpointAsync(operation.Id, owner, 0, 2,
            Encoding.UTF8.GetBytes(checkpoint.ToString()), null, "Frozen large plan.", now));

        LongRunningOperationRecoveryResult recovered = await service.RecoverPruneAsync(
            Assert.IsType<LongRunningOperation>(await operations.GetAsync(operation.Id)), CancellationToken.None);

        Assert.Equal(LongRunningOperationState.Completed, recovered.State);

        Assert.Equal(10_000, await CountLargeWorkspacePruneAsync());
    }

    [SkippableTheory]
    [InlineData(10_001, 0)]
    [InlineData(3_000, 1_800)]
    public async Task ApplyAsync_LargePrune_CheckpointRemainsBoundedAndRecoversCompleteFrozenPlan(int count, int padding)
    {
        RequireSqlCipher();

        await SeedLargeWorkspacePruneAsync(count, padding);

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.WorkspaceIndexes = EnabledRule(days: 30);

        LongRunningOperationStore operations = new(_db!, TestOrdinaryConnectionFactory.For(_db!));

        HeartbeatCountingOperationStore interruptedStore = new(operations)
        {
            BeforeSaveCheckpoint = version =>
            {
                if (version != 0)
                {
                    throw new OperationCanceledException("Interrupt after the initial durable checkpoint.");
                }
            },
        };

        DataRetentionService service = CreateService(settings, operationStore: interruptedStore);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(request);

        Assert.Equal(count, plan.CandidateIds.Length);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            Result<DataRetentionApplyResult> result = await service.ApplyAsync(new(request, plan.PlanId));

            Assert.True(result.IsSuccess, result.Error.Code + ": " + result.Error.Message);
        });

        LongRunningOperation interrupted = Assert.Single(await operations.ListAsync(new(Kind: LongRunningOperationKinds.DataRetentionPrune)));

        byte[] checkpoint = Assert.IsType<byte[]>(interrupted.CheckpointPayload);

        Assert.InRange(checkpoint.Length, 1, 256);

        await using (var measured = _db!.Database.GetDbConnection().CreateCommand())
        {
            measured.CommandText = "SELECT COUNT(*), SUM(length(Payload)), MAX(length(Payload)) FROM data_retention_prune_snapshots";

            await using var reader = await measured.ExecuteReaderAsync();

            Assert.True(await reader.ReadAsync());

            Assert.InRange(reader.GetInt64(2), 1, 256 * 1024);

            if (padding > 0)
            {
                Assert.True(reader.GetInt64(1) > 5 * 1024 * 1024);
            }

            Console.WriteLine($"Candidates={count}; snapshot={reader.GetInt64(1)} bytes in {reader.GetInt64(0)} chunks; cursor={checkpoint.Length} bytes.");
        }

        // A candidate refreshed after the snapshot and a newly eligible row are neither new authority
        // nor permission to move the original age boundary when recovery runs under a shorter rule.
        string refreshed = plan.CandidateIds[^1]["workspace:".Length..];

        await ExecuteAsync("UPDATE workspace_file_chunks SET IndexedAt = @at WHERE ChunkId = @id",
            ("@at", DateTimeOffset.UtcNow.AddDays(-10).ToString("o", CultureInfo.InvariantCulture)), ("@id", refreshed));

        await SeedLargeWorkspacePruneAsync(1, padding + 1);

        settings.Retention.WorkspaceIndexes = EnabledRule(days: 1);

        await ExecuteAsync("""
            CREATE TRIGGER reject_retention_snapshot_rewrite BEFORE INSERT ON data_retention_prune_snapshots
            BEGIN SELECT RAISE(ABORT, 'Recovery must reuse its immutable snapshot.'); END
            """);

        LongRunningOperationReconciler reconciler = new(
            operations,
            [new DataRetentionRecoveryHandler(CreateService(settings))],
            TimeProvider.System,
            NullLogger<LongRunningOperationReconciler>.Instance,
            new LongRunningOperationOwnership());

        await reconciler.ReconcileNowAsync("large-recovery", 10, 1, CancellationToken.None);

        LongRunningOperation recovered = Assert.IsType<LongRunningOperation>(await operations.GetAsync(interrupted.Id));

        Assert.Equal(LongRunningOperationState.Completed, recovered.State);

        Assert.Equal(2, await CountLargeWorkspacePruneAsync());

        DateTimeOffset now = DateTimeOffset.UtcNow;

        Assert.NotNull(await operations.TryStartSingleFlightAsync(new(
            LongRunningOperationKinds.DataRetentionPrune,
            LongRunningOperationRecoveryPolicy.RestartIdempotently,
            "Next prune can acquire the single flight.", now), "next-prune", now, now.AddMinutes(5)));

        await ExecuteAsync("DELETE FROM LongRunningOperations WHERE Id = @id", ("@id", interrupted.Id.ToString("N")));

        Assert.Equal(0, await CountAsync("data_retention_prune_snapshots", "OperationId", interrupted.Id.ToString("N")));
    }

    [SkippableTheory]
    [InlineData("missing", "missing or its digest")]
    [InlineData("foreign", "missing or its digest")]
    [InlineData("oversize", "bounded size")]
    [InlineData("line", "bounded encoding")]
    [InlineData("digest", "missing or its digest")]
    public async Task RecoverPruneAsync_LargeSnapshot_InvalidSegmentsNeverReplan(string corruption, string expectedMessage)
    {
        RequireSqlCipher();

        await SeedLargeWorkspacePruneAsync(1_001, 0);

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.WorkspaceIndexes = EnabledRule(days: 30);

        LongRunningOperationStore operations = new(_db!, TestOrdinaryConnectionFactory.For(_db!));

        HeartbeatCountingOperationStore interruptedStore = new(operations)
        {
            BeforeSaveCheckpoint = version =>
            {
                if (version != 0)
                {
                    throw new OperationCanceledException();
                }
            },
        };

        DataRetentionService service = CreateService(settings, operationStore: interruptedStore);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(request);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ApplyAsync(new(request, plan.PlanId)));

        LongRunningOperation interrupted = Assert.Single(await operations.ListAsync(new(Kind: LongRunningOperationKinds.DataRetentionPrune)));

        if (corruption == "foreign")
        {
            interrupted = interrupted with { Id = Guid.NewGuid(), LeaseOwner = "foreign-owner" };
        }
        else if (corruption == "missing")
        {
            await ExecuteAsync("DELETE FROM data_retention_prune_snapshots WHERE OperationId = @id AND Part = 0",
                ("@id", interrupted.Id.ToString("N")));

            interrupted = interrupted with { LeaseOwner = "recovery-owner" };
        }
        else
        {
            await ExecuteAsync("DROP TRIGGER data_retention_prune_snapshots_guard_update");

            await ExecuteAsync("PRAGMA ignore_check_constraints = ON");

            byte[] payload = corruption switch
            {
                "oversize" => new byte[256 * 1024 + 1],
                "line" => Encoding.UTF8.GetBytes(new string('A', 12 * 1024 + 1)),
                _ => Encoding.UTF8.GetBytes("ARCADATA9\n"),
            };

            await ExecuteAsync("UPDATE data_retention_prune_snapshots SET Payload = @payload WHERE OperationId = @id AND Part = 0",
                ("@payload", payload), ("@id", interrupted.Id.ToString("N")));

            interrupted = interrupted with { LeaseOwner = "recovery-owner" };
        }

        long before = await CountLargeWorkspacePruneAsync();

        InvalidDataException failure = await Assert.ThrowsAsync<InvalidDataException>(
            () => CreateService(settings).RecoverPruneAsync(interrupted, CancellationToken.None));

        Assert.Contains(expectedMessage, failure.Message, StringComparison.Ordinal);

        Assert.Equal(before, await CountLargeWorkspacePruneAsync());
    }

    [SkippableFact]
    public async Task ApplyAsync_LargePrune_SnapshotCommitFailurePrecedesEveryCandidateEffect()
    {
        RequireSqlCipher();

        await SeedLargeWorkspacePruneAsync(10_001, 0);

        await ExecuteAsync("""
            CREATE TRIGGER reject_second_snapshot_segment BEFORE INSERT ON data_retention_prune_snapshots
            WHEN NEW.Part = 1 BEGIN SELECT RAISE(ABORT, 'Interrupted snapshot transaction.'); END
            """);

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.WorkspaceIndexes = EnabledRule(days: 30);

        DataRetentionService service = CreateService(settings);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(request);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(new(request, plan.PlanId));

        Assert.True(result.IsFailure);

        Assert.Equal(10_001, await CountLargeWorkspacePruneAsync());

        LongRunningOperationStore operations = new(_db!, TestOrdinaryConnectionFactory.For(_db!));

        LongRunningOperation interrupted = Assert.Single(await operations.ListAsync(new(Kind: LongRunningOperationKinds.DataRetentionPrune)));

        Assert.Null(interrupted.CheckpointPayload);

        Assert.Equal(0, await CountAsync("data_retention_prune_snapshots", "OperationId", interrupted.Id.ToString("N")));
    }

    [SkippableFact]
    public async Task RecoverPruneAsync_LargeSnapshot_ReconcilesPendingFileJournalBeforeContinuing()
    {
        RequireSqlCipher();

        await SeedLargeWorkspacePruneAsync(1_001, 0);

        Guid fileId = Guid.NewGuid();

        string path = Path.Combine(_filesRoot, fileId.ToString("N"));

        await File.WriteAllBytesAsync(path, [7, 8, 9]);

        await SeedUploadedFileAsync(fileId, 3);

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.WorkspaceIndexes = EnabledRule(days: 30);

        settings.Retention.UploadedFiles = EnabledRule(days: 30);

        LongRunningOperationStore operations = new(_db!, TestOrdinaryConnectionFactory.For(_db!));

        int saves = 0;

        HeartbeatCountingOperationStore interruptedStore = new(operations)
        {
            BeforeSaveCheckpoint = _ =>
            {
                if (++saves == 3)
                {
                    throw new OperationCanceledException("Interrupt before pending-journal clear.");
                }
            },
        };

        DataRetentionService service = CreateService(settings, operationStore: interruptedStore);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(request);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ApplyAsync(new(request, plan.PlanId)));

        LongRunningOperation interrupted = Assert.Single(await operations.ListAsync(new(Kind: LongRunningOperationKinds.DataRetentionPrune)));

        Assert.Equal(3, interrupted.CheckpointVersion);

        Assert.Contains("\nP:", Encoding.UTF8.GetString(Assert.IsType<byte[]>(interrupted.CheckpointPayload)));

        LongRunningOperationReconciler reconciler = new(operations,
            [new DataRetentionRecoveryHandler(CreateService(settings))], TimeProvider.System,
            NullLogger<LongRunningOperationReconciler>.Instance, new LongRunningOperationOwnership());

        await reconciler.ReconcileNowAsync("file-journal-recovery", 10, 1, CancellationToken.None);

        Assert.Equal(LongRunningOperationState.Completed,
            Assert.IsType<LongRunningOperation>(await operations.GetAsync(interrupted.Id)).State);

        Assert.False(File.Exists(path));

        Assert.Equal(0, await CountLargeWorkspacePruneAsync());
    }

    private async Task SeedLargeWorkspacePruneAsync(int count, int padding)
    {
        await ExecuteAsync(
            """
            WITH RECURSIVE numbers(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM numbers WHERE n < @count)
            INSERT INTO workspace_file_chunks
                (ChunkId, WorkspacePath, RelativePath, ChunkIndex, Content, CharOffset, CharLength, FileLastWriteTime, IndexedAt)
            SELECT @prefix || printf('%08d', n), '/large-workspace', 'old.cs', n, 'old', 0, 3, @at, @at
            FROM numbers
            """,
            ("@count", count), ("@prefix", new string('x', padding)), ("@at", OldTimestamp));
    }

    private async Task<long> CountLargeWorkspacePruneAsync()
    {
        await using var command = _db!.Database.GetDbConnection().CreateCommand();

        command.CommandText = "SELECT COUNT(*) FROM workspace_file_chunks WHERE WorkspacePath = '/large-workspace'";

        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static string EncodePruneTime(DateTimeOffset value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value.ToString("o", CultureInfo.InvariantCulture)));
}
