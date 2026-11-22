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
    [InlineData(null, false)]
    [InlineData(BudgetReservationStatus.UncertainPaid, false)]
    [InlineData(BudgetReservationStatus.Expired, true)]
    [InlineData(BudgetReservationStatus.Reconciled, true)]
    public Task Cohort_finalization_rechecks_live_reservation_even_when_settlement_epoch_is_unchanged(
        BudgetReservationStatus? replacementStatus, bool unknownUsage) =>
        VerifyCohortProviderFinalizationAsync(historicalReservation: true, replacementStatus, unknownUsage);

    [Fact]
    public Task Cohort_finalizes_a_new_unmetered_provider_result_without_a_reservation() =>
        VerifyCohortProviderFinalizationAsync(historicalReservation: false, replacementStatus: null, unknownUsage: false);

    private static async Task VerifyCohortProviderFinalizationAsync(
        bool historicalReservation, BudgetReservationStatus? replacementStatus, bool unknownUsage)
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

        BudgetReservationService? daily = historicalReservation ? new(db) : null;

        GuildQuestStore store = daily is null ? f.Store : f.CreateDailyStore(daily);

        execution = new(store, execution.Driver, execution.Dispatch);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        BudgetReservation? reservation = historicalReservation
            ? await LegacyBudgetFixture.SeedAsync(db, new(member.Quest.RunId, 1m, now.AddHours(1),
                BudgetReservationService.UtcBudgetPeriod(now)))
            : null;

        GuildBudgetEffectRequest effect = new(Guid.NewGuid(), member.Quest.Id, execution.Dispatch.DispatchId,
            member.Quest.RunId, GuildBudgetEffectKind.LocalProvider, Guid.NewGuid(), reservation?.Id, reservation?.ReservedUsd,
            historicalReservation ? 1000L : null);

        BillableOperationRecord bill = new(member.Quest.RunId, BillableOperationType.Chat, "provider", "model", "guild",
            now, now, 100, 20, 5, 40, "{}", historicalReservation && !unknownUsage ? .25m : 0m,
            unknownUsage ? BillableOperationStatus.Failed : BillableOperationStatus.Completed, "request-id");

        var sent = unknownUsage || reservation is null
            ? await store.ExecuteAndSettleProviderResponseAsync(execution, daily, effect,
                _ => Task.FromResult(Result<string>.Success("answer")), _ => bill, default)
            : await store.ExecuteProviderEffectAsync(execution, daily, effect,
                _ => Task.FromResult(Result<string>.Success("answer")), default);

        Assert.True(sent.IsSuccess, sent.Error.Message);

        var settled = daily is null
            ? await f.Authority.InQuestTransactionAsync<GuildBudgetEffect>(root.Quest.RootQuestId,
                (connection, transaction, _, ct) => new GuildBudgetLedger().ReadEffectWithinAsync(connection, transaction, effect.EffectId, ct),
                default)
            : await store.SettleProviderEffectAsync(execution, daily, effect, bill, default);

        Assert.True(settled.IsSuccess, settled.Error.Message);

        Assert.Equal(reservation?.Id, settled.Value.Request.ReservationId);

        bool unknownUsd = unknownUsage || reservation is null;

        Assert.Equal(unknownUsd, settled.Value.UnknownUsd);

        Assert.Equal(unknownUsage, settled.Value.UnknownTokens);

        Assert.Equal(historicalReservation ? 1L : 0L, await f.ScalarAsync("SELECT count(*) FROM BudgetReservations"));

        if (reservation is not null)
        {
            await using var find = lease.Connection.CreateCommand();

            find.CommandText = "SELECT Status,ReconciledUsd FROM BudgetReservations WHERE Id=$id";

            find.Parameters.AddWithValue("$id", reservation.Id.ToString("N"));

            await using var row = await find.ExecuteReaderAsync();

            Assert.True(await row.ReadAsync());

            Assert.Equal(unknownUsage ? BudgetReservationStatus.UncertainPaid : BudgetReservationStatus.Reconciled,
                (BudgetReservationStatus)row.GetInt32(0));

            Assert.Equal(bill.ActualCostUsd, ExactUsdText.Read(row, 1));
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

        if (replacementStatus is { } replacement)
        {
            Assert.NotNull(reservation);

            await using var mutate = lease.Connection.CreateCommand();

            mutate.CommandText = "UPDATE BudgetReservations SET Status=$status,ReconciledUsd='0' WHERE Id=$id";

            mutate.Parameters.AddWithValue("$status", (int)replacement);

            mutate.Parameters.AddWithValue("$id", reservation.Id.ToString("N"));

            Assert.Equal(1, await mutate.ExecuteNonQueryAsync());
        }

        Assert.Equal(epoch, await f.ScalarAsync("SELECT Revision FROM guild_cohort_settlement_clock"));

        if (replacementStatus is not null)
        {
            var blocked = await cohorts.ReconcileAsync(owner, cohort.Id, default);

            Assert.True(blocked.IsSuccess, blocked.Error.Message);

            Assert.Equal(GuildCohortState.Draining, blocked.Value.State);

            Assert.Equal(unknownUsd, blocked.Value.Spend.UnknownUsd);

            Assert.Equal(unknownUsage, blocked.Value.Spend.UnknownTokens);
        }
        else
        {
            CohortConvergenceDto completed = await WaitForConvergenceAsync(cohorts, owner, cohort.Id,
                value => value.State == GuildCohortState.Completed);

            Assert.Equal(unknownUsd, completed.Spend.UnknownUsd);

            Assert.Equal(unknownUsage, completed.Spend.UnknownTokens);

            Assert.Equal(historicalReservation ? 1L : 0L, await f.ScalarAsync("SELECT count(*) FROM BudgetReservations"));
        }
    }
}
