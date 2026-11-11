using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Cryptography;
using System.Text;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Local_cleanup_receipt_cannot_author_completed_or_contentful_result(bool completed)
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var owner = await f.OperatorAsync();

        var issued = await f.IssueAsync(owner, f.Request);

        Assert.True(issued.IsSuccess, issued.Error.Message);

        GuildCancelQuestRequest stop = new(Guid.NewGuid(), issued.Value.Quest.Revision, GuildCancellationReason.OperatorRequested);

        var canceled = await f.Store.RequestCancellationAsync(owner,
            new(stop.IdempotencyKey, GuildQuestIntent.Compute(issued.Value.Quest.Id, stop).Value), issued.Value.Quest.Id, stop, default);

        Assert.True(canceled.IsSuccess, canceled.Error.Message);

        var cleanup = await f.Store.CreateLocalCleanupDriverAsync(owner, issued.Value.Quest.Id, default);

        Assert.True(cleanup.IsSuccess, cleanup.Error.Message);

        var opened = await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

        Assert.True(opened.IsSuccess, opened.Error.Message);

        await using var connection = opened.Value;

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection.Connection);

        LongRunningOperationStore operations = new(db, f.Connections);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        var lease = await operations.TryAcquireLeaseAsync(issued.Value.Quest.OperationId, "cleanup-proof", now, now.AddMinutes(2));

        Assert.True(lease.Acquired);

        GuildQuestDriver driver = cleanup.Value with { LeaseOwner = "cleanup-proof", OperationRevision = lease.Operation.Revision };

        byte[] body = Encoding.UTF8.GetBytes("This cleanup credential cannot cast a successful vote.");

        string digest = Convert.ToHexString(SHA256.HashData(body));

        Guid resultId = Guid.NewGuid();

        GuildDataRef content = new(Guid.NewGuid(), GuildContentKind.QuestResult, 1, digest, "text/plain", body.Length, false);

        GuildQuestResult result = new(new(issued.Value.Quest.Id, resultId, digest, content,
            completed ? QuestState.Completed : QuestState.Canceled, completed ? null : LongRunningOperationRecoveryOutcomes.Cancelled),
            body, NoDispatchProven: true, WorkStoppedProven: true);

        var current = await f.Store.GetAsync(owner, issued.Value.Quest.Id, default);

        var refused = await f.Store.CommitResultAsync(driver, new(resultId, digest), current.Value.Quest.Revision, result, default);

        Assert.True(refused.IsFailure);

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_result_receipts"));

        Assert.Equal(0L, await f.ScalarAsync($"SELECT count(*) FROM guild_content WHERE ContentId='{content.ContentId:D}' COLLATE NOCASE"));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    public async Task Cohort_withdrawn_local_subtree_drains_after_restart_without_rebinding_work_authority(bool disabled, bool revoke, bool erased)
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        f.Settings.CurrentValue.Features.Apprentices = true;

        f.Settings.CurrentValue.Providers = [new() { Name = "provider", Type = AiProviderKind.OpenAICompatible,
            Endpoint = "https://provider.example.test/v1", Models = [new() { Name = "model" }] }];

        var setup = await CreateCohortAuthorityAsync(f, true);

        var owner = await f.OperatorAsync();

        if (revoke)
        {
            var target = await f.Store.GetAsync(owner, setup.Execution.QuestId, default);

            Guid key = Guid.NewGuid();

            var revoked = await f.Authority.RevokeAsync(owner, new(key, new string('A', 64)), target.Value.Quest.SignetId,
                new(key, target.Value.Quest.SignetRevision, GuildCancellationReason.OperatorRequested), default);

            Assert.True(revoked.IsSuccess, revoked.Error.Message);
        }
        else
        {
            await f.ExecuteAsync($"""
                UPDATE guild_cohorts SET MembershipVersion=MembershipVersion+1,Revision=Revision+1 WHERE Id='{setup.Cohort.Id:D}' COLLATE NOCASE;
                UPDATE guild_cohort_members SET StateCode=2,RemovalSequence=2 WHERE CohortId='{setup.Cohort.Id:D}' COLLATE NOCASE;
                UPDATE guild_cohort_member_quests SET Active=0;
                """);
        }

        var root = await f.Store.GetAsync(owner, setup.Execution.RootQuestId, default);

        Assert.True(root.IsSuccess, root.Error.Message);

        GuildCancelQuestRequest cancel = new(Guid.NewGuid(), root.Value.Quest.Revision, GuildCancellationReason.OperatorRequested);

        var canceled = await f.Store.RequestCancellationAsync(owner,
            new(cancel.IdempotencyKey, GuildQuestIntent.Compute(root.Value.Quest.Id, cancel).Value), root.Value.Quest.Id, cancel, default);

        Assert.True(canceled.IsSuccess, canceled.Error.Message);

        if (erased)
        {
            await SealLocalOwnerAsync(f, root.Value.Quest.SessionId!.Value);

            Assert.True((await f.Store.GetAsync(owner, root.Value.Quest.Id, default)).IsFailure);
        }

        var beforeRestart = await f.Store.GetAsync(owner, setup.Execution.QuestId, default);

        var recovery = await f.Policy.CreateRecoveryAsync(owner, setup.Execution.QuestId,
            beforeRestart.IsSuccess ? beforeRestart.Value.Quest.OperationId : root.Value.Quest.OperationId,
            beforeRestart.IsSuccess ? beforeRestart.Value.Quest.SignetRevision : root.Value.Quest.SignetRevision, default);

        Assert.True(recovery.IsFailure || (await f.Authority.RebindAsync(recovery.Value, default)).IsFailure);

        if (disabled) f.Settings.CurrentValue.Features.Guild = false;

        var opened = await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

        Assert.True(opened.IsSuccess, opened.Error.Message);

        await using var connection = opened.Value;

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection.Connection);

        LongRunningOperationOwnership ownership = new();

        SchedulerEngine engine = new();

        GuildLocalQuestScheduler? scheduler = null;

        GuildLocalHostPolicy restartedPolicy = f.NewPolicy();

        SignetAuthority restartedAuthority = new(new(f.Connections), f.Gate, restartedPolicy, TimeProvider.System);

        GuildQuestStore restartedStore = new(new(f.Connections, f.Gate, TimeProvider.System), restartedAuthority,
            restartedPolicy, new GuildBudgetLedger(), TimeProvider.System, new PortAdmission(), f.Readiness);

        var restartedOwner = await restartedPolicy.CreateNativeOperatorAsync(Guid.NewGuid(), default);

        Assert.True(restartedOwner.IsSuccess, restartedOwner.Error.Message);

        Assert.True(restartedPolicy.Authenticates(restartedOwner.Value));

        Assert.Equal(f.Policy.OperatorPrincipalId, restartedPolicy.OperatorPrincipalId);

        var cleanupTarget = await restartedStore.GetAsync(restartedOwner.Value, root.Value.Quest.Id, default);

        if (erased) Assert.True(cleanupTarget.IsFailure);
        else
        {
            Assert.True(cleanupTarget.IsSuccess, cleanupTarget.Error.Message);

            Assert.True(cleanupTarget.Value.Quest.CancellationRequested);
        }

        var cleanup = await restartedStore.CreateLocalCleanupDriverAsync(restartedOwner.Value, root.Value.Quest.Id, default);

        Assert.True(cleanup.IsSuccess, cleanup.Error.Message);

        Assert.Equal(GuildCallerKind.HostReceipt, cleanup.Value.Caller!.Kind);

        Assert.True((await restartedStore.ReadLocalInputAsync(cleanup.Value, default)).IsFailure);

        Assert.True((await restartedStore.PrepareDispatchAsync(cleanup.Value, Guid.NewGuid(), new string('C', 64),
            cleanup.Value.Execution.RunId, false, default)).IsFailure);

        Assert.True((await restartedAuthority.VerifyAsync(cleanup.Value.Execution,
            new(GuildAuthorityAction.Start, cleanup.Value.Execution.QuestId), default)).IsFailure);

        GuildCallerContext forgedCaller = new(GuildCallerKind.HostReceipt, restartedOwner.Value.PrincipalId!.Value,
            Guid.NewGuid(), f.Generation, receiptId: cleanup.Value.Execution.QuestId, receiptSource: GuildLocalHostPolicy.LocalCleanupReceiptSource);

        Assert.True((await restartedStore.ReadLocalPortAsync(cleanup.Value with { Caller = forgedCaller },
            cleanup.Value.Execution.QuestId, default)).IsFailure);

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_dispatches"));

        ServiceCollection registration = new();

        registration.AddSingleton(restartedStore);

        registration.AddSingleton(restartedPolicy);

        registration.AddSingleton(restartedAuthority);

        registration.AddSingleton(new GuildCohortStore(new(f.Connections, f.Gate, TimeProvider.System), restartedAuthority,
            restartedStore, new(), restartedPolicy, TimeProvider.System));

        registration.AddSingleton<ILongRunningOperationStore>(new LongRunningOperationStore(db, f.Connections));

        registration.AddSingleton<IGuildLocalExecutionEngine>(engine);

        registration.AddSingleton<IGuildLocalInferenceRuntime>(new SchedulerInference());

        registration.AddSingleton<IGuildLocalQuestScheduler>(_ => scheduler!);

        registration.AddSingleton(ownership);

        registration.AddSingleton(TimeProvider.System);

        registration.AddScoped<GuildLocalPort>();

        await using ServiceProvider services = registration.BuildServiceProvider();

        scheduler = new(services.GetRequiredService<IServiceScopeFactory>(), f.Settings, f.Connections, ownership,
            TimeProvider.System, NullLogger<GuildLocalQuestScheduler>.Instance, new SchedulerReadiness(false), f.Gate);

        using (scheduler)
        {
            await scheduler.StartAsync(default);

            try
            {
                using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));

                while (await f.ScalarAsync("SELECT count(*) FROM guild_result_receipts") < 3)
                    await Task.Delay(20, deadline.Token);

                Assert.Equal(3L, await f.ScalarAsync("SELECT count(*) FROM guild_result_receipts WHERE OutcomeCode=5"));

                Assert.Equal(0, engine.Calls);

                Assert.True((await f.Authority.VerifyAsync(setup.Execution,
                    new(GuildAuthorityAction.Issue, setup.Execution.QuestId), default)).IsFailure);

                Assert.Equal(3L, await f.ScalarAsync("SELECT count(*) FROM guild_quests"));

                if (erased)
                {
                    GuildCohortStore cohorts = services.GetRequiredService<GuildCohortStore>();

                    GuildCohortCleanupParticipant participant = new(f.Connections, cohorts, restartedPolicy);

                    GuildCleanupOwner erasedOwner = new(2, root.Value.Quest.SessionId!.Value, null);

                    while (await f.ScalarAsync($"SELECT Retired FROM guild_cohorts WHERE Id='{setup.Cohort.Id:D}' COLLATE NOCASE") == 0)
                    {
                        _ = await participant.ReconcileAsync(erasedOwner, deadline.Token);

                        await Task.Delay(20, deadline.Token);
                    }

                    Assert.True((await participant.ReconcileAsync(erasedOwner, deadline.Token)).IsSuccess);

                    Assert.Equal(1L, await f.ScalarAsync($"SELECT count(*) FROM guild_result_outbox WHERE QuestId='{root.Value.Quest.Id:D}' COLLATE NOCASE AND RecipientQuestId IS NULL AND DeliveryStateCode=3 AND AcknowledgedAtUtc IS NOT NULL"));

                    var questClosure = await new GuildQuestCleanupParticipant(f.Connections, TimeProvider.System)
                        .ReconcileAsync(erasedOwner, deadline.Token);

                    Assert.True(questClosure.IsSuccess, $"{questClosure.Error.Message}; resultOutbox={await f.ScalarAsync("SELECT count(*) FROM guild_result_outbox WHERE DeliveryStateCode=1")}; portalOutbox={await f.ScalarAsync("SELECT count(*) FROM guild_portal_outbox WHERE DeliveryStateCode=1")}; openAccounts={await f.ScalarAsync("SELECT count(*) FROM guild_budget_accounts WHERE Closed=0")}; accounting={await f.ScalarAsync("SELECT count(*) FROM guild_quests WHERE AccountingStateCode<>0")}");

                    Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_cohort_stop_frontiers WHERE Completed=0"));
                }
            }
            finally
            {
                engine.Release.TrySetResult();

                await scheduler.StopAsync(default);
            }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Local_cleanup_for_erased_owner_refuses_restore_inert_or_changed_authority_epoch(bool restored)
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var setup = await CreateCohortAuthorityAsync(f, false);

        var owner = await f.OperatorAsync();

        var root = await f.Store.GetAsync(owner, setup.Execution.RootQuestId, default);

        Assert.True(root.IsSuccess, root.Error.Message);

        await SealLocalOwnerAsync(f, root.Value.Quest.SessionId!.Value);

        GuildCallerContext forged = new(GuildCallerKind.Operator, owner.PrincipalId!.Value, Guid.NewGuid(), f.Generation);

        Assert.True((await f.Store.CreateLocalCleanupDriverAsync(forged, root.Value.Quest.Id, default)).IsFailure);

        var cleanup = await f.Store.CreateLocalCleanupDriverAsync(owner, root.Value.Quest.Id, default);

        Assert.True(cleanup.IsSuccess, cleanup.Error.Message);

        if (restored)
            await f.ExecuteAsync($"UPDATE guild_signets SET RestoredInert=1,Revision=Revision+1 WHERE QuestId='{root.Value.Quest.Id:D}' COLLATE NOCASE");
        else
            await f.ExecuteAsync($"UPDATE guild_signet_authority_state SET Epoch='{Guid.NewGuid().ToString("D").ToUpperInvariant()}' WHERE StateKey=1");

        Assert.True((await f.Store.ReadLocalPortAsync(cleanup.Value, root.Value.Quest.Id, default)).IsFailure);

        Assert.True((await f.Store.CreateLocalCleanupDriverAsync(owner, root.Value.Quest.Id, default)).IsFailure);

        Assert.True((await f.Store.ReadLocalInputAsync(cleanup.Value, default)).IsFailure);

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_result_receipts"));
    }

    private static async Task SealLocalOwnerAsync(Fixture f, Guid sessionId)
    {
        await using var connection = await f.OpenAsync();

        GuildCleanupOwner owner = new(2, sessionId, null);

        GuildSignetCleanupParticipant signets = new(f.Gate);

        Assert.True((await signets.PrepareSealAsync(owner, connection, default)).IsSuccess);

        GuildCohortStore cohorts = new(new(f.Connections, f.Gate, TimeProvider.System), f.Authority,
            f.Store, new GuildBudgetLedger(), f.Policy, TimeProvider.System);

        var prepared = await new GuildCohortCleanupParticipant(f.Connections, cohorts, f.Policy)
            .PrepareSealAsync(owner, connection, default);

        Assert.True(prepared.IsFailure);

        Assert.Equal(RetroDownfall.Arcanum.Core.Primitives.ErrorCodes.Guild.ReconciliationRequired, prepared.Error.Code);

        await using (var transaction = connection.BeginTransaction())
        {
            Assert.True((await new GuildQuestCleanupParticipant(f.Connections, TimeProvider.System)
                .SealAsync(owner, connection, transaction, default)).IsSuccess);

            await transaction.CommitAsync();
        }

        using (RetroDownfall.Arcanum.Infrastructure.Data.Covenant.CovenantSqliteConnectionInitializer.Instance.Authorize(
            connection, RetroDownfall.Arcanum.Infrastructure.Data.Covenant.CovenantSqliteAuthorizationKind.SessionRetention))
        {
            await using var command = connection.CreateCommand();

            command.CommandText = "DELETE FROM Sessions WHERE replace(lower(Id),'-','')=$id";

            command.Parameters.AddWithValue("$id", sessionId.ToString("N"));

            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
    }
}
