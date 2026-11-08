using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Fact]
    public async Task Enabling_daily_budget_after_preparation_refuses_unreserved_physical_dispatch()
    {
        await using Fixture f = await Fixture.CreateAsync();

        await using SqliteConnection connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        BudgetPolicySettings policy = new() { Enabled = false, DailyLimitUsd = 10m };

        ArcanumSettings settings = new() { Cost = new CostSettings { Budget = policy } };

        BudgetReservationService daily = new(db, new TestOptionsMonitor<ArcanumSettings>(settings));

        GuildBudgetLedger ledger = new(daily);

        f.UseBudgetParticipant(ledger);

        GuildQuestStored row = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildQuestDriver driver = await f.DriverAsync(row);

        Guid dispatch = Guid.NewGuid(), accounting = Guid.NewGuid();

        Assert.True((await f.Store.PrepareDispatchAsync(driver, dispatch, ReceiptDigest("daily-enable"), accounting, false, default)).IsSuccess);

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        await using (SqliteCommand seed = connection.CreateCommand())
        {
            seed.Transaction = transaction;

            seed.CommandText = "INSERT INTO InferenceRuns(Id,RequestId,Surface,Purpose,StartedAt,Status) VALUES($run,'daily-enable','test','test',$now,0)";

            seed.Parameters.AddWithValue("$run", accounting.ToString("N"));
            seed.Parameters.AddWithValue("$now", UtcInstantText.Format(DateTimeOffset.UtcNow));

            _ = await seed.ExecuteNonQueryAsync();
        }

        GuildBudgetEffectRequest request = new(Guid.NewGuid(), row.Quest.Id, dispatch, accounting,
            GuildBudgetEffectKind.LocalProvider, Guid.NewGuid(), null, 3m, 30);

        Result<GuildBudgetEffect> prepared = await GuildBudgetPhysicalAccounting.PrepareLocalWithinAsync(connection,
            transaction, ledger, daily, request, DateTimeOffset.UtcNow.AddHours(1), default);

        Assert.True(prepared.IsSuccess, prepared.Error.Message);

        Assert.Null(prepared.Value.Request.ReservationId);

        policy.Enabled = true;

        Result<GuildBudgetEffectAdmission> admitted = await ledger.ExposeEffectWithinAsync(connection, transaction,
            request.EffectId, 1, default);

        Assert.True(admitted.IsFailure, "Live daily policy must be rechecked even when preparation persisted no daily reservation.");

        GuildBudgetEffect pending = (await ledger.ReadEffectWithinAsync(connection, transaction, request.EffectId, default)).Value;

        Assert.Equal(GuildBudgetEffectState.Prepared, pending.State);

        Assert.Equal(1L, pending.Revision);

        Assert.Null(pending.Request.ReservationId);
    }

    [Fact]
    public async Task Settled_child_consumption_is_known_spend_and_not_an_active_allocation()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildBudgetLedger ledger = new();

        f.UseBudgetParticipant(ledger);

        f.Host.Grant = f.Host.Grant with { Budget = new(new(GuildBudgetMode.Finite, 10m), new(GuildBudgetMode.Finite, 100)) };

        GuildQuestStored root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request with { Grant = f.Host.Grant }, default)).Value;

        GuildQuestStored child = await f.ChildAsync(root, await f.DriverAsync(root));

        GuildQuestDriver driver = await f.DriverAsync(child);

        Guid dispatch = Guid.NewGuid(), accounting = Guid.NewGuid();

        Assert.True((await f.Store.PrepareDispatchAsync(driver, dispatch, ReceiptDigest("settled-child"), accounting, false, default)).IsSuccess);

        await using SqliteConnection connection = await f.OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        Guid effect = Guid.NewGuid();

        Assert.True((await ledger.PrepareEffectWithinAsync(connection, transaction,
            new(effect, child.Quest.Id, dispatch, accounting, GuildBudgetEffectKind.LocalProvider, Guid.NewGuid(), null, 2m, 20), default)).IsSuccess);

        Assert.True((await ledger.ExposeAsync(connection, transaction, child.Quest.Id, child.AllocationAccountId, dispatch, accounting, default)).IsSuccess);

        Assert.True((await ledger.ApplyEvidenceWithinAsync(connection, transaction, effect,
            new(Guid.NewGuid(), ReceiptDigest("child-bill"), 1m, 10, false, false, true), default)).IsSuccess);

        Assert.True((await ledger.CloseAccountWithinAsync(connection, transaction, child.AllocationAccountId, Guid.NewGuid(), default)).IsSuccess);

        GuildBudgetSnapshot snapshot = (await ledger.ReadWithinAsync(connection, transaction, root.AllocationAccountId, default)).Value;

        Assert.Equal(1m, snapshot.KnownUsd);

        Assert.Equal(10L, snapshot.KnownTokens);

        Assert.Equal(0m, snapshot.OutstandingAllocation.Usd.Value);

        Assert.Equal(0L, snapshot.OutstandingAllocation.Tokens.Value);

        Assert.Equal(9m, snapshot.Remaining.Usd.Value);

        Assert.Equal(1L, snapshot.PricedCount);
    }

    [Fact]
    public async Task Provider_bound_violation_stops_new_authority_even_when_grant_has_remaining_capacity()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildBudgetLedger ledger = new();

        f.UseBudgetParticipant(ledger);

        f.Host.Grant = f.Host.Grant with { Budget = new(new(GuildBudgetMode.Finite, 10m), new(GuildBudgetMode.Finite, 100)) };

        GuildQuestStored row = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request with { Grant = f.Host.Grant }, default)).Value;

        GuildQuestDriver driver = await f.DriverAsync(row);

        Guid dispatch = Guid.NewGuid(), accounting = Guid.NewGuid();

        Assert.True((await f.Store.PrepareDispatchAsync(driver, dispatch, ReceiptDigest("provider-fault"), accounting, false, default)).IsSuccess);

        await using SqliteConnection connection = await f.OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        Guid sibling = (await ledger.AllocateAccountWithinAsync(connection, transaction,
            GuildBudgetOwnerKind.Quest, Guid.NewGuid(), row.AllocationAccountId,
            new(new(GuildBudgetMode.Finite, 1m), new(GuildBudgetMode.Finite, 1)), default)).Value;

        Guid effect = Guid.NewGuid();

        Assert.True((await ledger.PrepareEffectWithinAsync(connection, transaction,
            new(effect, row.Quest.Id, dispatch, accounting, GuildBudgetEffectKind.LocalProvider, Guid.NewGuid(), null, 2m, 20), default)).IsSuccess);

        Assert.True((await ledger.ExposeAsync(connection, transaction, row.Quest.Id, row.AllocationAccountId, dispatch, accounting, default)).IsSuccess);

        Assert.True((await ledger.ApplyEvidenceWithinAsync(connection, transaction, effect,
            new(Guid.NewGuid(), ReceiptDigest("bound-violation"), 3m, 30, false, false, true), default)).IsSuccess);

        GuildBudgetSnapshot state = (await ledger.ReadWithinAsync(connection, transaction, row.AllocationAccountId, default)).Value;

        Assert.Equal(3m, state.KnownUsd);

        Assert.Equal(6m, state.Remaining.Usd.Value);

        Result<Guid> refused = await ledger.AllocateAccountWithinAsync(connection, transaction,
            GuildBudgetOwnerKind.Cohort, Guid.NewGuid(), row.AllocationAccountId,
            new(new(GuildBudgetMode.Finite, 1m), new(GuildBudgetMode.Finite, 1)), default);

        Assert.Equal(ErrorCodes.Guild.GrantExceeded, refused.Error.Code);

        Assert.Equal(ErrorCodes.Guild.GrantExceeded, (await ledger.AllocateAccountWithinAsync(connection, transaction,
            GuildBudgetOwnerKind.Quest, Guid.NewGuid(), sibling,
            new(new(GuildBudgetMode.Finite, 1m), new(GuildBudgetMode.Finite, 1)), default)).Error.Code);
    }

    [Fact]
    public async Task Physical_effect_admission_grants_exactly_one_attempt_and_stale_prepared_revision_refuses()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildBudgetLedger ledger = new();

        f.UseBudgetParticipant(ledger);

        GuildQuestStored row = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildQuestDriver driver = await f.DriverAsync(row);

        Guid dispatch = Guid.NewGuid(), accounting = Guid.NewGuid();

        Assert.True((await f.Store.PrepareDispatchAsync(driver, dispatch, ReceiptDigest("once-per-call"), accounting, false, default)).IsSuccess);

        Guid effect = Guid.NewGuid();

        await using (SqliteConnection connection = await f.OpenAsync())
        {
            await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

            Assert.True((await ledger.PrepareEffectWithinAsync(connection, transaction,
                new(effect, row.Quest.Id, dispatch, accounting, GuildBudgetEffectKind.LocalProvider,
                    Guid.NewGuid(), null, 1m, 10), default)).IsSuccess);

            Assert.Equal(ErrorCodes.Guild.RevisionConflict,
                (await ledger.ExposeEffectWithinAsync(connection, transaction, effect, 2, default)).Error.Code);

            Result<GuildBudgetEffectAdmission> first = await ledger.ExposeEffectWithinAsync(connection, transaction, effect, 1, default);

            Assert.True(first.IsSuccess, first.Error.Message);

            Assert.True(first.Value.MayDispatch);

            Assert.Equal(GuildBudgetEffectState.PossiblyDispatched, first.Value.Effect.State);

            await transaction.CommitAsync();
        }

        await using (SqliteConnection connection = await f.OpenAsync())
        {
            await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

            Result<GuildBudgetEffectAdmission> replay = await new GuildBudgetLedger().ExposeEffectWithinAsync(connection, transaction, effect, 1, default);

            Assert.True(replay.IsSuccess, replay.Error.Message);

            Assert.False(replay.Value.MayDispatch);

            Assert.Equal(2L, replay.Value.Effect.Revision);
        }
    }

    [Fact]
    public async Task Cancellation_request_cannot_clear_possibly_dispatched_grant_exposure()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildBudgetLedger ledger = new();

        f.UseBudgetParticipant(ledger);

        GuildQuestStored row = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildQuestDriver driver = await f.DriverAsync(row);

        Guid dispatch = Guid.NewGuid(), accounting = Guid.NewGuid();

        Assert.True((await f.Store.PrepareDispatchAsync(driver, dispatch, ReceiptDigest("cancel-unknown"), accounting, false, default)).IsSuccess);

        await using (SqliteConnection connection = await f.OpenAsync())
        {
            await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

            Assert.True((await ledger.PrepareEffectWithinAsync(connection, transaction,
                new(Guid.NewGuid(), row.Quest.Id, dispatch, accounting, GuildBudgetEffectKind.LocalProvider,
                    Guid.NewGuid(), null, 7m, 70), default)).IsSuccess);

            await transaction.CommitAsync();
        }

        Assert.True((await f.Store.MarkPossiblyDispatchedAsync(driver, dispatch, 1, default)).IsSuccess);

        await f.ExecuteAsync($"UPDATE guild_quests SET CancellationRequested=1,Revision=Revision+1 WHERE Id='{row.Quest.Id:D}' COLLATE NOCASE");

        row = (await f.Store.GetAsync(f.Caller, row.Quest.Id, default)).Value;

        GuildQuestResult fabricated = new(new(row.Quest.Id, Guid.NewGuid(), ReceiptDigest(""), null,
            QuestState.Rejected, ErrorCodes.Guild.Rejected), null, NoDispatchProven: true);

        Assert.True((await f.Store.CommitResultAsync(driver, new(Guid.NewGuid(), ReceiptDigest("false-unsent")),
            row.Quest.Revision, fabricated, default)).IsFailure);

        await using SqliteConnection read = await f.OpenAsync();

        await using SqliteTransaction transactionRead = read.BeginTransaction(deferred: false);

        GuildBudgetSnapshot state = (await ledger.ReadWithinAsync(read, transactionRead, row.AllocationAccountId, default)).Value;

        Assert.True(state.UnknownUsd);

        Assert.Equal(7m, state.UnresolvedHolds.Usd.Value);

        Assert.Equal(70L, state.UnresolvedHolds.Tokens.Value);

        Assert.False(state.Closed);
    }

    [Fact]
    public async Task Unlimited_remote_terminal_unknown_is_retained_until_authoritative_reconciliation()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildBudgetLedger ledger = new();

        f.UseBudgetParticipant(ledger);

        GuildQuestStored row = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildQuestDriver driver = await f.DriverAsync(row);

        Guid dispatch = Guid.NewGuid(), accounting = Guid.NewGuid();

        Assert.True((await f.Store.PrepareDispatchAsync(driver, dispatch, ReceiptDigest("unlimited-remote"), accounting, false, default)).IsSuccess);

        await using SqliteConnection connection = await f.OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        GuildBudgetEffectRequest request = new(Guid.NewGuid(), row.Quest.Id, dispatch, accounting,
            GuildBudgetEffectKind.External, Guid.NewGuid(), null, null, null);

        Assert.True((await ledger.PrepareEffectWithinAsync(connection, transaction, request, default)).IsSuccess);

        Assert.True((await ledger.ExposeAsync(connection, transaction, row.Quest.Id, row.AllocationAccountId, dispatch, accounting, default)).IsSuccess);

        Assert.True((await ledger.ApplyEvidenceWithinAsync(connection, transaction, request.EffectId,
            new(Guid.NewGuid(), ReceiptDigest("unpriced-complete"), 0m, 0, true, true, true), default)).IsSuccess);

        Assert.True((await ledger.CloseAccountWithinAsync(connection, transaction, row.AllocationAccountId, Guid.NewGuid(), default)).IsSuccess);

        GuildBudgetSnapshot unknown = (await ledger.ReadWithinAsync(connection, transaction, row.AllocationAccountId, default)).Value;

        Assert.True(unknown.UnknownUsd);

        Assert.True(unknown.UnknownTokens);

        Assert.Equal(1L, unknown.UnpricedCount);

        Assert.Equal(1L, unknown.UncertainCount);

        Assert.Equal(GuildBudgetMode.Unlimited, unknown.UnresolvedHolds.Usd.Mode);

        Assert.True((await ledger.ApplyEvidenceWithinAsync(connection, transaction, request.EffectId,
            new(Guid.NewGuid(), ReceiptDigest("priced-correction"), 5m, 7, false, false, true), default)).IsSuccess);

        GuildBudgetSnapshot known = (await ledger.ReadWithinAsync(connection, transaction, row.AllocationAccountId, default)).Value;

        Assert.Equal(5m, known.KnownUsd);

        Assert.Equal(7L, known.KnownTokens);

        Assert.False(known.UnknownUsd);

        Assert.Equal(0m, known.UnresolvedHolds.Usd.Value);

        Assert.Equal(1L, known.PricedCount);
    }

    [Fact]
    public async Task Proven_never_dispatched_result_returns_the_prepared_hold_atomically()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildBudgetLedger ledger = new();

        f.UseBudgetParticipant(ledger);

        f.Host.Grant = f.Host.Grant with { Budget = new(new(GuildBudgetMode.Finite, 10m), new(GuildBudgetMode.Finite, 100)) };

        GuildQuestStored row = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request with { Grant = f.Host.Grant }, default)).Value;

        GuildQuestDriver driver = await f.DriverAsync(row);

        Guid dispatch = Guid.NewGuid(), accounting = Guid.NewGuid();

        Assert.True((await f.Store.PrepareDispatchAsync(driver, dispatch, ReceiptDigest("unsent-hold"), accounting, false, default)).IsSuccess);

        GuildBudgetEffectRequest request = new(Guid.NewGuid(), row.Quest.Id, dispatch, accounting,
            GuildBudgetEffectKind.LocalProvider, Guid.NewGuid(), null, 7m, 70);

        await using (SqliteConnection connection = await f.OpenAsync())
        {
            await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

            Assert.True((await ledger.PrepareEffectWithinAsync(connection, transaction, request, default)).IsSuccess);

            await transaction.CommitAsync();
        }

        Assert.True((await f.Store.RecordDispatchEvidenceAsync(driver,
            new(dispatch, 1, GuildDispatchEvidence.ResolvedNoDispatch, NoDispatchProven: true), default)).IsSuccess);

        row = (await f.Store.GetAsync(f.Caller, row.Quest.Id, default)).Value;

        GuildQuestResult result = new(new(row.Quest.Id, Guid.NewGuid(), ReceiptDigest(""), null,
            QuestState.Rejected, ErrorCodes.Guild.Rejected), null, NoDispatchProven: true);

        Result<GuildResultReceipt> committed = await f.Store.CommitResultAsync(driver,
            new(Guid.NewGuid(), ReceiptDigest("unsent-result")), row.Quest.Revision, result, default);

        Assert.True(committed.IsSuccess, committed.Error.Message);

        await using SqliteConnection read = await f.OpenAsync();

        await using SqliteTransaction snapshot = read.BeginTransaction(deferred: false);

        GuildBudgetSnapshot state = (await ledger.ReadWithinAsync(read, snapshot, row.AllocationAccountId, default)).Value;

        Assert.True(state.Closed);

        Assert.Equal(0m, state.KnownUsd);

        Assert.Equal(0m, state.UnresolvedHolds.Usd.Value);

        Assert.Equal(10m, state.Remaining.Usd.Value);

        Assert.Equal(GuildBudgetEffectState.ProvenNoDispatch,
            (await ledger.ReadEffectWithinAsync(read, snapshot, request.EffectId, default)).Value.State);
    }

    [Fact]
    public async Task Concurrent_own_call_and_child_allocation_cannot_spend_the_same_remainder()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildBudgetLedger ledger = new();

        f.UseBudgetParticipant(ledger);

        f.Host.Grant = f.Host.Grant with { Budget = new(new(GuildBudgetMode.Finite, 10m), new(GuildBudgetMode.Finite, 100)) };

        GuildQuestStored row = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request with { Grant = f.Host.Grant }, default)).Value;

        GuildQuestDriver driver = await f.DriverAsync(row);

        Guid dispatch = Guid.NewGuid(), accounting = Guid.NewGuid();

        Assert.True((await f.Store.PrepareDispatchAsync(driver, dispatch, ReceiptDigest("concurrent"), accounting, false, default)).IsSuccess);

        TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<Result> own = Task.Run(async () =>
        {
            await start.Task;

            await using SqliteConnection connection = await f.OpenAsync();

            await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

            Result<GuildBudgetEffect> result = await new GuildBudgetLedger().PrepareEffectWithinAsync(connection, transaction,
                new(Guid.NewGuid(), row.Quest.Id, dispatch, accounting, GuildBudgetEffectKind.LocalProvider, Guid.NewGuid(), null, 7m, 70), default);

            if (result.IsSuccess) await transaction.CommitAsync();

            return (Result)result;
        });

        Task<Result> child = Task.Run(async () =>
        {
            await start.Task;

            await using SqliteConnection connection = await f.OpenAsync();

            await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

            Result<Guid> result = await new GuildBudgetLedger().AllocateAccountWithinAsync(connection, transaction,
                GuildBudgetOwnerKind.Cohort, Guid.NewGuid(), row.AllocationAccountId,
                new(new(GuildBudgetMode.Finite, 7m), new(GuildBudgetMode.Finite, 70)), default);

            if (result.IsSuccess) await transaction.CommitAsync();

            return (Result)result;
        });

        start.SetResult();

        Result[] results = await Task.WhenAll(own, child);

        Assert.Single(results, result => result.IsSuccess);

        Assert.Equal(ErrorCodes.Guild.GrantExceeded, Assert.Single(results, result => result.IsFailure).Error.Code);

        await using SqliteConnection read = await f.OpenAsync();

        await using SqliteTransaction snapshot = read.BeginTransaction(deferred: false);

        Assert.Equal(3m, (await ledger.ReadWithinAsync(read, snapshot, row.AllocationAccountId, default)).Value.Remaining.Usd.Value);
    }

    [Fact]
    public async Task Abandoned_transaction_rolls_back_effect_child_and_revisions_before_restart_replays_evidence()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildBudgetLedger ledger = new();

        f.UseBudgetParticipant(ledger);

        f.Host.Grant = f.Host.Grant with { Budget = new(new(GuildBudgetMode.Finite, 10m), new(GuildBudgetMode.Finite, 100)) };

        GuildQuestStored row = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request with { Grant = f.Host.Grant }, default)).Value;

        GuildQuestDriver driver = await f.DriverAsync(row);

        Guid dispatch = Guid.NewGuid(), accounting = Guid.NewGuid();

        Assert.True((await f.Store.PrepareDispatchAsync(driver, dispatch, ReceiptDigest("rollback"), accounting, false, default)).IsSuccess);

        GuildBudgetEffectRequest request = new(Guid.NewGuid(), row.Quest.Id, dispatch, accounting,
            GuildBudgetEffectKind.LocalProvider, Guid.NewGuid(), null, 7m, 70);

        await using (SqliteConnection connection = await f.OpenAsync())
        {
            await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

            Assert.True((await ledger.PrepareEffectWithinAsync(connection, transaction, request, default)).IsSuccess);

            Assert.True((await ledger.AllocateAccountWithinAsync(connection, transaction,
                GuildBudgetOwnerKind.Cohort, Guid.NewGuid(), row.AllocationAccountId,
                new(new(GuildBudgetMode.Finite, 3m), new(GuildBudgetMode.Finite, 30)), default)).IsSuccess);

            // Disposing without commit models interruption before the durable boundary.
        }

        Assert.Equal(0L, await f.CountAsync("guild_budget_effects"));

        Assert.Equal(1L, await f.CountAsync("guild_budget_accounts"));

        GuildBudgetEvidence evidence = new(Guid.NewGuid(), ReceiptDigest("durable-replay"), 2m, 20, false, false, true);

        await using (SqliteConnection connection = await f.OpenAsync())
        {
            await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

            GuildBudgetSnapshot before = (await new GuildBudgetLedger().ReadWithinAsync(connection, transaction, row.AllocationAccountId, default)).Value;

            Assert.Equal(10m, before.Remaining.Usd.Value);

            Assert.Equal(1L, before.Revision);

            Assert.True((await ledger.PrepareEffectWithinAsync(connection, transaction, request, default)).IsSuccess);

            Assert.True((await ledger.ExposeAsync(connection, transaction, row.Quest.Id, row.AllocationAccountId, dispatch, accounting, default)).IsSuccess);

            Assert.True((await ledger.ApplyEvidenceWithinAsync(connection, transaction, request.EffectId, evidence, default)).IsSuccess);

            await transaction.CommitAsync();
        }

        await using (SqliteConnection connection = await f.OpenAsync())
        {
            await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

            GuildBudgetLedger restarted = new();

            Assert.True((await restarted.ApplyEvidenceWithinAsync(connection, transaction, request.EffectId, evidence, default)).IsSuccess);

            GuildBudgetSnapshot replay = (await restarted.ReadWithinAsync(connection, transaction, row.AllocationAccountId, default)).Value;

            Assert.Equal(2m, replay.KnownUsd);

            Assert.Equal(8m, replay.Remaining.Usd.Value);

            Assert.Equal(1L, replay.PricedCount);
        }

        Assert.Equal(1L, await f.CountAsync("guild_budget_evidence"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Prepared_effect_cannot_cross_boundary_after_account_closed_or_overrun(bool overrun)
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildBudgetLedger ledger = new();

        f.UseBudgetParticipant(ledger);

        f.Host.Grant = f.Host.Grant with { Budget = new(new(GuildBudgetMode.Finite, 10m), new(GuildBudgetMode.Finite, 100)) };

        GuildQuestStored row = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request with { Grant = f.Host.Grant }, default)).Value;

        GuildQuestDriver driver = await f.DriverAsync(row);

        Guid dispatch = Guid.NewGuid(), accounting = Guid.NewGuid();

        Assert.True((await f.Store.PrepareDispatchAsync(driver, dispatch, ReceiptDigest("recheck"), accounting, false, default)).IsSuccess);

        await using SqliteConnection connection = await f.OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        GuildBudgetEffectRequest first = new(Guid.NewGuid(), row.Quest.Id, dispatch, accounting,
            GuildBudgetEffectKind.LocalProvider, Guid.NewGuid(), null, 6m, 60);

        Assert.True((await ledger.PrepareEffectWithinAsync(connection, transaction, first, default)).IsSuccess);

        GuildBudgetEffectRequest pending = first;

        if (overrun)
        {
            Assert.True((await ledger.ExposeAsync(connection, transaction, row.Quest.Id, row.AllocationAccountId, dispatch, accounting, default)).IsSuccess);

            pending = first with { EffectId = Guid.NewGuid(), SourceId = Guid.NewGuid(), UsdBound = 4m, TokenBound = 40 };

            Assert.True((await ledger.PrepareEffectWithinAsync(connection, transaction, pending, default)).IsSuccess);

            Assert.True((await ledger.ApplyEvidenceWithinAsync(connection, transaction, first.EffectId,
                new(Guid.NewGuid(), ReceiptDigest("recheck-overrun"), 12m, 120, false, false, true), default)).IsSuccess);
        }
        else
        {
            Assert.True((await ledger.CloseAccountWithinAsync(connection, transaction, row.AllocationAccountId, Guid.NewGuid(), default)).IsSuccess);
        }

        Result refused = await ledger.ExposeAsync(connection, transaction, row.Quest.Id, row.AllocationAccountId, dispatch, accounting, default);

        Assert.True(refused.IsFailure, "A prepared hold must be rechecked at the physical boundary.");

        Assert.Equal(overrun ? ErrorCodes.Guild.GrantExceeded : ErrorCodes.Guild.Forbidden, refused.Error.Code);

        Assert.Equal(GuildBudgetEffectState.Prepared,
            (await ledger.ReadEffectWithinAsync(connection, transaction, pending.EffectId, default)).Value.State);
    }

    [Fact]
    public async Task Proven_unsent_dispatch_rejects_new_physical_preparation()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildBudgetLedger ledger = new();

        f.UseBudgetParticipant(ledger);

        GuildQuestStored row = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildQuestDriver driver = await f.DriverAsync(row);

        Guid dispatch = Guid.NewGuid(), accounting = Guid.NewGuid();

        Assert.True((await f.Store.PrepareDispatchAsync(driver, dispatch, ReceiptDigest("never-started"), accounting, false, default)).IsSuccess);

        Assert.True((await f.Store.RecordDispatchEvidenceAsync(driver,
            new(dispatch, 1, GuildDispatchEvidence.ResolvedNoDispatch, NoDispatchProven: true), default)).IsSuccess);

        await using SqliteConnection connection = await f.OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        GuildBudgetEffectRequest request = new(Guid.NewGuid(), row.Quest.Id, dispatch, accounting,
            GuildBudgetEffectKind.LocalProvider, Guid.NewGuid(), null, null, null);

        Assert.True((await ledger.PrepareEffectWithinAsync(connection, transaction, request, default)).IsFailure);
    }

    [Fact]
    public async Task Physical_partial_evidence_replaces_known_portion_of_hold_and_replay_never_spends_twice()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildBudgetLedger ledger = new();

        f.UseBudgetParticipant(ledger);

        f.Host.Grant = f.Host.Grant with { Budget = new(new(GuildBudgetMode.Finite, 10m), new(GuildBudgetMode.Finite, 100)) };

        Result<GuildQuestStored> issued = await f.Store.IssueAsync(f.Caller, f.Identity, f.Request with { Grant = f.Host.Grant }, default);

        Assert.True(issued.IsSuccess, issued.Error.Message);

        GuildQuestStored row = issued.Value;

        GuildQuestDriver driver = await f.DriverAsync(row);

        Guid dispatch = Guid.NewGuid(), accounting = Guid.NewGuid();

        Assert.True((await f.Store.PrepareDispatchAsync(driver, dispatch, ReceiptDigest("budget-physical"), accounting, false, default)).IsSuccess);

        GuildBudgetEffectRequest request = new(Guid.NewGuid(), row.Quest.Id, dispatch, accounting,
            GuildBudgetEffectKind.LocalProvider, Guid.NewGuid(), null, 8m, 80);

        await using (SqliteConnection connection = await f.OpenAsync())
        {
            await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

            Assert.True((await ledger.PrepareEffectWithinAsync(connection, transaction, request, default)).IsSuccess);

            Assert.Equal(2m, (await ledger.ReadWithinAsync(connection, transaction, row.AllocationAccountId, default)).Value.Remaining.Usd.Value);

            Result<Guid> sibling = await ledger.AllocateAccountWithinAsync(connection, transaction,
                GuildBudgetOwnerKind.Cohort, Guid.NewGuid(), row.AllocationAccountId,
                new(new(GuildBudgetMode.Finite, 3m), new(GuildBudgetMode.Finite, 30)), default);

            Assert.Equal(ErrorCodes.Guild.GrantExceeded, sibling.Error.Code);

            Assert.Equal(ErrorCodes.Guild.PriceUnavailable, (await ledger.PrepareEffectWithinAsync(connection, transaction,
                request with { EffectId = Guid.NewGuid(), SourceId = Guid.NewGuid(), UsdBound = null }, default)).Error.Code);

            Assert.Equal(ErrorCodes.Guild.BudgetCapabilityUnavailable, (await ledger.PrepareEffectWithinAsync(connection, transaction,
                request with { EffectId = Guid.NewGuid(), SourceId = Guid.NewGuid(), TokenBound = null }, default)).Error.Code);

            Assert.Equal(ErrorCodes.Guild.BudgetCapabilityUnavailable, (await ledger.PrepareEffectWithinAsync(connection, transaction,
                request with { EffectId = Guid.NewGuid(), SourceId = Guid.NewGuid(), Kind = GuildBudgetEffectKind.External }, default)).Error.Code);

            await transaction.CommitAsync();
        }

        Assert.True((await f.Store.MarkPossiblyDispatchedAsync(driver, dispatch, 1, default)).IsSuccess);

        await using (SqliteConnection connection = await f.OpenAsync())
        {
            await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

            GuildBudgetEvidence partial = new(Guid.NewGuid(), ReceiptDigest("partial"), 3m, 30, true, true, false);

            Result<GuildBudgetEffect> applied = await ledger.ApplyEvidenceWithinAsync(connection, transaction, request.EffectId, partial, default);

            Assert.True(applied.IsSuccess, applied.Error.Message);

            GuildBudgetSnapshot snapshot = (await ledger.ReadWithinAsync(connection, transaction, row.AllocationAccountId, default)).Value;

            Assert.Equal(3m, snapshot.KnownUsd);

            Assert.Equal(5m, snapshot.UnresolvedHolds.Usd.Value);

            Assert.Equal(2m, snapshot.Remaining.Usd.Value);

            Assert.True(snapshot.UnknownUsd);

            Assert.Equal(applied.Value.Revision, (await ledger.ApplyEvidenceWithinAsync(connection, transaction, request.EffectId, partial, default)).Value.Revision);

            Assert.Equal(ErrorCodes.Guild.IdempotencyConflict,
                (await ledger.ApplyEvidenceWithinAsync(connection, transaction, request.EffectId, partial with { KnownUsd = 4m }, default)).Error.Code);

            GuildBudgetEvidence completed = new(Guid.NewGuid(), ReceiptDigest("complete"), 4m, 40, false, false, true);

            Assert.True((await ledger.ApplyEvidenceWithinAsync(connection, transaction, request.EffectId, completed, default)).IsSuccess);

            snapshot = (await ledger.ReadWithinAsync(connection, transaction, row.AllocationAccountId, default)).Value;

            Assert.Equal(4m, snapshot.KnownUsd);

            Assert.Equal(0m, snapshot.UnresolvedHolds.Usd.Value);

            Assert.Equal(6m, snapshot.Remaining.Usd.Value);

            Assert.False(snapshot.UnknownUsd);

            Assert.Equal(1L, snapshot.PricedCount);

            GuildBudgetEvidence overrun = new(Guid.NewGuid(), ReceiptDigest("overrun-correction"), 12m, 120, false, false, true);

            Assert.True((await ledger.ApplyEvidenceWithinAsync(connection, transaction, request.EffectId, overrun, default)).IsSuccess);

            snapshot = (await ledger.ReadWithinAsync(connection, transaction, row.AllocationAccountId, default)).Value;

            Assert.Equal(12m, snapshot.KnownUsd);

            Assert.Equal(0m, snapshot.Remaining.Usd.Value);

            Assert.Equal(ErrorCodes.Guild.GrantExceeded, (await ledger.PrepareEffectWithinAsync(connection, transaction,
                request with { EffectId = Guid.NewGuid(), SourceId = Guid.NewGuid(), UsdBound = 0m, TokenBound = 0 }, default)).Error.Code);

            Assert.True((await ledger.ApplyEvidenceWithinAsync(connection, transaction, request.EffectId,
                new(Guid.NewGuid(), ReceiptDigest("maximum-overrun"), decimal.MaxValue, long.MaxValue, false, false, true), default)).IsSuccess);

            snapshot = (await ledger.ReadWithinAsync(connection, transaction, row.AllocationAccountId, default)).Value;

            Assert.Equal(decimal.MaxValue, snapshot.KnownUsd);

            Assert.Equal(0m, snapshot.Remaining.Usd.Value);

            await transaction.CommitAsync();
        }

        Assert.Equal(4L, await f.CountAsync("guild_budget_evidence"));

        Assert.Equal(0L, await f.CountAsync("BillableOperations"));
    }
}
