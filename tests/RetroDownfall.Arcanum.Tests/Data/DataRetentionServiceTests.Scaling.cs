using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SQLitePCL;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class DataRetentionServiceTests
{
    [SkippableFact]
    public async Task PlanAsync_EntryEmbeddingMembership_AllocatesLinearly()
    {
        RequireSqlCipher();

        const int count = 2_000;

        _ = await SeedEntryEmbeddingBatchAsync(count);

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.SessionEntryEmbeddings = EnabledRule();

        DataRetentionService service = CreateService(settings);

        long before = GC.GetTotalAllocatedBytes(true);

        DataRetentionPlan plan = await service.PlanAsync(new(DataRetentionOperation.Prune));

        long allocated = GC.GetTotalAllocatedBytes(true) - before;

        Assert.Equal(count, plan.CandidateIds.Length);

        Console.WriteLine($"Embedding candidates={count}; allocated={allocated} bytes.");

        Assert.True(allocated < count * 50_000L,
            $"Embedding planning allocated {allocated} bytes for {count} candidates; the 100 MB budget excludes quadratic candidate-key formatting.");
    }

    [SkippableTheory]
    [InlineData("entries")]
    [InlineData("embeddings")]
    [InlineData("attachments")]
    public async Task PlanAsync_EntryFamily_MaterializesReservationConflictsOnce(string family)
    {
        RequireSqlCipher();

        (Guid session, Guid entry) = await SeedSessionAsync(pinned: false);

        await SeedEntryEmbeddingAsync(entry);

        _ = await SeedAttachmentAsync(session, entry);

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.Entries = family == "entries" ? EnabledRule() : new();

        settings.Retention.SessionEntryEmbeddings = family == "embeddings" ? EnabledRule() : new();

        settings.Retention.Attachments = family == "attachments" ? EnabledRule() : new();

        List<string> statements = [];

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        raw.sqlite3_trace(connection.Handle, (object _, string sql) => statements.Add(sql), null);

        DataRetentionPlan plan;

        try
        {
            plan = await CreateService(settings).PlanAsync(new(DataRetentionOperation.Prune));
        }
        finally
        {
            raw.sqlite3_trace(connection.Handle, (strdelegate_trace)null!, null);
        }

        Assert.Single(plan.CandidateIds);

        string[] queries = [.. statements.Where(sql => sql.Contains("FROM BudgetReservations reservation", StringComparison.Ordinal)
            && (sql.Contains("FROM Entries entry", StringComparison.Ordinal)
                || sql.Contains("FROM entry_embeddings embedding", StringComparison.Ordinal)
                || sql.Contains("FROM SessionAttachments attachment", StringComparison.Ordinal)))];

        Assert.Equal(2, queries.Length);

        foreach (string sql in queries)
        {
            await AssertNoCorrelatedLedgerReadsAsync(connection, sql, "reservation");
        }
    }

    [SkippableFact]
    public async Task PlanAsync_Accounting_GroupsAdjustmentMembershipOnceWithoutDoubleCounting()
    {
        RequireSqlCipher();

        Guid run = Guid.NewGuid();

        await SeedCompletedInferenceRunAsync(run, null);

        string billable = Guid.NewGuid().ToString("D");

        await ExecuteAsync("""
            INSERT INTO BillableOperations(Id,RunId,OperationType,Provider,Model,Purpose,StartedAt,CompletedAt,
                InputTokens,OutputTokens,CachedTokens,PricingSnapshotJson,Status)
            VALUES(@billable,@run,0,'test','test','test',@at,@at,0,0,0,'{}',0);
            INSERT INTO CostAdjustments(Id,RunId,BillableOperationId,Reason,CreatedAt)
            VALUES('direct',@run,NULL,'test',@at),('indirect',NULL,@billable,'test',@at),
                ('both',@run,@billable,'test',@at);
            """, ("@run", run.ToString("D")), ("@billable", billable), ("@at", OldTimestamp));

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.Accounting = EnabledRule();

        List<string> statements = [];

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        raw.sqlite3_trace(connection.Handle, (object _, string sql) => statements.Add(sql), null);

        DataRetentionPlan plan;

        try
        {
            plan = await CreateService(settings).PlanAsync(new(DataRetentionOperation.Prune));
        }
        finally
        {
            raw.sqlite3_trace(connection.Handle, (strdelegate_trace)null!, null);
        }

        Assert.Single(plan.CandidateIds);

        Assert.Equal(3, Assert.Single(plan.Items, item => item.DataClass == RetentionDataClass.CostAdjustments).Rows);

        string sql = Assert.Single(statements, sql => sql.Contains("SELECT run.Id, run.Status,", StringComparison.Ordinal));

        await AssertNoCorrelatedLedgerReadsAsync(connection, sql, "billable", "reservation", "adjustment");
    }

    [SkippableTheory]
    [InlineData(256)]
    [InlineData(1_024)]
    public async Task PlanAsync_AccountingLedger_SqlWorkGrowsWithTheLedger(int count)
    {
        RequireSqlCipher();

        await ExecuteAsync("""
            WITH RECURSIVE rows(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM rows WHERE n < @count)
            INSERT INTO InferenceRuns(Id,RequestId,Surface,Purpose,StartedAt,CompletedAt,Status)
            SELECT printf('%032x',n),printf('request-%d',n),'test','test',@at,@at,1 FROM rows;
            INSERT INTO BillableOperations(Id,RunId,OperationType,Provider,Model,Purpose,StartedAt,CompletedAt,
                InputTokens,OutputTokens,CachedTokens,PricingSnapshotJson,Status)
            SELECT 'billable-'||Id,Id,0,'test','test','test',@at,@at,0,0,0,'{}',0 FROM InferenceRuns;
            INSERT INTO BudgetReservations(Id,RunId,BudgetPeriod,Status,ExpiresAt,CreatedAt,UpdatedAt)
            SELECT 'reservation-'||Id,Id,'2000-01-01',2,@at,@at,@at FROM InferenceRuns;
            INSERT INTO CostAdjustments(Id,RunId,BillableOperationId,Reason,CreatedAt)
            SELECT 'adjustment-'||Id,Id,'billable-'||Id,'test',@at FROM InferenceRuns;
            """, ("@count", count), ("@at", OldTimestamp));

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.Accounting = EnabledRule();

        DataRetentionService service = CreateService(settings);

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        long instructions = 0;

        long budget = count * 2_000L;

        raw.sqlite3_progress_handler(connection.Handle, 1_000, (object _) =>
        {
            instructions += 1_000;

            return instructions > budget ? 1 : 0;
        }, null);

        DataRetentionPlan plan;

        try
        {
            plan = await service.PlanAsync(new(DataRetentionOperation.Prune));
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 9)
        {
            Assert.Fail($"Accounting planning exceeded {budget} SQLite VM instructions for {count} run chains.");

            throw;
        }
        finally
        {
            raw.sqlite3_progress_handler(connection.Handle, 0, null, null);
        }

        Assert.Equal(count, plan.CandidateIds.Length);

        Assert.Equal(count * 4, plan.Rows);

        Console.WriteLine($"Accounting chains={count}; SQLite VM instructions~{instructions}; instructions/chain={instructions / count}.");
    }

    [SkippableTheory]
    [InlineData(256)]
    [InlineData(1_024)]
    public async Task PlanAsync_EntryLedger_DoesNotRescanUnrelatedOutstandingReservations(int count)
    {
        RequireSqlCipher();

        Guid[] entries = await SeedEntryEmbeddingBatchAsync(count);

        Guid session = await ReadEntrySessionIdAsync(entries[0]);

        for (int index = 0; index < 20; index++)
        {
            await SeedCompletedInferenceRunAsync(Guid.NewGuid(), session);
        }

        await ExecuteAsync("""
            WITH RECURSIVE rows(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM rows WHERE n < 2048)
            INSERT INTO InferenceRuns(Id,RequestId,SessionId,Surface,Purpose,StartedAt,CompletedAt,Status)
            SELECT printf('%032x',n),printf('request-%d',n),@foreignSession,'test','test',@at,@at,@completed FROM rows;
            INSERT INTO BudgetReservations(Id,RunId,BudgetPeriod,Status,ExpiresAt,CreatedAt,UpdatedAt)
            SELECT 'reservation-'||Id,Id,'2000-01-01',@reserved,@at,@at,@at FROM InferenceRuns WHERE SessionId = @foreignSession;
            """, ("@foreignSession", Guid.NewGuid().ToString("D")), ("@at", OldTimestamp),
            ("@reserved", (int)BudgetReservationStatus.Reserved), ("@completed", (int)InferenceRunStatus.Completed));

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.Entries = EnabledRule();

        DataRetentionService service = CreateService(settings);

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        long instructions = 0;

        long budget = count * 5_000L + 200_000;

        raw.sqlite3_progress_handler(connection.Handle, 1_000, (object _) =>
        {
            instructions += 1_000;

            return instructions > budget ? 1 : 0;
        }, null);

        DataRetentionPlan plan;

        try
        {
            plan = await service.PlanAsync(new(DataRetentionOperation.Prune));
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 9)
        {
            Assert.Fail($"Entry planning exceeded {budget} SQLite VM instructions for {count} entries, 20 own runs and 2048 unrelated outstanding reservations.");

            throw;
        }
        finally
        {
            raw.sqlite3_progress_handler(connection.Handle, 0, null, null);
        }

        Assert.Equal(count, plan.CandidateIds.Length);

        Console.WriteLine($"Entry candidates={count}; own runs=20; unrelated outstanding reservations=2048; SQLite VM instructions~{instructions}.");
    }

    private static async Task AssertNoCorrelatedLedgerReadsAsync(SqliteConnection connection, string sql, params string[] aliases)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "EXPLAIN QUERY PLAN " + sql;

        List<(int Id, int Parent, string Detail)> rows = [];

        await using (SqliteDataReader reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                rows.Add((reader.GetInt32(0), reader.GetInt32(1), reader.GetString(3)));
            }
        }

        foreach (var row in rows.Where(row => aliases.Any(alias => row.Detail.StartsWith("SCAN " + alias + " ", StringComparison.Ordinal)
                     || row.Detail == "SCAN " + alias
                     || row.Detail.StartsWith("SEARCH " + alias + " ", StringComparison.Ordinal))))
        {
            int parent = row.Parent;

            while (rows.FindIndex(value => value.Id == parent) is int index && index >= 0)
            {
                var ancestor = rows[index];

                Assert.False(ancestor.Detail.Contains("CORRELATED", StringComparison.Ordinal),
                    $"Repeated ledger work: {row.Detail} under {ancestor.Detail}.\n" + string.Join("\n", rows));

                parent = ancestor.Parent;
            }
        }
    }

    [SkippableFact]
    public async Task ApplyAsync_JournalBearingUploads_WriteOnlyBoundedCursorBytesPerCandidate()
    {
        RequireSqlCipher();

        const int count = 1_001;

        for (int index = 0; index < count; index++)
        {
            Guid id = Guid.NewGuid();

            await SeedUploadedFileAsync(id, 1);

            await File.WriteAllBytesAsync(Path.Combine(_filesRoot, id.ToString("N")), [1]);
        }

        await ExecuteAsync("""
            CREATE TEMP TABLE retention_checkpoint_write_sizes(Bytes INTEGER NOT NULL, Journal INTEGER NOT NULL);
            CREATE TEMP TRIGGER measure_retention_checkpoint_writes
            AFTER UPDATE OF CheckpointPayload ON LongRunningOperations
            WHEN NEW.CheckpointPayload IS NOT NULL
            BEGIN
                INSERT INTO retention_checkpoint_write_sizes(Bytes,Journal)
                VALUES(length(NEW.CheckpointPayload),instr(CAST(NEW.CheckpointPayload AS TEXT),char(10)||'P:') > 0);
            END;
            """);

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.UploadedFiles = EnabledRule();

        DataRetentionService service = CreateService(settings);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(request);

        Assert.Equal(count, plan.CandidateIds.Length);

        var result = await service.ApplyAsync(new(request, plan.PlanId));

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.True(result.Value.Reconciled);

        Assert.Equal(count, result.Value.RowsDeleted);

        Assert.Equal(count, result.Value.FilesDeleted);

        Assert.Equal(0, await CountAllAsync("UploadedFiles"));

        Assert.Equal(count * 2 + 1, await CampaignScalarCountAsync("SELECT COUNT(*) FROM retention_checkpoint_write_sizes"));

        Assert.Equal(count, await CampaignScalarCountAsync("SELECT SUM(Journal) FROM retention_checkpoint_write_sizes"));

        long checkpointBytes = await CampaignScalarCountAsync("SELECT SUM(Bytes) FROM retention_checkpoint_write_sizes");

        long snapshotBytes = await CampaignScalarCountAsync("SELECT COALESCE(SUM(length(Payload)),0) FROM data_retention_prune_snapshots");

        Console.WriteLine($"Journal-bearing files={count}; checkpoint writes={count * 2 + 1}; cursor bytes={checkpointBytes}; immutable snapshot bytes={snapshotBytes}.");

        Assert.InRange(await CampaignScalarCountAsync("SELECT MAX(Bytes) FROM retention_checkpoint_write_sizes"), 1, 2_048);

        Assert.True(checkpointBytes + snapshotBytes < count * 2_048L,
            $"Journal-bearing sweep wrote {checkpointBytes} cursor bytes plus {snapshotBytes} snapshot bytes; candidate lists must not repeat on every save.");
    }

    [SkippableTheory]
    [InlineData("D", false)]
    [InlineData("D", true)]
    [InlineData("N", false)]
    [InlineData("N", true)]
    [InlineData("D", null)]
    [InlineData("N", null)]
    public async Task ApplyAsync_UploadedIdentityLookup_PreservesAllAcceptedGuidSpellings(string format, bool? upper)
    {
        RequireSqlCipher();

        Guid id = Guid.Parse("AbCdEf12-3456-7890-AbCd-Ef1234567890");

        string text = id.ToString(format);

        text = upper is true ? text.ToUpperInvariant() : upper is false ? text : text.Replace('a', 'A').Replace('e', 'E');

        await SeedUploadedFileAsync(id, 0);

        await ExecuteAsync("UPDATE UploadedFiles SET Id = @spelling", ("@spelling", text));

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.UploadedFiles = EnabledRule();

        DataRetentionService service = CreateService(settings);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(request);

        Assert.Single(plan.CandidateIds);

        var result = await service.ApplyAsync(new(request, plan.PlanId));

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(1, result.Value.RowsDeleted);

        Assert.Equal(0, await CountAllAsync("UploadedFiles"));
    }

    [Fact]
    public void RetentionSchema_PreservesCapturedVersionTwentyTwoFingerprint()
    {
        Assert.Equal(CoreSchemaVersionTwentyTwoFixture.PublishedFingerprint, CoreSchemaVersionTwentyTwoFixture.Fingerprint);
    }

    [SkippableFact]
    public async Task FactoryReset_ReconcilesTheCompleteCampaignSummaryClosure()
    {
        RequireSqlCipher();

        (Guid contribution, Guid campaign) = await SeedRetentionSummaryAsync(true, OldTimestamp);

        (Guid rollup, _) = await SeedRetentionSummaryAsync(false, OldTimestamp, campaign);

        Guid session = await ReadContributionOwnerAsync(contribution);

        await ExecuteAsync("""
            INSERT INTO campaign_rollup_sources(RollupArtifactId,SessionId,CampaignId,RollupRevision,
                ContributionArtifactId,ContributionRevision,ContentDigest,SensitivityDigest,SummarizedThroughSequence)
            SELECT @rollup,SessionId,CampaignId,1,ArtifactId,Revision,ContentDigest,SensitivityDigest,SummarizedThroughSequence
            FROM campaign_contribution_artifacts WHERE ArtifactId = @contribution;
            """, ("@rollup", Canonical(rollup)), ("@contribution", Canonical(contribution)));

        using (CovenantSqliteConnectionInitializer.Instance.Authorize(
                   (SqliteConnection)_db!.Database.GetDbConnection(), CovenantSqliteAuthorizationKind.TurnCapacityMutation))
        {
            await ExecuteAsync("""
                INSERT INTO assistant_finalization_capacity_reservations(ReservationId,SessionId,AssistantEntryId,
                    OriginCode,ClaimId,StateCode,CreatedAtUtc,StateChangedAtUtc)
                VALUES('capacity',@session,'assistant',1,'claim',1,@at,@at);
                INSERT INTO session_turn_claims(ClaimId,OriginInstallationId,OriginRestoreEpoch,ClientTurnId,SessionId,
                    SurfaceCode,RequestDigest,DependencyDigest,StateCode,PreRequestHistoryRevision,InputSensitivityRevision,
                    ExpectedCurrentSensitivityRevision,FinalizationReservationId,CheckpointRevision,CompletedStepMask,
                    ExecutorId,CreatedAtUtc)
                VALUES('claim','installation',0,'client',@session,1,zeroblob(32),zeroblob(32),1,0,0,0,'capacity',0,0,'executor',@at);
                INSERT INTO campaign_maintenance_checkpoints(ClaimId,StepCode,CampaignId,SessionId,ExecutorId,
                    ExpectedOutputRevision,CheckpointStateCode,InputSourceGeneration,InputManifestDigest,
                    CheckpointRevision,UpdatedAtUtc)
                VALUES('claim',5,@campaign,@session,'executor',1,1,0,zeroblob(32),0,@at);
                """, ("@session", Canonical(session)), ("@campaign", Canonical(campaign)), ("@at", OldTimestamp));
        }

        DataRetentionService service = CreateService();

        DataRetentionPlan plan = await service.PlanAsync(new(DataRetentionOperation.FactoryReset));

        Assert.Equal(2, plan.Items.Where(item => item.DataClass == RetentionDataClass.CampaignSummaries).Sum(item => item.Rows));

        Assert.Equal(4, plan.Items.Where(item => item.DataClass == RetentionDataClass.CampaignSummaries).Sum(item => item.DerivedRecords));

        (var recovery, _) = await ReconcileFactoryResetV0Async(service, "campaign-closure-factory-reset");

        Assert.Equal(1, recovery.Completed);

        Assert.Equal(0, recovery.RequiresAttention);

        foreach (string table in new[] { "campaign_rollup_state", "campaign_rollup_sources", "campaign_rollup_artifacts",
                     "campaign_contribution_state", "campaign_contribution_artifacts", "campaign_maintenance_checkpoints" })
        {
            Assert.Equal(0, await CountAllAsync(table));
        }

        Assert.Equal(1, await CountAsync("Campaigns", "Id", Canonical(campaign)));
    }

    [SkippableFact]
    public async Task FactoryReset_RemovesCampaignRollupContentAndState()
    {
        RequireSqlCipher();

        (Guid _, Guid campaign) = await SeedRetentionSummaryAsync(false, OldTimestamp);

        DataRetentionService service = CreateService();

        DataRetentionPlan plan = await service.PlanAsync(new(DataRetentionOperation.FactoryReset));

        (var recovery, _) = await ReconcileFactoryResetV0Async(service, "campaign-factory-reset");

        Assert.Equal(1, recovery.Completed);

        Assert.Equal(0, recovery.RequiresAttention);

        Assert.Equal(0, await CountAllAsync("campaign_rollup_artifacts"));

        Assert.Equal(0, await CountAllAsync("campaign_rollup_state"));

        Assert.Equal(1, await CountAsync("Campaigns", "Id", Canonical(campaign)));

        Assert.Contains(plan.Items, item => item.DataClass == RetentionDataClass.CampaignSummaries && item.Rows == 1);
    }
}
