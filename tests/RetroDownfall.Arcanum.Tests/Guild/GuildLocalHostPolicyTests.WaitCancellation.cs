using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Theory]
    [InlineData("waiting", true)]
    [InlineData("parked-wait", true)]
    [InlineData("unpaid-parked-wait", true)]
    [InlineData("accepted-continuation", false)]
    [InlineData("uncertain-effect", false)]
    [InlineData("missing-checkpoint", false)]
    public async Task Persisted_paid_input_wait_cancels_after_restart_only_at_the_exact_stopped_frontier(string frontier, bool canceled)
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildCallerContext caller = await f.OperatorAsync();

        var opened = await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

        Assert.True(opened.IsSuccess, opened.Error.Message);

        await using var connection = opened.Value;

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection.Connection);

        BudgetReservationService daily = new(db);

        GuildQuestStore store = new(new(f.Connections, f.Gate, TimeProvider.System), f.Authority, f.Policy,
            new GuildBudgetLedger(daily), TimeProvider.System, new PortAdmission(), f.Readiness);

        GuildIssueRootRequest requested = frontier == "parked-wait" ? f.Request with
        {
            Grant = f.Request.Grant with { Delegation = [GuildDelegationKind.Local, GuildDelegationKind.Cohort] },
        } : f.Request;

        var issued = await store.IssueAsync(caller, new(f.Generation, null, caller.InvocationId, caller.InvocationId,
            requested.IdempotencyKey), requested, default);

        Assert.True(issued.IsSuccess, issued.Error.Message);

        GuildQuestDto quest = issued.Value.Quest;

        var recovery = await f.Policy.CreateRecoveryAsync(caller, quest.Id, quest.OperationId, quest.SignetRevision, default);

        Assert.True(recovery.IsSuccess, recovery.Error.Message);

        var execution = await f.Authority.RebindAsync(recovery.Value, default);

        Assert.True(execution.IsSuccess, execution.Error.Message);

        var run = await f.Policy.CreateRunCallerAsync(f.Authority, execution.Value, quest.Id, default);

        Assert.True(run.IsSuccess, run.Error.Message);

        Guid? cohortId = null;

        if (frontier == "parked-wait")
        {
            GuildCohortStore cohortStore = new(new(f.Connections, f.Gate, TimeProvider.System), f.Authority,
                store, new GuildBudgetLedger(daily), f.Policy, TimeProvider.System);

            var created = await cohortStore.CreateAsync(run.Value, new(Guid.NewGuid(), quest.Id, "Parked issuer",
                new(GuildConvergenceKind.All, null), new("text/plain"), null), default);

            Assert.True(created.IsSuccess, created.Error.Message);

            cohortId = created.Value.Id;
        }

        LongRunningOperationStore operations = new(db, f.Connections);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        var leased = await operations.TryAcquireLeaseAsync(quest.OperationId, "before-restart", now, now.AddMinutes(5));

        Assert.True(leased.Acquired);

        GuildQuestDriver driver = new(execution.Value, "before-restart", leased.Operation.Revision, run.Value);

        GuildLocalExecutionBinding binding;

        await using (SqliteTransaction transaction = connection.Connection.BeginTransaction())
            binding = (await GuildLocalExecutionStore.ReadWithinAsync(connection.Connection, transaction, quest.Id, default)).Value;

        var dispatch = await store.PrepareDispatchAsync(driver, quest.Id, binding.Input.Digest, quest.RunId, false, default);

        Assert.True(dispatch.IsSuccess, dispatch.Error.Message);

        GuildLocalInferenceExecution invocation = new(store, driver, dispatch.Value);

        PaidPortEngine paid = new(daily, true);

        bool paidCall = frontier != "unpaid-parked-wait";

        PaidWaitEvidence? beforeEvidence = null;

        if (paidCall)
        {
            var paidResult = await paid.ExecuteGuildAsync(binding, (await store.ReadLocalInputAsync(driver, default)).Value,
                invocation, new UnusedInference(), null!, default);

            Assert.True(paidResult.IsSuccess, paidResult.Error.Message);

            beforeEvidence = await ReadPaidWaitEvidenceAsync(connection.Connection, quest.Id);
        }

        Assert.True((await store.ResolveLocalDispatchAsync(driver, quest.Id, default)).IsSuccess);

        Guid question = Guid.NewGuid(), continuation = Guid.NewGuid();

        GuildLocalWaitSnapshot snapshot = new(1, binding.Input.Digest, 0,
            [new() { Index = 0, Description = "Choose an option", Attempts = 1 }],
            new() { CurrentStep = 0, ParentApprenticeId = binding.ParentApprenticeId, LaunchRequested = true, EscalationReason = "Which option?" });

        var waiting = await store.WaitForInputAsync(driver, question, continuation, new(quest.Id, snapshot), default);

        Assert.True(waiting.IsSuccess, waiting.Error.Message);

        if (frontier == "accepted-continuation")
        {
            var accepted = await store.AcceptContinuationAsync(caller, quest.Id,
                new(Guid.NewGuid(), waiting.Value.Quest.Revision, continuation, "text/plain", "Use the first option"), default);

            Assert.True(accepted.IsSuccess, accepted.Error.Message);
        }

        if (frontier == "uncertain-effect")
        {
            await f.ExecuteAsync("""
                INSERT INTO guild_budget_effects
                SELECT 'EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE',AccountId,QuestId,DispatchId,AccountingAssociationId,KindCode,
                    'EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE',NULL,UsdBound,TokenBound,'0',0,1,1,1,1
                FROM guild_budget_effects;
                """);
        }

        if (frontier == "missing-checkpoint") await f.ExecuteAsync("DELETE FROM guild_local_wait_checkpoints");

        if (frontier is "parked-wait" or "unpaid-parked-wait")
            await f.ExecuteAsync("UPDATE LongRunningOperations SET State=7,TerminalErrorCode='Guild.AwaitingInput',Revision=Revision+1");

        await f.ExecuteAsync("UPDATE LongRunningOperations SET LeaseOwner=NULL,LeaseExpiresAt=NULL,Revision=Revision+1");

        GuildLocalHostPolicy restartedPolicy = f.NewPolicy();

        var restartedCaller = await restartedPolicy.CreateNativeOperatorAsync(Guid.NewGuid(), default);

        Assert.True(restartedCaller.IsSuccess, restartedCaller.Error.Message);

        using SignetAuthority restartedAuthority = new(new(f.Connections), f.Gate, restartedPolicy, TimeProvider.System);

        GuildQuestStore restartedStore = new(new(f.Connections, f.Gate, TimeProvider.System), restartedAuthority, restartedPolicy,
            new GuildBudgetLedger(daily), TimeProvider.System, new PortAdmission(), f.Readiness);

        var current = await restartedStore.GetAsync(restartedCaller.Value, quest.Id, default);

        Assert.True(current.IsSuccess, current.Error.Message);

        if (cohortId is { } retainedCohort)
        {
            GuildCohortStore restartedCohorts = new(new(f.Connections, f.Gate, TimeProvider.System), restartedAuthority,
                restartedStore, new GuildBudgetLedger(daily), restartedPolicy, TimeProvider.System);

            var parkedStep = await restartedCohorts.ReconcileAsync(restartedCaller.Value, retainedCohort, default);

            Assert.True(parkedStep.IsSuccess || parkedStep.Error.Code == ErrorCodes.Guild.MaintenanceDeferred, parkedStep.Error.Message);

            Assert.Equal((long)GuildCohortState.Open,
                await f.ScalarAsync($"SELECT StateCode FROM guild_cohorts WHERE Id='{retainedCohort:D}' COLLATE NOCASE"));

            Assert.Equal(0L, await f.ScalarAsync($"SELECT COUNT(*) FROM guild_cohort_stop_frontiers WHERE CohortId='{retainedCohort:D}' COLLATE NOCASE"));
        }

        var reboundContext = await restartedPolicy.CreateRecoveryAsync(restartedCaller.Value, quest.Id, quest.OperationId,
            current.Value.Quest.SignetRevision, default);

        Assert.True(reboundContext.IsSuccess, reboundContext.Error.Message);

        var rebound = await restartedAuthority.RebindAsync(reboundContext.Value, default);

        Assert.True(rebound.IsSuccess, rebound.Error.Message);

        current = await restartedStore.GetAsync(restartedCaller.Value, quest.Id, default);

        GuildCancelQuestRequest cancel = new(Guid.NewGuid(), current.Value.Quest.Revision, GuildCancellationReason.OperatorRequested);

        GuildMutationIdentity mutation = new(cancel.IdempotencyKey, GuildQuestIntent.Compute(quest.Id, cancel).Value);

        Assert.True((await restartedStore.RequestCancellationAsync(restartedCaller.Value, mutation, quest.Id, cancel, default)).IsSuccess);

        DeterministicLocalEngine engine = new();

        GuildLocalPort port = new(restartedStore, restartedPolicy, restartedAuthority, operations, engine, new UnusedInference(),
            new ReadyPortHost(), new(), TimeProvider.System);

        var outcome = await port.RequestCancellationAsync(rebound.Value, mutation, quest.Id, quest.Id, default);

        Assert.True(outcome.IsSuccess, outcome.Error.Message);

        Assert.Equal(canceled, outcome.Value.State == QuestState.Canceled);

        Assert.Equal(0, engine.Calls);

        Assert.Equal(paidCall ? 1 : 0, paid.Calls);

        Assert.Equal(paidCall ? 1L : 0L, await f.ScalarAsync("SELECT count(*) FROM BillableOperations"));

        Assert.Equal(canceled ? 1L : 0L, await f.ScalarAsync($"SELECT count(*) FROM guild_result_receipts WHERE WorkStoppedProven=1 AND NoDispatchProven={(paidCall ? 0 : 1)}"));

        if (canceled)
        {
            var result = await restartedStore.GetAsync(restartedCaller.Value, quest.Id, default);

            Assert.True(result.IsSuccess, result.Error.Message);

            Assert.True(result.Value.Quest.CancellationConverged);

            Assert.Equal(0L, await f.ScalarAsync("SELECT COUNT(*) FROM BudgetReservations"));

            if (paidCall)
            {
                Assert.Equal(beforeEvidence, await ReadPaidWaitEvidenceAsync(connection.Connection, quest.Id));
            }
            else
            {
                Assert.Equal(0L, await f.ScalarAsync("SELECT COUNT(*) FROM guild_budget_effects"));
            }

            await using SqliteTransaction proof = connection.Connection.BeginTransaction(deferred: true);

            await using SqliteCommand account = connection.Connection.CreateCommand();

            account.Transaction = proof;

            account.CommandText = """
                SELECT a.AccountId,a.Closed,a.SettlementReceiptId,r.ResultId
                FROM guild_quests q JOIN guild_budget_accounts a ON a.AccountId=q.AllocationAccountId
                JOIN guild_result_receipts r ON r.QuestId=q.Id WHERE q.Id=$quest
                """;

            account.Parameters.AddWithValue("$quest", quest.Id.ToString("D").ToUpperInvariant());

            Guid rootAccount;

            await using (SqliteDataReader reader = await account.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());

                rootAccount = Guid.Parse(reader.GetString(0));

                Assert.Equal(1L, reader.GetInt64(1));

                Assert.Equal(reader.GetString(3), reader.GetString(2));

                Assert.False(await reader.ReadAsync());
            }

            var budget = await new GuildBudgetLedger(daily).ReadWithinAsync(connection.Connection, proof, rootAccount, default);

            Assert.True(budget.IsSuccess, budget.Error.Message);

            Assert.Equal(GuildBudgetMode.Unlimited, budget.Value.Grant.Usd.Mode);

            Assert.Equal(GuildBudgetMode.Unlimited, budget.Value.Grant.Tokens.Mode);

            Assert.Equal(paidCall, budget.Value.UnknownUsd);

            Assert.False(budget.Value.UnknownTokens);

            Assert.False(budget.Value.PhysicalBlocked);

            Assert.False(budget.Value.UsdBlocked);

            Assert.False(budget.Value.TokensBlocked);

            account.CommandText = "SELECT COUNT(*) FROM guild_budget_accounts WHERE ParentAccountId=$account AND Closed=0";

            account.Parameters.AddWithValue("$account", rootAccount.ToString("D").ToUpperInvariant());

            Assert.Equal(cohortId is null ? 0L : 1L, await account.ExecuteScalarAsync());

            if (cohortId is { } openCohort)
            {
                account.CommandText = """
                    SELECT COUNT(*) FROM guild_cohorts c JOIN guild_budget_accounts a ON a.AccountId=c.AllocationAccountId
                    WHERE c.Id=$cohort AND c.StateCode=$open AND a.OwnerKindCode=1 AND a.OwnerId=c.Id
                      AND a.ParentAccountId=$account AND a.RootAccountId=$account AND a.Closed=0 AND a.SettlementReceiptId IS NULL
                    """;

                account.Parameters.AddWithValue("$cohort", openCohort.ToString("D").ToUpperInvariant());

                account.Parameters.AddWithValue("$open", (long)GuildCohortState.Open);

                Assert.Equal(1L, await account.ExecuteScalarAsync());
            }

            await proof.CommitAsync();

            GuildAccountingState expectedAccounting = frontier switch
            {
                "waiting" => GuildAccountingState.Settled,
                "parked-wait" => GuildAccountingState.Pending,
                "unpaid-parked-wait" => GuildAccountingState.Settled,
                _ => throw new InvalidOperationException(frontier),
            };

            Assert.Equal(expectedAccounting, result.Value.Quest.AccountingState);
        }
    }

    private sealed record PaidWaitEvidence(Guid EffectId, Guid SourceId, Guid AssociationId, Guid ReceiptId, long Revision, string Digest);

    private static async Task<PaidWaitEvidence> ReadPaidWaitEvidenceAsync(SqliteConnection connection, Guid questId)
    {
        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            SELECT e.EffectId,e.SourceId,e.AccountingAssociationId,r.ReceiptId,e.Revision,r.Digest,
                e.KnownUsd,e.KnownTokens,e.UnknownUsd,e.UnknownTokens,e.StateCode,e.ReservationId,e.UsdBound,
                r.KnownUsd,r.KnownTokens,r.UnknownUsd,r.UnknownTokens,r.Complete,r.NoDispatchProven
            FROM guild_budget_effects e JOIN guild_budget_evidence r ON r.EffectId=e.EffectId AND r.AppliedRevision=e.Revision
            WHERE e.QuestId=$quest AND e.KindCode=0
            """;

        command.Parameters.AddWithValue("$quest", questId.ToString("D").ToUpperInvariant());

        PaidWaitEvidence evidence;

        await using (SqliteDataReader reader = await command.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());

            byte[] digest = Assert.IsType<byte[]>(reader.GetValue(5));

            Assert.Equal(32, digest.Length);

            evidence = new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)),
                Guid.Parse(reader.GetString(3)), reader.GetInt64(4), Convert.ToHexString(digest));

            Assert.NotEqual(Guid.Empty, evidence.EffectId);

            Assert.NotEqual(Guid.Empty, evidence.SourceId);

            Assert.NotEqual(Guid.Empty, evidence.AssociationId);

            Assert.NotEqual(Guid.Empty, evidence.ReceiptId);

            Assert.True(evidence.Revision > 0);

            Assert.Equal("0", reader.GetString(6));

            Assert.Equal(10L, reader.GetInt64(7));

            Assert.Equal(1L, reader.GetInt64(8));

            Assert.Equal(0L, reader.GetInt64(9));

            Assert.Equal((long)GuildBudgetEffectState.Complete, reader.GetInt64(10));

            Assert.True(reader.IsDBNull(11));

            Assert.True(reader.IsDBNull(12));

            Assert.Equal("0", reader.GetString(13));

            Assert.Equal(10L, reader.GetInt64(14));

            Assert.Equal(1L, reader.GetInt64(15));

            Assert.Equal(0L, reader.GetInt64(16));

            Assert.Equal(1L, reader.GetInt64(17));

            Assert.Equal(0L, reader.GetInt64(18));

            Assert.False(await reader.ReadAsync());
        }

        command.CommandText = "SELECT Id,Status,InputTokens,OutputTokens FROM BillableOperations";

        await using (SqliteDataReader reader = await command.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());

            Assert.Equal(evidence.SourceId, Guid.Parse(reader.GetString(0)));

            Assert.Equal(0L, reader.GetInt64(1));

            Assert.Equal(7L, reader.GetInt64(2));

            Assert.Equal(3L, reader.GetInt64(3));

            Assert.False(await reader.ReadAsync());
        }

        await transaction.CommitAsync();

        return evidence;
    }
}
