using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public async Task Cohort_retires_more_than_one_page_of_real_stale_nodes_before_rebuilding()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (owner, _, root, _, cohort, members) = await ConvergenceFixtureAsync(f,
            GuildConvergenceKind.All, null, "text/plain", 91);

        var step = await f.Authority.InQuestTransactionAsync<GuildCohortSettlementStep>(root.Quest.RootQuestId,
            (connection, transaction, _, ct) => GuildCohortStore.AdvanceSettlementWithinAsync(connection, transaction, cohort, ct), default);

        Assert.True(step.IsSuccess, step.Error.Message);

        for (int page = 0; page < 100 && !step.Value.Complete; page++)
        {
            step = await f.Authority.InQuestTransactionAsync<GuildCohortSettlementStep>(root.Quest.RootQuestId,
                (connection, transaction, _, ct) => GuildCohortStore.AdvanceSettlementWithinAsync(connection, transaction, cohort, ct), default);

            Assert.True(step.IsSuccess, step.Error.Message);
        }

        Assert.True(step.Value.Complete);

        long oldNodes = await f.ScalarAsync("SELECT count(*) FROM guild_cohort_settlement_nodes WHERE Generation=1");

        Assert.True(oldNodes > 90);

        await CommitConvergenceResultAsync(f, owner, members[0], "changed evidence");

        step = await f.Authority.InQuestTransactionAsync<GuildCohortSettlementStep>(root.Quest.RootQuestId,
            (connection, transaction, _, ct) => GuildCohortStore.AdvanceSettlementWithinAsync(connection, transaction, cohort, ct), default);

        Assert.True(step.IsSuccess, step.Error.Message);

        Assert.False(step.Value.Complete);

        Assert.Null(step.Value.Snapshot);

        Assert.Equal(2L, await f.ScalarAsync("SELECT Generation FROM guild_cohort_settlement_state"));

        long remaining = await f.ScalarAsync("SELECT count(*) FROM guild_cohort_settlement_nodes WHERE Generation=1");

        Assert.Equal(oldNodes - 90, remaining);

        GuildLocalHostPolicy restartedPolicy = f.NewPolicy();

        using SignetAuthority restarted = new(new(f.Connections), f.Gate, restartedPolicy, TimeProvider.System);

        var initialized = await restartedPolicy.CreateNativeOperatorAsync(Guid.NewGuid(), default);

        Assert.True(initialized.IsSuccess, initialized.Error.Message);

        for (int page = 0; page < 100 && !step.Value.Complete; page++)
        {
            long before = remaining;

            step = await restarted.InQuestTransactionAsync<GuildCohortSettlementStep>(root.Quest.RootQuestId,
                (connection, transaction, _, ct) => GuildCohortStore.AdvanceSettlementWithinAsync(connection, transaction, cohort, ct), default);

            Assert.True(step.IsSuccess, step.Error.Message);

            remaining = await f.ScalarAsync("SELECT count(*) FROM guild_cohort_settlement_nodes WHERE Generation=1");

            Assert.InRange(before - remaining, 0, 90);

            if (before - remaining == 90) Assert.False(step.Value.Complete);
        }

        Assert.True(step.Value.Complete);

        Assert.Equal(0, remaining);

        Assert.Equal(90, step.Value.PendingMembers);

        var expected = await restarted.InQuestTransactionAsync<GuildBudgetSnapshot>(root.Quest.RootQuestId,
            (connection, transaction, _, ct) => new GuildBudgetLedger().ReadWithinAsync(connection, transaction, cohort.AllocationAccountId, ct), default);

        Assert.True(expected.IsSuccess, expected.Error.Message);

        Assert.Equal(expected.Value, step.Value.Snapshot);
    }

    [Fact]
    public async Task Operator_correction_invalidates_paged_cohort_cache_without_rewriting_initial_receipt()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (owner, _, root, _, cohort, members) = await ConvergenceFixtureAsync(f,
            GuildConvergenceKind.All, null, "text/plain", 23);

        GuildQuestStored member = members[0];

        GuildLocalInferenceExecution execution = await ToolExecutionAsync(f, owner, member);

        var opened = await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

        Assert.True(opened.IsSuccess, opened.Error.Message);

        await using var lease = opened.Value;

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(lease.Connection);

        BudgetReservationService daily = new(db);

        GuildBudgetEffectRequest effect = new(Guid.NewGuid(), member.Quest.Id, execution.Dispatch.DispatchId,
            member.Quest.RunId, GuildBudgetEffectKind.LocalProvider, Guid.NewGuid(), null, null, null);

        var executed = await f.Store.ExecuteProviderEffectAsync(execution, daily, effect,
            _ => Task.FromResult(Result<string>.Success("answer")), default);

        Assert.True(executed.IsSuccess, executed.Error.Message);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        BillableOperationRecord bill = new(member.Quest.RunId, BillableOperationType.Chat, "provider", "model", "guild",
            now, now, 100, 20, 5, 40, "{}", .25m, BillableOperationStatus.Completed, "request-id");

        var initial = await f.Store.SettleProviderEffectAsync(execution, daily, effect, bill, default);

        Assert.True(initial.IsSuccess, initial.Error.Message);

        var cached = await f.Authority.InQuestTransactionAsync<GuildCohortSettlementStep>(root.Quest.RootQuestId,
            (connection, transaction, _, ct) => GuildCohortStore.AdvanceSettlementWithinAsync(connection, transaction, cohort, ct), default);

        Assert.True(cached.IsSuccess, cached.Error.Message);

        Assert.False(cached.Value.Complete);

        for (int page = 0; page < 30 && !cached.Value.Complete; page++)
        {
            cached = await f.Authority.InQuestTransactionAsync<GuildCohortSettlementStep>(root.Quest.RootQuestId,
                (connection, transaction, _, ct) => GuildCohortStore.AdvanceSettlementWithinAsync(connection, transaction, cohort, ct), default);

            Assert.True(cached.IsSuccess, cached.Error.Message);
        }

        Assert.True(cached.Value.Complete);

        Assert.Equal(.25m, cached.Value.Snapshot!.KnownUsd);

        GuildSpendCorrectionRequest request = new(Guid.NewGuid(), initial.Value.Revision, GuildSpendOrigin.OperatorAttested,
            new(110, 25, 45, 6, .30m), new string('A', 64));

        var corrected = await f.Store.CorrectOperatorSpendAsync(owner, member.Quest.Id, effect.EffectId, request, default);

        Assert.True(corrected.IsSuccess, corrected.Error.Message);

        Assert.Equal(.30m, corrected.Value.Receipt.Facts.KnownUsd);

        var recomputed = await f.Authority.InQuestTransactionAsync<GuildCohortSettlementStep>(root.Quest.RootQuestId,
            (connection, transaction, _, ct) => GuildCohortStore.AdvanceSettlementWithinAsync(connection, transaction, cohort, ct), default);

        Assert.True(recomputed.IsSuccess, recomputed.Error.Message);

        Assert.False(recomputed.Value.Complete);

        Assert.Equal(2L, await f.ScalarAsync("SELECT Generation FROM guild_cohort_settlement_state"));

        for (int page = 0; page < 30 && !recomputed.Value.Complete; page++)
        {
            recomputed = await f.Authority.InQuestTransactionAsync<GuildCohortSettlementStep>(root.Quest.RootQuestId,
                (connection, transaction, _, ct) => GuildCohortStore.AdvanceSettlementWithinAsync(connection, transaction, cohort, ct), default);

            Assert.True(recomputed.IsSuccess, recomputed.Error.Message);
        }

        Assert.True(recomputed.Value.Complete);

        Assert.Equal(.30m, recomputed.Value.Snapshot!.KnownUsd);

        var ordinary = await f.Authority.InQuestTransactionAsync<GuildBudgetSnapshot>(root.Quest.RootQuestId,
            (connection, transaction, _, ct) => new GuildBudgetLedger().ReadWithinAsync(connection, transaction, cohort.AllocationAccountId, ct), default);

        Assert.True(ordinary.IsSuccess, ordinary.Error.Message);

        Assert.Equal(ordinary.Value, recomputed.Value.Snapshot);

        var replay = await f.Store.SettleProviderEffectAsync(execution, daily, effect, bill, default);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.Equal(initial.Value, replay.Value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(3)]
    [InlineData(1)]
    public async Task Cohort_finalization_rechecks_live_reservation_even_when_settlement_epoch_is_unchanged(int disposition)
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (owner, issuer, root, cohorts, cohort, members) = await ConvergenceFixtureAsync(f,
            GuildConvergenceKind.All, null, "text/plain", 1);

        GuildQuestStored member = members[0];

        GuildLocalInferenceExecution execution = await ToolExecutionAsync(f, owner, member);

        var opened = await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

        Assert.True(opened.IsSuccess, opened.Error.Message);

        await using var lease = opened.Value;

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(lease.Connection);

        BudgetReservationService daily = new(db);

        GuildQuestStore store = f.CreateDailyStore(daily);

        execution = new(store, execution.Driver, execution.Dispatch);

        GuildBudgetEffectRequest effect = new(Guid.NewGuid(), member.Quest.Id, execution.Dispatch.DispatchId,
            member.Quest.RunId, GuildBudgetEffectKind.LocalProvider, Guid.NewGuid(), null, 1m, 1000);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        bool unknown = disposition is 1 or 3;

        BillableOperationRecord bill = new(member.Quest.RunId, BillableOperationType.Chat, "provider", "model", "guild",
            now, now, 100, 20, 5, 40, "{}", unknown ? 0m : .25m,
            unknown ? BillableOperationStatus.Failed : BillableOperationStatus.Completed, "request-id");

        var sent = unknown
            ? await store.ExecuteAndSettleProviderResponseAsync(execution, daily, effect,
                _ => Task.FromResult(Result<string>.Success("answer")), _ => bill, default)
            : await store.ExecuteProviderEffectAsync(execution, daily, effect,
                _ => Task.FromResult(Result<string>.Success("answer")), default);

        Assert.True(sent.IsSuccess, sent.Error.Message);

        var settled = await store.SettleProviderEffectAsync(execution, daily, effect, bill, default);

        Assert.True(settled.IsSuccess, settled.Error.Message);

        if (unknown)
        {
            Assert.True(settled.Value.UnknownUsd);

            Assert.True(settled.Value.UnknownTokens);
        }

        Guid reservation;

        await using (var find = lease.Connection.CreateCommand())
        {
            find.CommandText = "SELECT ReservationId FROM guild_budget_effects WHERE EffectId=$id";

            find.Parameters.AddWithValue("$id", effect.EffectId.ToString("D").ToUpperInvariant());

            reservation = Guid.Parse(Assert.IsType<string>(await find.ExecuteScalarAsync()));
        }

        await using (var query = lease.Connection.CreateCommand())
        {
            query.CommandText = "SELECT Revision FROM guild_dispatches WHERE DispatchId=$id";

            query.Parameters.AddWithValue("$id", execution.Dispatch.DispatchId.ToString("D").ToUpperInvariant());

            long revision = (long)(await query.ExecuteScalarAsync())!;

            var confirmed = await store.RecordDispatchEvidenceAsync(execution.Driver,
                new(execution.Dispatch.DispatchId, revision, GuildDispatchEvidence.Confirmed, LocalChildId: execution.Dispatch.LocalChildId), default);

            Assert.True(confirmed.IsSuccess, confirmed.Error.Message);
        }

        var currentMember = await f.Store.GetAsync(owner, member.Quest.Id, default);

        Assert.True(currentMember.IsSuccess, currentMember.Error.Message);

        byte[] bytes = System.Text.Encoding.UTF8.GetBytes("answer");

        string digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));

        GuildDataRef content = new(Guid.NewGuid(), GuildContentKind.QuestResult, 1, digest, "text/plain", bytes.Length, false);

        GuildQuestResult result = new(new(member.Quest.Id, Guid.NewGuid(), digest, content, QuestState.Completed, null), bytes,
            WorkStoppedProven: true, DesignatedResultCount: 1, DesignatedResultUtf8: bytes, DesignatedResultMimeType: "text/plain");

        var committed = await store.CommitResultAsync(execution.Driver, new(Guid.NewGuid(), digest), currentMember.Value.Quest.Revision, result, default);

        Assert.True(committed.IsSuccess, committed.Error.Message);

        var current = await cohorts.GetAsync(issuer, cohort.Id, default);

        Assert.True(current.IsSuccess, current.Error.Message);

        var closed = await cohorts.CloseAsync(issuer, cohort.Id, new(Guid.NewGuid(), current.Value.Revision), default);

        Assert.True(closed.IsSuccess, closed.Error.Message);

        _ = await WaitForConvergenceAsync(cohorts, owner, cohort.Id, value => value.State == GuildCohortState.Draining);

        var stopped = await f.Store.RefreshCancellationConvergenceAsync(owner, member.Quest.Id, default);

        Assert.True(stopped.IsSuccess && stopped.Value, stopped.Error.Message);

        var cached = await f.Authority.InQuestTransactionAsync<GuildCohortSettlementStep>(root.Quest.RootQuestId,
            (connection, transaction, _, ct) => GuildCohortStore.AdvanceSettlementWithinAsync(connection, transaction, cohort, ct), default);

        Assert.True(cached.IsSuccess, cached.Error.Message);

        Assert.True(cached.Value.Complete);

        long epoch = await f.ScalarAsync("SELECT Revision FROM guild_cohort_settlement_clock");

        if (disposition != 0)
        {
            await using var mutate = lease.Connection.CreateCommand();

            mutate.CommandText = "UPDATE BudgetReservations SET Status=$status,ReconciledUsd='0' WHERE Id=$id";

            mutate.Parameters.AddWithValue("$status", disposition);

            mutate.Parameters.AddWithValue("$id", reservation.ToString("N"));

            Assert.Equal(1, await mutate.ExecuteNonQueryAsync());
        }

        Assert.Equal(epoch, await f.ScalarAsync("SELECT Revision FROM guild_cohort_settlement_clock"));

        if (disposition != 0)
        {
            var blocked = await cohorts.ReconcileAsync(owner, cohort.Id, default);

            Assert.True(blocked.IsSuccess, blocked.Error.Message);

            Assert.Equal(GuildCohortState.Draining, blocked.Value.State);

            if (disposition is 1 or 3)
            {
                Assert.True(blocked.Value.Spend.UnknownUsd);

                Assert.True(blocked.Value.Spend.UnknownTokens);
            }
        }
        else
        {
            _ = await WaitForConvergenceAsync(cohorts, owner, cohort.Id, value => value.State == GuildCohortState.Completed);
        }
    }
}
