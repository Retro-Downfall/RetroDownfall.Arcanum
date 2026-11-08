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

        Assert.Contains(plan.Items, item => item.DataClass == RetentionDataClass.GuildEvidence && item.DerivedRecords == 3);

        var recovered = await ReconcileFactoryResetV0Async(CreateService(), "guild-budget-reset");

        Assert.Equal(1, recovered.Summary.Completed);

        Assert.Equal(0, await CampaignScalarCountAsync("SELECT count(*) FROM guild_budget_accounts"));
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
    [InlineData(2, true, true, true)]
    [InlineData(2, true, false, false)]
    public async Task Guild_physical_evidence_protects_terminal_inference_accounting_until_current_snapshot_is_complete(
        int state, bool copied, bool unknown, bool pinned)
    {
        RequireSqlCipher();

        await SeedGuildPhysicalRetentionAsync(state, copied, unknown);

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

        await SeedGuildPhysicalRetentionAsync(2, staleSnapshot, false);

        if (staleSnapshot)
        {
            await ExecuteAsync("UPDATE guild_budget_effects SET Revision=Revision+1");
        }

        await ExecuteAsync("""
            INSERT INTO guild_result_receipts(QuestId,ResultId,Digest,OutcomeCode,NoDispatchProven,WorkStoppedProven,MutationId,AcceptedRevision,CreatedAtUtc)
            VALUES('11111111-1111-1111-1111-111111111111','99999999-9999-9999-9999-999999999999',zeroblob(32),5,0,1,
                '88888888-8888-8888-8888-888888888888',1,'2000-01-01T00:00:00.0000000Z');
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

    private async Task SeedGuildPhysicalRetentionAsync(int state, bool copied, bool unknown, bool seedRun = true)
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
            INSERT INTO guild_budget_accounts(AccountId,OwnerKindCode,OwnerId,RootAccountId,Revision)
            VALUES('AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA',1,'AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA','AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA',1);
            INSERT INTO guild_dispatches(DispatchId,QuestId,InvocationDigest,IsContinuation,AccountingAssociationId,EvidenceCode,Revision,CreatedAtUtc,UpdatedAtUtc)
            VALUES('BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB','11111111-1111-1111-1111-111111111111',zeroblob(32),0,@association,1,1,@at,@at);
            INSERT INTO guild_budget_effects(EffectId,AccountId,QuestId,DispatchId,AccountingAssociationId,KindCode,SourceId,KnownUsd,KnownTokens,UnknownUsd,StateCode,Revision)
            VALUES('CCCCCCCC-CCCC-CCCC-CCCC-CCCCCCCCCCCC','AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA','11111111-1111-1111-1111-111111111111',
                'BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB',@association,0,'FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF','1.5',2,@unknown,@state,1);
            """, ("@run", BudgetPhysicalRun.ToString("D")), ("@association", BudgetPhysicalRun.ToString("D").ToUpperInvariant()),
            ("@at", OldTimestamp), ("@unknown", unknown ? 1 : 0), ("@state", state));

        if (copied)
        {
            await ExecuteAsync("""
                INSERT INTO guild_budget_evidence(ReceiptId,EffectId,Digest,KnownUsd,KnownTokens,UnknownUsd,UnknownTokens,Complete,NoDispatchProven,AppliedRevision)
                VALUES('DDDDDDDD-DDDD-DDDD-DDDD-DDDDDDDDDDDD','CCCCCCCC-CCCC-CCCC-CCCC-CCCCCCCCCCCC',zeroblob(32),'1.5',2,@unknown,0,1,@unsent,1);
                """, ("@unknown", unknown ? 1 : 0), ("@unsent", state == 3 ? 1 : 0));
        }
    }

    private Task SeedBudgetResetAccountsAsync() => ExecuteAsync("""
        INSERT INTO guild_budget_accounts(AccountId,OwnerKindCode,OwnerId,RootAccountId,Revision)
        VALUES('AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA',1,'AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA','AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA',1);
        INSERT INTO guild_budget_accounts(AccountId,OwnerKindCode,OwnerId,RootAccountId,ParentAccountId,Revision)
        VALUES('BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB',1,'BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB','AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA','AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA',1);
        """);
}
