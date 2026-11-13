using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Operations;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Tests.Guild;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class DataRetentionServiceTests
{
    [SkippableTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    public async Task Durable_refresh_pins_the_actual_accounting_candidate_and_writer(int state)
    {
        RequireSqlCipher();

        await SeedGuildPhysicalRetentionAsync(2, true, false, authentic: true);

        var settings = CreatePruneSettings();

        settings.Retention.Accounting = new() { Enabled = true, Days = 30 };

        var service = CreateService(settings);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan before = await service.PlanAsync(request);

        string candidate = "accounting:" + BudgetPhysicalRun.ToString("D");

        Assert.Contains(candidate, before.CandidateIds);

        await SeedRefreshPinAsync(state);

        DataRetentionPlan after = await service.PlanAsync(request);

        Assert.DoesNotContain(candidate, after.CandidateIds);

        var refused = await service.DeleteAccountingCandidateAsync(BudgetPhysicalRun, DateTimeOffset.UtcNow.AddDays(-30), default);

        Assert.Equal(0, refused.Rows);

        Assert.Equal(1, await CampaignScalarCountAsync("SELECT count(*) FROM BillableOperations"));
    }

    [SkippableFact]
    public async Task Terminal_refresh_releases_accounting_but_preserves_linked_operation_history()
    {
        RequireSqlCipher();

        await SeedGuildPhysicalRetentionAsync(2, true, false, authentic: true);

        await SeedRefreshPinAsync(4);

        var settings = CreatePruneSettings();

        settings.Retention.Accounting = new() { Enabled = true, Days = 30 };

        settings.Retention.LongRunningOperations = new() { Enabled = true, Days = 30 };

        DataRetentionPlan plan = await CreateService(settings).PlanAsync(new(DataRetentionOperation.Prune));

        Assert.Contains("accounting:" + BudgetPhysicalRun.ToString("D"), plan.CandidateIds);

        Assert.DoesNotContain("operation:44444444-4444-4444-4444-444444444444", plan.CandidateIds);
    }

    [SkippableFact]
    public async Task Physical_refresh_claim_keeps_terminalized_operation_bill_pinned_until_released()
    {
        RequireSqlCipher();

        await SeedGuildPhysicalRetentionAsync(2, true, false, authentic: true);

        await SeedRefreshPinAsync(4);

        LongRunningOperationOwnership ownership = new();

        Guid operation = Guid.Parse("44444444-4444-4444-4444-444444444444");

        Assert.True(ownership.TryClaim(operation, out Guid token));

        var settings = CreatePruneSettings();

        settings.Retention.Accounting = new() { Enabled = true, Days = 30 };

        var service = CreateService(settings, operationOwnership: ownership);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        string candidate = "accounting:" + BudgetPhysicalRun.ToString("D");

        Assert.DoesNotContain(candidate, (await service.PlanAsync(request)).CandidateIds);

        var refused = await service.DeleteAccountingCandidateAsync(BudgetPhysicalRun, DateTimeOffset.UtcNow.AddDays(-30), default);

        Assert.Equal(0, refused.Rows);

        Assert.True(ownership.Release(operation, token));

        Assert.Contains(candidate, (await service.PlanAsync(request)).CandidateIds);
    }

    [SkippableFact]
    public async Task Accepted_refresh_receipt_keeps_its_terminal_operation_history()
    {
        RequireSqlCipher();

        await _db!.Database.OpenConnectionAsync();

        (Guid sessionId, _) = await SeedSessionAsync(pinned: false);

        var captured = await GuildQuestStoreTests.CaptureAndCompleteForResetAsync(_db.Database.GetDbConnection().ConnectionString, sessionId);

        await SeedTerminalSpendRefreshAsync(captured.EffectId);

        await using var query = _db.Database.GetDbConnection().CreateCommand();

        query.CommandText = "SELECT OperationId FROM guild_spend_refreshes WHERE AcceptedReceiptId IS NOT NULL";

        Guid operation = Guid.Parse(Assert.IsType<string>(await query.ExecuteScalarAsync()));

        var settings = CreatePruneSettings();

        settings.Retention.LongRunningOperations = new() { Enabled = true, Days = 30 };

        DataRetentionPlan plan = await CreateService(settings).PlanAsync(new(DataRetentionOperation.Prune));

        Assert.DoesNotContain("operation:" + operation.ToString("D"), plan.CandidateIds);
    }

    [SkippableFact]
    public async Task Corrupt_noncanonical_refresh_operation_identity_retains_its_accounting()
    {
        RequireSqlCipher();

        await SeedGuildPhysicalRetentionAsync(2, true, false, authentic: true);

        await SeedRefreshPinAsync(4);

        await ExecuteAsync("""
            PRAGMA foreign_keys=OFF;
            PRAGMA ignore_check_constraints=ON;
            DROP TRIGGER guild_spend_refreshes_guard_update;
            UPDATE LongRunningOperations SET Id='AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA' WHERE Id='44444444444444444444444444444444';
            UPDATE guild_spend_refreshes SET OperationId='AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA';
            """);

        var settings = CreatePruneSettings();

        settings.Retention.Accounting = new() { Enabled = true, Days = 30 };

        var service = CreateService(settings);

        string candidate = "accounting:" + BudgetPhysicalRun.ToString("D");

        Assert.DoesNotContain(candidate, (await service.PlanAsync(new(DataRetentionOperation.Prune))).CandidateIds);

        Assert.Equal(0, (await service.DeleteAccountingCandidateAsync(BudgetPhysicalRun, DateTimeOffset.UtcNow.AddDays(-30), default)).Rows);
    }

    [SkippableTheory]
    [InlineData("cccccccc-cccc-cccc-cccc-cccccccccccc", false)]
    [InlineData("cccccccccccccccccccccccccccccccc", false)]
    [InlineData("cccccccc-cccc-cccc-cccc-cccccccccccc", true)]
    public async Task Corrupt_refresh_effect_alias_retains_its_accounting(string alias, bool collision)
    {
        RequireSqlCipher();

        await SeedGuildPhysicalRetentionAsync(2, true, false, authentic: true);

        await SeedRefreshPinAsync(collision ? 4 : 0);

        await ExecuteAsync("""
            PRAGMA foreign_keys=OFF;
            PRAGMA ignore_check_constraints=ON;
            DROP TRIGGER guild_spend_refreshes_guard_update;
            """);

        if (collision)
            await ExecuteAsync("""
                DROP TRIGGER guild_spend_refreshes_guard_insert;
                INSERT INTO LongRunningOperations(Id,Kind,State,RecoveryPolicy,CreatedAt,PublicSummary,CheckpointVersion)
                VALUES('66666666666666666666666666666666','guild-spend-refresh',4,2,@at,'refresh',1);
                INSERT INTO guild_spend_refreshes(RefreshId,EffectId,OperationId,RequestDigest)
                VALUES('77777777-7777-7777-7777-777777777777',@alias,'66666666666666666666666666666666',zeroblob(32));
                """, ("@alias", alias), ("@at", OldTimestamp));
        else
            await ExecuteAsync("UPDATE guild_spend_refreshes SET EffectId=@alias", ("@alias", alias));

        var settings = CreatePruneSettings();

        settings.Retention.Accounting = new() { Enabled = true, Days = 30 };

        var service = CreateService(settings);

        Assert.DoesNotContain("accounting:" + BudgetPhysicalRun.ToString("D"),
            (await service.PlanAsync(new(DataRetentionOperation.Prune))).CandidateIds);

        Assert.Equal(0, (await service.DeleteAccountingCandidateAsync(BudgetPhysicalRun, DateTimeOffset.UtcNow.AddDays(-30), default)).Rows);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refresh_retention_lookup_seeks_both_effect_indexes(bool claimPath)
    {
        RequireSqlCipher();

        await SeedGuildPhysicalRetentionAsync(2, true, false, authentic: true);

        await SeedRefreshPinAsync(0);

        await _db!.Database.OpenConnectionAsync();

        await using var command = _db.Database.GetDbConnection().CreateCommand();

        command.CommandText = "EXPLAIN QUERY PLAN " + (claimPath ? DataRetentionService.GuildRefreshClaimPinSql : DataRetentionService.GuildRefreshDurablePinSql);

        var parameter = command.CreateParameter();

        parameter.ParameterName = "$key";

        parameter.Value = BudgetPhysicalRun.ToString("N");

        command.Parameters.Add(parameter);

        List<string> plan = [];

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync()) plan.Add(reader.GetString(3));

        Assert.Contains(plan, line => line.Contains("SEARCH effect", StringComparison.Ordinal) && line.Contains("idx_guild_budget_effects_accounting", StringComparison.Ordinal));

        Assert.Contains(plan, line => line.Contains("SEARCH refresh", StringComparison.Ordinal) && line.Contains("idx_guild_spend_refreshes_effect_norm", StringComparison.Ordinal));

        Assert.DoesNotContain(plan, line => line.Contains("SCAN refresh", StringComparison.Ordinal));
    }

    [SkippableTheory]
    [InlineData("42")]
    [InlineData("bad-state")]
    public async Task Invalid_refresh_operation_state_retains_its_accounting(string state)
    {
        RequireSqlCipher();

        await SeedGuildPhysicalRetentionAsync(2, true, false, authentic: true);

        await SeedRefreshPinAsync(4);

        await ExecuteAsync("UPDATE LongRunningOperations SET State=@state WHERE Id='44444444444444444444444444444444'", ("@state", state));

        var settings = CreatePruneSettings();

        settings.Retention.Accounting = new() { Enabled = true, Days = 30 };

        var service = CreateService(settings);

        Assert.DoesNotContain("accounting:" + BudgetPhysicalRun.ToString("D"),
            (await service.PlanAsync(new(DataRetentionOperation.Prune))).CandidateIds);

        Assert.Equal(0, (await service.DeleteAccountingCandidateAsync(BudgetPhysicalRun, DateTimeOffset.UtcNow.AddDays(-30), default)).Rows);
    }

    private async Task SeedRefreshPinAsync(int state)
    {
        await ExecuteAsync("""
            INSERT INTO guild_spend_sources(EffectId,SourceId,SourceKindCode,SourceVersion,InstallationGeneration,AuthorityEpoch,
                AuthorityKeyId,Endpoint,Provider,Model,PricingSnapshotJson,CredentialFingerprint)
            SELECT e.EffectId,e.SourceId,1,1,q.InstallationGeneration,'22222222-2222-2222-2222-222222222222',
                '33333333-3333-3333-3333-333333333333','https://openrouter.ai/api/v1','test','test','{}',zeroblob(32)
            FROM guild_budget_effects e JOIN guild_quests q ON q.Id=e.QuestId
            WHERE e.EffectId='CCCCCCCC-CCCC-CCCC-CCCC-CCCCCCCCCCCC';
            INSERT INTO LongRunningOperations(Id,Kind,State,RecoveryPolicy,CreatedAt,PublicSummary,CheckpointVersion)
            VALUES('44444444444444444444444444444444','guild-spend-refresh',@state,2,@at,'refresh',1);
            INSERT INTO guild_spend_refreshes(RefreshId,EffectId,OperationId,RequestDigest)
            VALUES('55555555-5555-5555-5555-555555555555','CCCCCCCC-CCCC-CCCC-CCCC-CCCCCCCCCCCC',
                '44444444444444444444444444444444',zeroblob(32));
            """, ("@state", state), ("@at", OldTimestamp));
    }
}
