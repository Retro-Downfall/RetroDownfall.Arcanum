using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Provider_effect_rechecks_wait_or_canceled_ancestor_after_first_child_call_settles(bool waiting)
    {
        await using Fixture f = await Fixture.CreateAsync();

        await using SqliteConnection connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        BudgetReservationService daily = new(db, new TestOptionsMonitor<ArcanumSettings>(new()));

        f.UseBudgetParticipant(new GuildBudgetLedger(daily));

        GuildQuestStored parent = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildQuestStored child = await f.ChildAsync(parent, await f.DriverAsync(parent));

        GuildQuestDriver driver = await f.DriverAsync(child);

        GuildQuestDispatchRecord dispatch = (await f.Store.PrepareDispatchAsync(driver, Guid.NewGuid(),
            ReceiptDigest("ancestor"), child.Quest.RunId, false, default)).Value;

        GuildLocalInferenceExecution execution = new(f.Store, driver, dispatch);

        GuildBudgetEffectRequest effect = new(Guid.NewGuid(), child.Quest.Id, dispatch.DispatchId, child.Quest.RunId,
            GuildBudgetEffectKind.LocalProvider, Guid.NewGuid(), null, null, null);

        int sends = 0;

        Task<Result<string>> Send(CancellationToken _) { sends++; return Task.FromResult(Result<string>.Success("answer")); }

        Assert.True((await f.Store.ExecuteProviderEffectAsync(execution, daily, effect, Send, default)).IsSuccess);

        BillableOperationRecord bill = new(child.Quest.RunId, BillableOperationType.Chat, "provider", "model", "guild",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 7, 3, 0, 0, "{}", 0m, BillableOperationStatus.Completed, null);

        Assert.True((await f.Store.SettleProviderEffectAsync(execution, daily, effect, bill, default)).IsSuccess);

        if (waiting)
        {
            var confirmed = await f.Store.RecordDispatchEvidenceAsync(driver,
                new(dispatch.DispatchId, dispatch.Revision + 1, GuildDispatchEvidence.Confirmed, Guid.NewGuid(), null, null), default);

            Assert.True(confirmed.IsSuccess, confirmed.Error.Message);

            var paused = await f.Store.WaitForInputAsync(driver, Guid.NewGuid(), Guid.NewGuid(), default);

            Assert.True(paused.IsSuccess, paused.Error.Message);

            LongRunningOperationStore operations = new(db, f.Connections);

            var operation = await operations.GetAsync(child.Quest.OperationId);

            Assert.True(execution.TryRefreshDriver(driver with { OperationRevision = operation!.Revision }));
        }
        else
        {
            GuildQuestStored currentParent = (await f.Store.GetAsync(f.Caller, parent.Quest.Id, default)).Value;

            GuildCancelQuestRequest cancel = new(Guid.NewGuid(), currentParent.Quest.Revision, GuildCancellationReason.OperatorRequested);

            var cancelled = await f.Store.RequestCancellationAsync(f.Caller,
                new(cancel.IdempotencyKey, GuildQuestIntent.Compute(parent.Quest.Id, cancel).Value), parent.Quest.Id, cancel, default);

            Assert.True(cancelled.IsSuccess, cancelled.Error.Message);
        }

        Result<string> next = await f.Store.ExecuteProviderEffectAsync(execution, daily,
            effect with { EffectId = Guid.NewGuid(), SourceId = Guid.NewGuid() }, Send, default);

        Assert.True(next.IsFailure);

        Assert.Equal(1, sends);

        Assert.Equal(1L, await f.CountAsync("guild_budget_effects"));
    }

    [Fact]
    public async Task Provider_effect_timeout_keeps_real_daily_hold_past_expiry_and_refuses_another_identity()
    {
        await using Fixture f = await Fixture.CreateAsync();

        await using SqliteConnection connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        ArcanumSettings settings = new() { Cost = new() { Budget = new() { Enabled = true, DailyLimitUsd = 10m } } };

        BudgetReservationService daily = new(db, new TestOptionsMonitor<ArcanumSettings>(settings));

        f.UseBudgetParticipant(new GuildBudgetLedger(daily));

        GuildQuestStored quest = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildQuestDriver driver = await f.DriverAsync(quest);

        GuildQuestDispatchRecord dispatch = (await f.Store.PrepareDispatchAsync(driver, Guid.NewGuid(),
            ReceiptDigest("timeout"), quest.Quest.RunId, false, default)).Value;

        GuildLocalInferenceExecution execution = new(f.Store, driver, dispatch);

        GuildBudgetEffectRequest effect = new(Guid.NewGuid(), quest.Quest.Id, dispatch.DispatchId, quest.Quest.RunId,
            GuildBudgetEffectKind.LocalProvider, Guid.NewGuid(), null, 1m, 100);

        int sends = 0;

        await Assert.ThrowsAsync<IOException>(() => f.Store.ExecuteProviderEffectAsync<string>(execution, daily, effect,
            async token =>
            {
                sends++;

                Assert.Equal(0, await daily.SweepExpiredAsync(DateTimeOffset.UtcNow.AddDays(10), token));

                throw new IOException("lost response");
            }, default));

        Assert.Equal(0, await daily.SweepExpiredAsync(DateTimeOffset.UtcNow.AddDays(20)));

        Result<string> retried = await f.Store.ExecuteProviderEffectAsync(execution, daily,
            effect with { EffectId = Guid.NewGuid(), SourceId = Guid.NewGuid() },
            _ => { sends++; return Task.FromResult(Result<string>.Success("unexpected")); }, default);

        Assert.Equal(ErrorCodes.Guild.ReconciliationRequired, retried.Error.Code);

        Assert.Equal(1, sends);

        Assert.Equal(1L, await f.CountAsync("BudgetReservations"));

        Assert.Equal(0L, await f.CountAsync("BillableOperations"));

        await using SqliteCommand state = connection.CreateCommand();

        state.CommandText = "SELECT StateCode FROM guild_budget_effects";

        Assert.Equal(1L, await state.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Provider_effect_commits_real_preparation_dispatch_and_exactly_one_bill()
    {
        await using Fixture f = await Fixture.CreateAsync();

        await using SqliteConnection connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        BudgetReservationService daily = new(db, new TestOptionsMonitor<ArcanumSettings>(new()));

        f.UseBudgetParticipant(new GuildBudgetLedger(daily));

        GuildQuestStored quest = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildQuestDriver driver = await f.DriverAsync(quest);

        GuildQuestDispatchRecord dispatch = (await f.Store.PrepareDispatchAsync(driver, Guid.NewGuid(),
            ReceiptDigest("physical"), quest.Quest.RunId, false, default)).Value;

        GuildLocalInferenceExecution execution = new(f.Store, driver, dispatch);

        GuildBudgetEffectRequest effect = new(Guid.NewGuid(), quest.Quest.Id, dispatch.DispatchId, quest.Quest.RunId,
            GuildBudgetEffectKind.LocalProvider, Guid.NewGuid(), null, null, null);

        int sends = 0;

        async Task<Result<string>> Send(CancellationToken ct)
        {
            sends++;

            Assert.Equal(1L, await f.CountAsync("guild_budget_effects"));

            await using SqliteCommand read = connection.CreateCommand();

            read.CommandText = "SELECT EvidenceCode FROM guild_dispatches";

            Assert.Equal(2L, await read.ExecuteScalarAsync(ct));

            return "answer";
        }

        Result<string> called = await f.Store.ExecuteProviderEffectAsync(execution, daily, effect, Send, default);

        Assert.True(called.IsSuccess, called.Error.Message);

        Assert.Equal("answer", called.Value);

        Result<string> replay = await f.Store.ExecuteProviderEffectAsync(execution, daily, effect, Send, default);

        Assert.True(replay.IsFailure);

        Assert.Equal(1, sends);

        BillableOperationRecord bill = new(quest.Quest.RunId, BillableOperationType.Chat, "provider", "model", "guild",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 7, 3, 0, 0, "{}", 0m, BillableOperationStatus.Completed, null);

        Result<GuildBudgetEffect> settled = await f.Store.SettleProviderEffectAsync(execution, daily, effect, bill, default);

        Assert.True(settled.IsSuccess, settled.Error.Message);

        Assert.Equal(10, settled.Value.KnownTokens);

        Assert.True(settled.Value.UnknownUsd);

        Assert.False(settled.Value.UnknownTokens);

        Assert.True((await f.Store.SettleProviderEffectAsync(execution, daily, effect, bill, default)).IsSuccess);

        Assert.Equal(1L, await f.CountAsync("BillableOperations"));

        Assert.Equal(1L, await f.CountAsync("guild_budget_evidence"));

        await using SqliteCommand outbox = connection.CreateCommand();

        outbox.CommandText = "SELECT count(*) FROM guild_portal_outbox WHERE SourceKindCode=$kind";

        outbox.Parameters.AddWithValue("$kind", (int)GuildReceiptSourceKind.Settlement);

        Assert.Equal(0L, await outbox.ExecuteScalarAsync());

        Assert.True((await f.Store.SettleProviderEffectAsync(new(f.Store, driver, dispatch), daily, effect, bill, default)).IsFailure,
            "Copied ids must not authorize settlement of a physical call they did not own.");
    }

    [Fact]
    public async Task Provider_effect_refuses_unqualified_finite_bounds_and_rolls_back_before_send()
    {
        await using Fixture f = await Fixture.CreateAsync();

        await using SqliteConnection connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        BudgetReservationService daily = new(db, new TestOptionsMonitor<ArcanumSettings>(new()));

        f.UseBudgetParticipant(new GuildBudgetLedger(daily));

        f.Host.Grant = f.Host.Grant with { Budget = new(new(GuildBudgetMode.Finite, 1m), new(GuildBudgetMode.Finite, 100)) };

        GuildQuestStored quest = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request with { Grant = f.Host.Grant }, default)).Value;

        GuildQuestDriver driver = await f.DriverAsync(quest);

        GuildQuestDispatchRecord dispatch = (await f.Store.PrepareDispatchAsync(driver, Guid.NewGuid(),
            ReceiptDigest("finite"), quest.Quest.RunId, false, default)).Value;

        GuildBudgetEffectRequest effect = new(Guid.NewGuid(), quest.Quest.Id, dispatch.DispatchId, quest.Quest.RunId,
            GuildBudgetEffectKind.LocalProvider, Guid.NewGuid(), null, null, null);

        int sends = 0;

        Result<string> result = await f.Store.ExecuteProviderEffectAsync(new(f.Store, driver, dispatch), daily, effect,
            _ => { sends++; return Task.FromResult(Result<string>.Success("unexpected")); }, default);

        Assert.Equal(ErrorCodes.Guild.PriceUnavailable, result.Error.Code);

        Assert.Equal(0, sends);

        Assert.Equal(0L, await f.CountAsync("guild_budget_effects"));

        Assert.Equal(0L, await f.CountAsync("InferenceRuns"));
    }
}
