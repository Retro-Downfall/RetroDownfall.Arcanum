using RetroDownfall.Arcanum.Infrastructure.Data;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class DataRetentionServiceTests
{
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Factory_reset_counts_and_clears_Guild_budget_tree_even_without_foreign_keys(bool foreignKeysDisabled)
    {
        RequireSqlCipher();

        await SeedBudgetResetAccountsAsync();

        if (foreignKeysDisabled)
        {
            await ExecuteAsync("PRAGMA foreign_keys=OFF");
        }

        DataRetentionPlan plan = await CreateService().PlanAsync(new(DataRetentionOperation.FactoryReset));

        Assert.Equal(1, await CampaignScalarCountAsync("SELECT count(*) FROM guild_cohort_settlement_clock"));

        Assert.Contains(plan.Items, item => item.DataClass == RetentionDataClass.GuildEvidence && item.DerivedRecords == 5);

        var recovered = await ReconcileFactoryResetV0Async(CreateService(), "guild-budget-reset");

        Assert.Equal(1, recovered.Summary.Completed);

        Assert.Equal(0, await CampaignScalarCountAsync("SELECT count(*) FROM guild_budget_accounts"));

        Assert.Equal(0, await CampaignScalarCountAsync("SELECT count(*) FROM guild_cohort_settlement_clock"));
    }

    [SkippableFact]
    public async Task Factory_reset_detects_reinserted_Guild_budget_evidence_without_foreign_keys()
    {
        RequireSqlCipher();

        await SeedBudgetResetAccountsAsync();

        await ExecuteAsync("PRAGMA foreign_keys=OFF");

        await ExecuteAsync("""
            CREATE TEMP TRIGGER retain_budget_account AFTER DELETE ON guild_budget_accounts
            BEGIN
                INSERT INTO guild_budget_accounts(AccountId,OwnerKindCode,OwnerId,RootAccountId,ParentAccountId,UsdLimit,TokenLimit,Closed,SettlementReceiptId,Revision)
                VALUES(OLD.AccountId,OLD.OwnerKindCode,OLD.OwnerId,OLD.RootAccountId,OLD.ParentAccountId,OLD.UsdLimit,OLD.TokenLimit,OLD.Closed,OLD.SettlementReceiptId,OLD.Revision);
            END;
            """);

        var recovered = await ReconcileFactoryResetV0Async(CreateService(), "guild-budget-reinserted");

        Assert.Equal(1, recovered.Summary.RequiresAttention);

        Assert.Equal(0, recovered.Summary.Completed);

        Assert.Equal(2, await CampaignScalarCountAsync("SELECT count(*) FROM guild_budget_accounts"));
    }

    [SkippableTheory]
    [InlineData(0, false, false, true)]
    [InlineData(1, false, false, true)]
    [InlineData(2, false, false, true)]
    [InlineData(2, true, true, false)]
    [InlineData(2, true, false, false)]
    public async Task Guild_physical_evidence_protects_terminal_inference_accounting_until_current_snapshot_is_complete(
        int state, bool copied, bool unknown, bool pinned)
    {
        RequireSqlCipher();

        await SeedGuildPhysicalRetentionAsync(state, copied, unknown, authentic: true);

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.Accounting = new() { Enabled = true, Days = 30 };

        var service = CreateService(settings);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(request);

        string candidate = "accounting:" + BudgetPhysicalRun.ToString("D");

        Assert.Equal(!pinned, plan.CandidateIds.Contains(candidate, StringComparer.Ordinal));

        Assert.Equal(pinned, plan.Conflicts.Any(conflict => conflict.ResourceId == BudgetPhysicalRun.ToString("D")));

        if (!pinned)
        {
            var applied = await service.ApplyAsync(new(request, plan.PlanId));

            Assert.True(applied.IsSuccess, applied.Error.Message);

            Assert.Equal(0, await CampaignScalarCountAsync("SELECT count(*) FROM InferenceRuns"));

            Assert.Equal(0, await CampaignScalarCountAsync("SELECT count(*) FROM BillableOperations"));

            Assert.Equal(1, await CampaignScalarCountAsync("SELECT count(*) FROM guild_budget_evidence"));
        }
        else
        {
            Assert.Equal(1, await CampaignScalarCountAsync("SELECT count(*) FROM BillableOperations"));
        }
    }

    [SkippableTheory]
    [InlineData("none")]
    [InlineData("missing-evidence")]
    [InlineData("orphan-reference")]
    public async Task Completed_unrestricted_unknown_usage_releases_stopped_quest_content(string mutation)
    {
        RequireSqlCipher();

        await SeedGuildPhysicalRetentionAsync(2, true, true, authentic: true);

        await ExecuteAsync("""
            INSERT INTO guild_result_receipts(QuestId,ResultId,Digest,OutcomeCode,NoDispatchProven,WorkStoppedProven,MutationId,AcceptedRevision,CreatedAtUtc)
            VALUES('11111111-1111-1111-1111-111111111111','99999999-9999-9999-9999-999999999999',zeroblob(32),4,0,1,
                '88888888-8888-8888-8888-888888888888',1,'2000-01-01T00:00:00.0000000Z');
            UPDATE guild_budget_accounts SET Closed=1,SettlementReceiptId='99999999-9999-9999-9999-999999999999',Revision=Revision+1;
            INSERT INTO guild_content(ContentId,ContentKindCode,SourceRecordId,Revision,Digest,MimeType,Body,SensitivityCode,ProvenanceModeCode,CreatedAtUtc)
            VALUES('CCCCCCCC-CCCC-CCCC-CCCC-CCCCCCCCCCCC',1,'11111111-1111-1111-1111-111111111111',1,@digest,'text/plain',X'',0,0,'2000-01-01T00:00:00.0000000Z');
            """, ("@digest", SHA256.HashData([])));

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.GuildContent.Enabled = true;

        settings.Retention.GuildContent.Days = 1;

        var service = CreateService(settings);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(request);

        Assert.Contains("guild-content:CCCCCCCC-CCCC-CCCC-CCCC-CCCCCCCCCCCC", plan.CandidateIds);

        if (mutation != "none")
        {
            if (mutation == "missing-evidence") await ExecuteAsync("DELETE FROM guild_budget_evidence");
            else
            {
                // A restored/corrupt store can contain an orphan reference only when FK enforcement
                // was absent. The final content proof must not silently drop that reference.
                await ExecuteAsync("""
                    PRAGMA foreign_keys=OFF;
                    INSERT INTO guild_result_receipts(QuestId,ResultId,Digest,ContentId,ContentRevision,MimeType,ByteLength,
                        OutcomeCode,NoDispatchProven,WorkStoppedProven,MutationId,AcceptedRevision,CreatedAtUtc)
                    VALUES('AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA','BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB',zeroblob(32),
                        'CCCCCCCC-CCCC-CCCC-CCCC-CCCCCCCCCCCC',1,'text/plain',0,4,0,1,
                        'EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE',1,'2000-01-01T00:00:00.0000000Z')
                    """);
            }

            var refused = await service.DeleteGuildCandidateAsync(Guid.Parse("CCCCCCCC-CCCC-CCCC-CCCC-CCCCCCCCCCCC"),
                false, DateTimeOffset.UtcNow.AddDays(-1), default);

            Assert.Equal(0, refused.Rows);

            Assert.Equal(1, await CampaignScalarCountAsync("SELECT count(*) FROM guild_content WHERE ContentKindCode=1"));
        }
        else
        {
            var applied = await service.ApplyAsync(new(request, plan.PlanId));

            Assert.True(applied.IsSuccess, applied.Error.Message);

            Assert.Equal(0, await CampaignScalarCountAsync("SELECT count(*) FROM guild_content WHERE ContentKindCode=1"));
        }
    }

    [SkippableFact]
    public async Task Physical_bill_delete_rechecks_changed_reservation_in_its_writer_transaction()
    {
        RequireSqlCipher();

        Guid reservation = Guid.NewGuid();

        await SeedGuildPhysicalRetentionAsync(2, true, false, authentic: true, reservation: reservation);

        await ExecuteAsync("""
            INSERT INTO BudgetReservations(Id,RunId,BudgetPeriod,Status,ExpiresAt,CreatedAt,UpdatedAt,ReservedUsd,ReconciledUsd)
            VALUES(@id,@run,'2000-01-01',1,@at,@at,@at,'2','1.5')
            """, ("@id", reservation.ToString("N")), ("@run", BudgetPhysicalRun.ToString("N")), ("@at", OldTimestamp));

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.Accounting = new() { Enabled = true, Days = 30 };

        var service = CreateService(settings);

        DataRetentionPlan plan = await service.PlanAsync(new(DataRetentionOperation.Prune));

        Assert.Contains("accounting:" + BudgetPhysicalRun.ToString("D"), plan.CandidateIds);

        await ExecuteAsync("UPDATE BudgetReservations SET ReconciledUsd='1.4' WHERE Id=@id", ("@id", reservation.ToString("N")));

        var refused = await service.DeleteAccountingCandidateAsync(BudgetPhysicalRun, DateTimeOffset.UtcNow.AddDays(-30), default);

        Assert.Equal(0, refused.Rows);

        Assert.Equal(1, await CampaignScalarCountAsync("SELECT count(*) FROM BillableOperations"));

        await ExecuteAsync("UPDATE BudgetReservations SET ReconciledUsd='1.5' WHERE Id=@id", ("@id", reservation.ToString("N")));

        var accepted = await service.DeleteAccountingCandidateAsync(BudgetPhysicalRun, DateTimeOffset.UtcNow.AddDays(-30), default);

        Assert.True(accepted.Rows > 0);

        Assert.True(accepted.Reconciled);

        Assert.Equal(1, await CampaignScalarCountAsync("SELECT count(*) FROM BudgetReservations"));
    }

    [SkippableFact]
    public async Task Pruning_a_bill_preserves_its_owned_guild_reservation()
    {
        RequireSqlCipher();

        Guid reservation = Guid.NewGuid();

        await SeedGuildPhysicalRetentionAsync(2, true, false, authentic: true, reservation: reservation);

        await ExecuteAsync("""
            INSERT INTO BudgetReservations(Id,RunId,BudgetPeriod,Status,ExpiresAt,CreatedAt,UpdatedAt,ReservedUsd,ReconciledUsd)
            VALUES(@id,@run,'2000-01-01',1,@at,@at,@at,'2','1.5')
            """, ("@id", reservation.ToString("N")), ("@run", BudgetPhysicalRun.ToString("N")), ("@at", OldTimestamp));

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.Accounting = new() { Enabled = true, Days = 30 };

        var service = CreateService(settings);

        var predicateMethod = typeof(DataRetentionService).GetMethod("GuildReservationUnlinkedAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        Assert.NotNull(predicateMethod);

        var predicateTask = (Task<string>)predicateMethod.Invoke(service, ["reservation", CancellationToken.None])!;

        string predicate = await predicateTask;

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        await using (SqliteCommand explain = connection.CreateCommand())
        {
            explain.CommandText = "EXPLAIN QUERY PLAN SELECT reservation.Id FROM BudgetReservations reservation WHERE " + predicate;

            List<string> details = [];

            await using (SqliteDataReader reader = await explain.ExecuteReaderAsync())
                while (await reader.ReadAsync()) details.Add(reader.GetString(3));

            Assert.Contains(details, value => value.Contains("SEARCH effect USING", StringComparison.Ordinal)
                && value.Contains("ux_guild_budget_effects_reservation", StringComparison.Ordinal));
        }

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(request);

        Assert.Contains("accounting:" + BudgetPhysicalRun.ToString("D"), plan.CandidateIds);

        Assert.DoesNotContain(plan.Items, item => item.DataClass == RetentionDataClass.BudgetReservations && item.Rows > 0);

        var applied = await service.ApplyAsync(new(request, plan.PlanId));

        Assert.True(applied.IsSuccess, applied.Error.Message);

        Assert.Equal(0, await CampaignScalarCountAsync("SELECT count(*) FROM BillableOperations"));

        Assert.Equal(1, await CampaignScalarCountAsync("SELECT count(*) FROM BudgetReservations"));
    }

    [SkippableFact]
    public async Task Content_retention_pages_past_blocked_prefix_to_later_eligible_content()
    {
        RequireSqlCipher();

        await SeedGuildPhysicalRetentionAsync(2, true, true, authentic: true);

        await ExecuteAsync("""
            INSERT INTO guild_result_receipts(QuestId,ResultId,Digest,OutcomeCode,NoDispatchProven,WorkStoppedProven,MutationId,AcceptedRevision,CreatedAtUtc)
            VALUES('11111111-1111-1111-1111-111111111111','99999999-9999-9999-9999-999999999999',zeroblob(32),4,0,1,
                '88888888-8888-8888-8888-888888888888',1,'2000-01-01T00:00:00.0000000Z');
            UPDATE guild_budget_accounts SET Closed=1,SettlementReceiptId='99999999-9999-9999-9999-999999999999',Revision=Revision+1;
            DELETE FROM guild_budget_evidence;
            INSERT INTO guild_portal_streams(StreamId,QuestId,ProducerKindCode,ProducerKey,LastSequence)
            VALUES('77777777-7777-7777-7777-777777777777','11111111-1111-1111-1111-111111111111',0,'retention',101);
            """);

        for (int index = 1; index <= 101; index++)
        {
            string content = Guid.NewGuid().ToString("D").ToUpperInvariant();

            string envelope = Guid.NewGuid().ToString("D").ToUpperInvariant();

            string inbox = Guid.NewGuid().ToString("D").ToUpperInvariant();

            string receipt = Guid.NewGuid().ToString("D").ToUpperInvariant();

            await ExecuteAsync("""
                INSERT INTO guild_content(ContentId,ContentKindCode,SourceRecordId,Revision,Digest,MimeType,Body,SensitivityCode,ProvenanceModeCode,CreatedAtUtc)
                VALUES(@content,0,@content,1,zeroblob(32),'text/plain',X'',0,0,'2000-01-01T00:00:00.0000000Z');
                INSERT INTO guild_envelopes(Id,StreamId,ProducerRequestId,IntentDigest,QuestId,RootQuestId,KindCode,ContentId,SenderSequence,MetadataJson,CreatedAtUtc)
                VALUES(@envelope,'77777777-7777-7777-7777-777777777777',@request,zeroblob(32),'11111111-1111-1111-1111-111111111111',
                  '11111111-1111-1111-1111-111111111111',0,@content,@sequence,'{}','2000-01-01T00:00:00.0000000Z');
                INSERT INTO guild_inbox(Id,EnvelopeId,RecipientKey,RecipientQuestId,StreamId,SenderSequence,DeliveryStateCode,Revision,CreatedAtUtc,AcknowledgedAtUtc)
                VALUES(@inbox,@envelope,'recipient','11111111-1111-1111-1111-111111111111','77777777-7777-7777-7777-777777777777',
                  @sequence,2,1,'2000-01-01T00:00:00.0000000Z','2000-01-01T00:00:00.0000000Z');
                INSERT INTO guild_portal_receipts(InboxId,ApplicationReceiptId,Digest,EffectKindCode,CreatedAtUtc)
                VALUES(@inbox,@receipt,zeroblob(32),0,'2000-01-01T00:00:00.0000000Z');
                INSERT INTO guild_portal_inputs(InboxId,RecipientQuestId,EnvelopeId,CreatedAtUtc)
                VALUES(@inbox,'11111111-1111-1111-1111-111111111111',@envelope,'2000-01-01T00:00:00.0000000Z');
                """, ("@content", content), ("@envelope", envelope), ("@inbox", inbox), ("@receipt", receipt),
                ("@request", index.ToString()), ("@sequence", index));
        }

        const string eligible = "AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA";

        await ExecuteAsync("""
            INSERT INTO guild_content(ContentId,ContentKindCode,SourceRecordId,Revision,Digest,MimeType,Body,SensitivityCode,ProvenanceModeCode,CreatedAtUtc)
            VALUES('AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA',0,'AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA',1,zeroblob(32),'text/plain',X'',0,0,'2001-01-01T00:00:00.0000000Z')
            """);

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.GuildContent.Enabled = true;

        settings.Retention.GuildContent.Days = 1;

        DataRetentionPlan plan = await CreateService(settings).PlanAsync(new(DataRetentionOperation.Prune));

        Assert.Contains("guild-content:" + eligible, plan.CandidateIds);
    }

    [SkippableFact]
    public async Task Physical_retention_page_accepts_empty_and_rejects_more_than_one_hundred_runs()
    {
        RequireSqlCipher();

        await SeedGuildPhysicalRetentionAsync(2, true, true, authentic: true);

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        Assert.Empty(await GuildPhysicalRetentionProof.ReadPageAsync(connection, null, [], default));

        string[] tooMany = [.. Enumerable.Range(0, 101).Select(_ => Guid.NewGuid().ToString("N"))];

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => GuildPhysicalRetentionProof.ReadPageAsync(connection, null, tooMany, default));
    }

    [SkippableTheory]
    [InlineData("valid", true)]
    [InlineData("missing", false)]
    [InlineData("wrong-run", false)]
    [InlineData("under-reserved", false)]
    [InlineData("negative-reserved", false)]
    [InlineData("amount-mismatch", false)]
    [InlineData("still-reserved", false)]
    [InlineData("cross-run-alias", false)]
    public async Task Physical_retention_page_checks_live_owned_reservation(string mutation, bool expected)
    {
        RequireSqlCipher();

        Guid reservation = Guid.NewGuid();

        await SeedGuildPhysicalRetentionAsync(2, true, false, authentic: true, reservation: reservation);

        await ExecuteAsync("""
            INSERT INTO BudgetReservations(Id,RunId,BudgetPeriod,Status,ExpiresAt,CreatedAt,UpdatedAt,ReservedUsd,ReconciledUsd)
            VALUES(@id,@run,'2000-01-01',1,@at,@at,@at,'2','1.5')
            """, ("@id", reservation.ToString("N")), ("@run", BudgetPhysicalRun.ToString("N")), ("@at", OldTimestamp));

        switch (mutation)
        {
            case "missing":
                await ExecuteAsync("DELETE FROM BudgetReservations");
                break;
            case "wrong-run":
                await ExecuteAsync("UPDATE BudgetReservations SET RunId=@run", ("@run", Guid.NewGuid().ToString("N")));
                break;
            case "under-reserved":
                await ExecuteAsync("UPDATE BudgetReservations SET ReservedUsd='1'");
                break;
            case "negative-reserved":
                await ExecuteAsync("UPDATE BudgetReservations SET ReservedUsd='-1'");
                break;
            case "amount-mismatch":
                await ExecuteAsync("UPDATE BudgetReservations SET ReconciledUsd='1.4'");
                break;
            case "still-reserved":
                await ExecuteAsync("UPDATE BudgetReservations SET Status=0");
                break;
            case "cross-run-alias":
                await ExecuteAsync("""
                    INSERT INTO BudgetReservations(Id,RunId,BudgetPeriod,Status,ExpiresAt,CreatedAt,UpdatedAt,ReservedUsd,ReconciledUsd)
                    SELECT @alias,@other,BudgetPeriod,Status,ExpiresAt,CreatedAt,UpdatedAt,ReservedUsd,ReconciledUsd FROM BudgetReservations
                    """, ("@alias", reservation.ToString("D").ToUpperInvariant()), ("@other", Guid.NewGuid().ToString("N")));
                break;
        }

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        string key = BudgetPhysicalRun.ToString("N");

        var proof = await GuildPhysicalRetentionProof.ReadPageAsync(connection, null, [key], default);

        Assert.Equal(expected, proof[key]);
    }

    [SkippableFact]
    public async Task Physical_retention_page_seeks_linked_reservations_by_identity()
    {
        RequireSqlCipher();

        Guid reservation = Guid.NewGuid();

        await SeedGuildPhysicalRetentionAsync(2, true, false, authentic: true, reservation: reservation);

        await ExecuteAsync("""
            INSERT INTO BudgetReservations(Id,RunId,BudgetPeriod,Status,ExpiresAt,CreatedAt,UpdatedAt,ReservedUsd,ReconciledUsd)
            VALUES(@id,@run,'2000-01-01',1,@at,@at,@at,'2','1.5')
            """, ("@id", reservation.ToString("N")), ("@run", BudgetPhysicalRun.ToString("N")), ("@at", OldTimestamp));

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        List<string> statements = [];

        SQLitePCL.raw.sqlite3_trace(connection.Handle, (object? _, string sql) => statements.Add(sql), null);

        try
        {
            var proof = await GuildPhysicalRetentionProof.ReadPageAsync(connection, null, [BudgetPhysicalRun.ToString("N")], default);

            Assert.True(proof[BudgetPhysicalRun.ToString("N")]);
        }
        finally
        {
            SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.strdelegate_trace)null!, null);
        }

        string sql = Assert.Single(statements, statement => statement.Contains("LEFT JOIN BudgetReservations r", StringComparison.Ordinal));

        await using SqliteCommand explain = connection.CreateCommand();

        explain.CommandText = "EXPLAIN QUERY PLAN " + sql;

        List<string> details = [];

        await using (SqliteDataReader reader = await explain.ExecuteReaderAsync())
            while (await reader.ReadAsync()) details.Add(reader.GetString(3));

        Assert.Contains(details, value => value.Contains("SEARCH e USING INDEX idx_guild_budget_effects_accounting", StringComparison.Ordinal));

        Assert.Contains(details, value => value.Contains("SEARCH r USING INDEX IX_BudgetReservations_Id_Norm", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task Physical_retention_page_seeks_effects_for_each_bounded_run()
    {
        RequireSqlCipher();

        await SeedGuildPhysicalRetentionAsync(2, true, true, authentic: true);

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        List<string> statements = [];

        SQLitePCL.raw.sqlite3_trace(connection.Handle, (object? _, string sql) => statements.Add(sql), null);

        try
        {
            _ = await GuildPhysicalRetentionProof.ReadPageAsync(connection, null, [BudgetPhysicalRun.ToString("N")], default);
        }
        finally
        {
            SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.strdelegate_trace)null!, null);
        }

        string[] proofQueries = [.. statements.Where(sql => sql.Contains("guild_retention_run_page p", StringComparison.Ordinal)
            && sql.Contains("guild_budget_effects e", StringComparison.Ordinal))];

        Assert.Equal(2, proofQueries.Length);

        foreach (string sql in proofQueries)
        {
            await using SqliteCommand explain = connection.CreateCommand();

            explain.CommandText = "EXPLAIN QUERY PLAN " + sql;

            List<string> details = [];

            await using (SqliteDataReader reader = await explain.ExecuteReaderAsync())
                while (await reader.ReadAsync()) details.Add(reader.GetString(3));

            Assert.Contains(details, value => value.Contains("SEARCH e USING INDEX idx_guild_budget_effects_accounting", StringComparison.Ordinal));
        }
    }

    [SkippableFact]
    public async Task Physical_retention_run_frontier_uses_normalized_indexes()
    {
        RequireSqlCipher();

        await SeedGuildPhysicalRetentionAsync(2, true, true, authentic: true);

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "EXPLAIN QUERY PLAN " + DataRetentionService.GuildPhysicalRetentionRunPageSql;

        command.Parameters.AddWithValue("$after", "");

        command.Parameters.AddWithValue("$cutoff", DBNull.Value);

        List<string> details = [];

        await using (SqliteDataReader reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) details.Add(reader.GetString(3));

        Assert.Contains(details, value => value.Contains("SEARCH effect USING INDEX idx_guild_budget_effects_accounting", StringComparison.Ordinal));

        Assert.Contains(details, value => value.Contains("SEARCH run", StringComparison.Ordinal) && value.Contains("IX_InferenceRuns_Id_Norm", StringComparison.Ordinal));
    }

    [SkippableTheory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    public async Task Physical_retention_page_blocks_unknown_only_on_finite_axes(bool finiteUsd, bool finiteTokens, bool expected)
    {
        RequireSqlCipher();

        await SeedGuildPhysicalRetentionAsync(2, true, true, authentic: true, unknownTokens: true,
            finiteUsd: finiteUsd, finiteTokens: finiteTokens);

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        string key = BudgetPhysicalRun.ToString("N");

        var proof = await GuildPhysicalRetentionProof.ReadPageAsync(connection, null, [key], default);

        Assert.Equal(expected, proof[key]);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Malformed_physical_usage_does_not_hide_an_unrelated_run_in_the_same_page(bool blobAccount)
    {
        RequireSqlCipher();

        await SeedGuildPhysicalRetentionAsync(2, true, true, authentic: true);

        Guid goodRun = Guid.NewGuid(), dispatch = Guid.NewGuid(), effect = Guid.NewGuid(), receipt = Guid.NewGuid();

        await ExecuteAsync("""
            INSERT INTO InferenceRuns(Id,RequestId,Surface,Purpose,StartedAt,Status) VALUES(@run,'retention-page','test','test',@at,1);
            INSERT INTO guild_dispatches(DispatchId,QuestId,InvocationDigest,IsContinuation,AccountingAssociationId,EvidenceCode,Revision,CreatedAtUtc,UpdatedAtUtc)
            VALUES(@dispatch,'11111111-1111-1111-1111-111111111111',zeroblob(32),0,@association,1,1,@at,@at);
            INSERT INTO guild_budget_effects(EffectId,AccountId,QuestId,DispatchId,AccountingAssociationId,KindCode,SourceId,KnownUsd,KnownTokens,UnknownUsd,StateCode,Revision)
            VALUES(@effect,'DDDDDDDD-DDDD-DDDD-DDDD-DDDDDDDDDDDD','11111111-1111-1111-1111-111111111111',@dispatch,@association,0,@source,'1.5',2,1,2,1);
            INSERT INTO guild_budget_evidence(ReceiptId,EffectId,Digest,KnownUsd,KnownTokens,UnknownUsd,UnknownTokens,Complete,NoDispatchProven,AppliedRevision)
            VALUES(@receipt,@effect,zeroblob(32),'1.5',2,1,0,1,0,1);
            """, ("@run", goodRun.ToString("N")), ("@at", OldTimestamp),
            ("@dispatch", dispatch.ToString("D").ToUpperInvariant()), ("@association", goodRun.ToString("D").ToUpperInvariant()),
            ("@effect", effect.ToString("D").ToUpperInvariant()), ("@source", Guid.NewGuid().ToString("D").ToUpperInvariant()),
            ("@receipt", receipt.ToString("D").ToUpperInvariant()));

        // The blob case represents an already-corrupt store that bypassed schema guards.
        await ExecuteAsync(blobAccount
            ? "PRAGMA foreign_keys=OFF; PRAGMA ignore_check_constraints=ON; DROP TRIGGER guild_budget_effects_guard_update; UPDATE guild_budget_effects SET AccountId=X'FF' WHERE EffectId='CCCCCCCC-CCCC-CCCC-CCCC-CCCCCCCCCCCC'"
            : "DROP TRIGGER guild_budget_effects_guard_update; UPDATE guild_budget_effects SET KnownUsd='not-a-decimal' WHERE EffectId='CCCCCCCC-CCCC-CCCC-CCCC-CCCCCCCCCCCC'");

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        string badKey = BudgetPhysicalRun.ToString("N"), goodKey = goodRun.ToString("N");

        var proof = await GuildPhysicalRetentionProof.ReadPageAsync(connection, null, [badKey, goodKey], default);

        Assert.False(proof[badKey]);

        Assert.True(proof[goodKey]);
    }

    [SkippableTheory]
    [InlineData("missing-binding")]
    [InlineData("bad-limit")]
    [InlineData("dangling-parent")]
    [InlineData("parent-cycle")]
    [InlineData("wrong-root")]
    [InlineData("receipt-mismatch")]
    [InlineData("receipt-unknown-mismatch")]
    [InlineData("receipt-incomplete")]
    [InlineData("receipt-false-unsent")]
    [InlineData("zero-receipt")]
    [InlineData("missing-account")]
    [InlineData("wrong-quest")]
    [InlineData("missing-dispatch")]
    [InlineData("malformed-effect")]
    [InlineData("negative-limit")]
    [InlineData("invalid-token-type")]
    public async Task Physical_retention_page_fails_closed_for_corrupt_funding_or_current_evidence(string corruption)
    {
        RequireSqlCipher();

        await SeedGuildPhysicalRetentionAsync(2, true, true, authentic: true);

        // This fixture deliberately simulates an already-corrupt store, including broken
        // parent edges that a healthy schema would refuse to create.
        await ExecuteAsync("PRAGMA foreign_keys=OFF; PRAGMA ignore_check_constraints=ON");

        switch (corruption)
        {
            case "missing-binding":
                await ExecuteAsync("DELETE FROM guild_quest_budget");
                break;
            case "bad-limit":
                await ExecuteAsync("DROP TRIGGER guild_budget_accounts_guard_update; UPDATE guild_budget_accounts SET UsdLimit='not-a-decimal',Revision=Revision+1");
                break;
            case "dangling-parent":
                await ExecuteAsync("DROP TRIGGER guild_budget_accounts_guard_update; UPDATE guild_budget_accounts SET ParentAccountId='AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA',Revision=Revision+1");
                break;
            case "parent-cycle":
                await ExecuteAsync("DROP TRIGGER guild_budget_accounts_guard_update; UPDATE guild_budget_accounts SET ParentAccountId=AccountId,Revision=Revision+1");
                break;
            case "wrong-root":
                await ExecuteAsync("DROP TRIGGER guild_budget_accounts_guard_update; UPDATE guild_budget_accounts SET RootAccountId='AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA',Revision=Revision+1");
                break;
            case "receipt-unknown-mismatch":
                await ExecuteAsync("DROP TRIGGER guild_budget_evidence_guard_update; UPDATE guild_budget_evidence SET UnknownUsd=0");
                break;
            case "receipt-incomplete":
                await ExecuteAsync("DROP TRIGGER guild_budget_evidence_guard_update; UPDATE guild_budget_evidence SET Complete=0");
                break;
            case "receipt-false-unsent":
                await ExecuteAsync("DROP TRIGGER guild_budget_evidence_guard_update; UPDATE guild_budget_evidence SET NoDispatchProven=1");
                break;
            case "zero-receipt":
                await ExecuteAsync("DROP TRIGGER guild_budget_evidence_guard_update; UPDATE guild_budget_evidence SET ReceiptId='00000000-0000-0000-0000-000000000000'");
                break;
            case "missing-account":
                await ExecuteAsync("DROP TRIGGER guild_budget_effects_guard_update; UPDATE guild_budget_effects SET AccountId='AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA'");
                break;
            case "wrong-quest":
                await ExecuteAsync("DROP TRIGGER guild_budget_effects_guard_update; UPDATE guild_budget_effects SET QuestId='AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA'");
                break;
            case "missing-dispatch":
                await ExecuteAsync("DROP TRIGGER guild_budget_effects_guard_update; UPDATE guild_budget_effects SET DispatchId='AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA'");
                break;
            case "malformed-effect":
                await ExecuteAsync("DROP TRIGGER guild_budget_effects_guard_update; UPDATE guild_budget_effects SET EffectId='invalid'");
                break;
            case "negative-limit":
                await ExecuteAsync("DROP TRIGGER guild_budget_accounts_guard_update; UPDATE guild_budget_accounts SET UsdLimit='-1',Revision=Revision+1");
                break;
            case "invalid-token-type":
                await ExecuteAsync("DROP TRIGGER guild_budget_effects_guard_update; UPDATE guild_budget_effects SET KnownTokens='invalid'");
                break;
            case "receipt-mismatch":
                await ExecuteAsync("""
                    DELETE FROM guild_budget_evidence;
                    INSERT INTO guild_budget_evidence(ReceiptId,EffectId,Digest,KnownUsd,KnownTokens,UnknownUsd,UnknownTokens,Complete,NoDispatchProven,AppliedRevision)
                    VALUES('DDDDDDDD-DDDD-DDDD-DDDD-DDDDDDDDDDDD','CCCCCCCC-CCCC-CCCC-CCCC-CCCCCCCCCCCC',zeroblob(32),'1.4',2,1,0,1,0,1)
                    """);
                break;
        }

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        string key = BudgetPhysicalRun.ToString("N");

        var proof = await GuildPhysicalRetentionProof.ReadPageAsync(connection, null, [key], default);

        Assert.False(proof[key]);
    }

    [SkippableFact]
    public async Task Physical_retention_page_refuses_malformed_effect_kind()
    {
        RequireSqlCipher();

        await SeedGuildPhysicalRetentionAsync(2, true, false, authentic: true);

        await ExecuteAsync("PRAGMA ignore_check_constraints=ON; DROP TRIGGER guild_budget_effects_guard_update; UPDATE guild_budget_effects SET KindCode=2");

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        string key = BudgetPhysicalRun.ToString("N");

        var proof = await GuildPhysicalRetentionProof.ReadPageAsync(connection, null, [key], default);

        Assert.False(proof[key]);
    }

    [SkippableFact]
    public async Task Physical_retention_page_requires_current_evidence_for_unrestricted_effect()
    {
        RequireSqlCipher();

        await SeedGuildPhysicalRetentionAsync(2, true, true, authentic: true);

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        string key = BudgetPhysicalRun.ToString("N");

        List<string> statements = [];

        SQLitePCL.raw.sqlite3_trace(connection.Handle, (object? _, string sql) => statements.Add(sql), null);

        Dictionary<string, bool> clear;

        try
        {
            clear = await GuildPhysicalRetentionProof.ReadPageAsync(connection, null, [key], default);
        }
        finally
        {
            SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.strdelegate_trace)null!, null);
        }

        Assert.True(clear[key]);

        Assert.DoesNotContain(statements, sql => sql.Contains("BudgetReservations", StringComparison.Ordinal));

        await ExecuteAsync("DELETE FROM guild_budget_evidence");

        var missing = await GuildPhysicalRetentionProof.ReadPageAsync(connection, null, [key], default);

        Assert.False(missing[key]);
    }

    [SkippableFact]
    public async Task Completed_unrestricted_physical_effect_with_unknown_usage_allows_bill_pruning()
    {
        RequireSqlCipher();

        await SeedGuildPhysicalRetentionAsync(2, true, true, authentic: true);

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.Accounting = new() { Enabled = true, Days = 30 };

        var service = CreateService(settings);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(request);

        Assert.Contains("accounting:" + BudgetPhysicalRun.ToString("D"), plan.CandidateIds);

        var applied = await service.ApplyAsync(new(request, plan.PlanId));

        Assert.True(applied.IsSuccess, applied.Error.Message);

        Assert.Equal(0, await CampaignScalarCountAsync("SELECT count(*) FROM BillableOperations"));

        Assert.Equal(1, await CampaignScalarCountAsync("SELECT count(*) FROM guild_budget_evidence"));
    }

    [SkippableFact]
    public async Task Guild_physical_obligation_appearing_after_preview_preserves_terminal_bill_on_apply()
    {
        RequireSqlCipher();

        await SeedCompletedInferenceRunAsync(BudgetPhysicalRun, null);

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.Accounting = new() { Enabled = true, Days = 30 };

        var service = CreateService(settings);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(request);

        Assert.Contains("accounting:" + BudgetPhysicalRun.ToString("D"), plan.CandidateIds);

        await SeedGuildPhysicalRetentionAsync(1, false, false, seedRun: false);

        _ = await service.ApplyAsync(new(request, plan.PlanId));

        Assert.Equal(1, await CampaignScalarCountAsync("SELECT count(*) FROM InferenceRuns"));

        Assert.Equal(1, await CampaignScalarCountAsync("SELECT count(*) FROM BillableOperations"));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Guild_reset_retains_terminal_Quest_until_current_physical_receipt_is_copied(bool staleSnapshot)
    {
        RequireSqlCipher();

        await SeedGuildPhysicalRetentionAsync(2, staleSnapshot, false, authentic: true);

        if (staleSnapshot)
        {
            await ExecuteAsync("UPDATE guild_budget_effects SET Revision=Revision+1");
        }

        await ExecuteAsync("""
            INSERT INTO guild_result_receipts(QuestId,ResultId,Digest,OutcomeCode,NoDispatchProven,WorkStoppedProven,MutationId,AcceptedRevision,CreatedAtUtc)
            VALUES('11111111-1111-1111-1111-111111111111','99999999-9999-9999-9999-999999999999',zeroblob(32),5,0,1,
                '88888888-8888-8888-8888-888888888888',1,'2000-01-01T00:00:00.0000000Z');
            UPDATE guild_budget_accounts SET Closed=1,SettlementReceiptId='99999999-9999-9999-9999-999999999999',Revision=2;
            """);

        var service = CreateService();

        DataRetentionPlan pinned = await service.PlanAsync(new(DataRetentionOperation.FactoryReset));

        Assert.Contains(pinned.Conflicts, conflict => conflict.Code == ErrorCodes.Guild.ReconciliationRequired
            && conflict.ResourceId == "11111111-1111-1111-1111-111111111111");

        await ExecuteAsync("""
            INSERT INTO guild_budget_evidence(ReceiptId,EffectId,Digest,KnownUsd,KnownTokens,UnknownUsd,UnknownTokens,Complete,NoDispatchProven,AppliedRevision)
            SELECT '77777777-7777-7777-7777-777777777777',EffectId,zeroblob(32),KnownUsd,KnownTokens,0,0,1,0,Revision
            FROM guild_budget_effects;
            """);

        DataRetentionPlan released = await service.PlanAsync(new(DataRetentionOperation.FactoryReset));

        Assert.DoesNotContain(released.Conflicts, conflict => conflict.Code == ErrorCodes.Guild.ReconciliationRequired
            && conflict.ResourceId == "11111111-1111-1111-1111-111111111111");
    }

    private static readonly Guid BudgetPhysicalRun = Guid.Parse("EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE");

    private async Task SeedGuildPhysicalRetentionAsync(int state, bool copied, bool unknown, bool seedRun = true, bool authentic = false, Guid? reservation = null, bool unknownTokens = false,
        bool finiteUsd = false, bool finiteTokens = false)
    {
        if (seedRun)
        {
            await SeedCompletedInferenceRunAsync(BudgetPhysicalRun, null);
        }

        await SeedRetentionQuestAsync();

        await ExecuteAsync("""
            INSERT INTO BillableOperations(Id,RunId,OperationType,Provider,Model,Purpose,StartedAt,CompletedAt,
                InputTokens,OutputTokens,CachedTokens,PricingSnapshotJson,Status,ActualCostUsd)
            VALUES('FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF',@run,0,'test','test','test',@at,@at,1,1,0,'{}',0,'1.5');
            INSERT INTO guild_budget_accounts(AccountId,OwnerKindCode,OwnerId,RootAccountId,Revision,UsdLimit,TokenLimit)
            VALUES(@account,@ownerKind,@owner,@account,1,@usdLimit,@tokenLimit);
            INSERT INTO guild_dispatches(DispatchId,QuestId,InvocationDigest,IsContinuation,AccountingAssociationId,EvidenceCode,Revision,CreatedAtUtc,UpdatedAtUtc)
            VALUES('BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB','11111111-1111-1111-1111-111111111111',zeroblob(32),0,@association,1,1,@at,@at);
            INSERT INTO guild_budget_effects(EffectId,AccountId,QuestId,DispatchId,AccountingAssociationId,KindCode,SourceId,KnownUsd,KnownTokens,UnknownUsd,UnknownTokens,StateCode,Revision,ReservationId,UsdBound)
            VALUES('CCCCCCCC-CCCC-CCCC-CCCC-CCCCCCCCCCCC',@account,'11111111-1111-1111-1111-111111111111',
                'BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB',@association,0,'FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF','1.5',2,@unknown,@unknownTokens,@state,1,@reservation,@bound);
            """, ("@run", BudgetPhysicalRun.ToString("D")), ("@association", BudgetPhysicalRun.ToString("D").ToUpperInvariant()),
            ("@at", OldTimestamp), ("@unknown", unknown ? 1 : 0), ("@state", state),
            ("@account", authentic ? "DDDDDDDD-DDDD-DDDD-DDDD-DDDDDDDDDDDD" : "AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA"),
            ("@ownerKind", authentic ? 0 : 1),
            ("@owner", authentic ? "11111111-1111-1111-1111-111111111111" : "AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA"),
            ("@reservation", (object?)reservation?.ToString("D").ToUpperInvariant() ?? DBNull.Value), ("@bound", reservation is null ? DBNull.Value : "2"),
            ("@unknownTokens", unknownTokens ? 1 : 0), ("@usdLimit", finiteUsd ? "10" : DBNull.Value),
            ("@tokenLimit", finiteTokens ? 100L : DBNull.Value));

        if (authentic)
        {
            await ExecuteAsync("INSERT INTO guild_quest_budget(QuestId,AccountId) VALUES('11111111-1111-1111-1111-111111111111','DDDDDDDD-DDDD-DDDD-DDDD-DDDDDDDDDDDD')");
        }

        if (copied)
        {
            await ExecuteAsync("""
                INSERT INTO guild_budget_evidence(ReceiptId,EffectId,Digest,KnownUsd,KnownTokens,UnknownUsd,UnknownTokens,Complete,NoDispatchProven,AppliedRevision)
                VALUES('DDDDDDDD-DDDD-DDDD-DDDD-DDDDDDDDDDDD','CCCCCCCC-CCCC-CCCC-CCCC-CCCCCCCCCCCC',zeroblob(32),'1.5',2,@unknown,@unknownTokens,1,@unsent,1);
                """, ("@unknown", unknown ? 1 : 0), ("@unknownTokens", unknownTokens ? 1 : 0), ("@unsent", state == 3 ? 1 : 0));
        }
    }

    private Task SeedBudgetResetAccountsAsync() => ExecuteAsync("""
        INSERT INTO guild_budget_accounts(AccountId,OwnerKindCode,OwnerId,RootAccountId,Revision)
        VALUES('AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA',1,'AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA','AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA',1);
        INSERT INTO guild_budget_accounts(AccountId,OwnerKindCode,OwnerId,RootAccountId,ParentAccountId,Revision)
        VALUES('BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB',1,'BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB','AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA','AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA',1);
        """);
}
