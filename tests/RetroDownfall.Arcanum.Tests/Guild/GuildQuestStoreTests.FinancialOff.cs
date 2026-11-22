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
    [Fact]
    public async Task Received_missing_usage_snapshot_survives_correction_pruning_and_restart()
    {
        await using Fixture f = await Fixture.CreateAsync();

        await using SqliteConnection connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        BudgetReservationService daily = new(db);

        GuildBudgetLedger ledger = new(daily);

        f.UseBudgetParticipant(ledger);

        GuildQuestStored quest = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildQuestDriver driver = await f.DriverAsync(quest);

        GuildQuestDispatchRecord dispatch = (await f.Store.PrepareDispatchAsync(driver, Guid.NewGuid(),
            ReceiptDigest("received-missing"), quest.Quest.RunId, false, default)).Value;

        GuildLocalInferenceExecution execution = new(f.Store, driver, dispatch);

        GuildBudgetEffectRequest effect = new(Guid.NewGuid(), quest.Quest.Id, dispatch.DispatchId, quest.Quest.RunId,
            GuildBudgetEffectKind.LocalProvider, Guid.NewGuid(), null, null, null);

        DateTimeOffset started = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

        BillableOperationRecord bill = new(quest.Quest.RunId, BillableOperationType.Chat, "provider", "model", "guild",
            started, started.AddSeconds(2), 2, 3, 0, 2, "{}", 0m, BillableOperationStatus.Failed, "request-id");

        Result<string> received = await f.Store.ExecuteAndSettleProviderResponseAsync(execution, daily, effect,
            _ => Task.FromResult(Result<string>.Success("answer")), _ => bill, default);

        Assert.True(received.IsSuccess, received.Error.Message);

        Result<GuildBudgetEffect> initial = await f.Store.SettleProviderEffectAsync(execution, daily, effect, bill, default);

        Assert.True(initial.IsSuccess, initial.Error.Message);

        Assert.Equal(GuildBudgetEffectState.Complete, initial.Value.State);

        Assert.True(initial.Value.UnknownUsd);

        Assert.True(initial.Value.UnknownTokens);

        Assert.Equal(5, initial.Value.KnownTokens);

        GuildSpendCorrectionRequest correction = new(Guid.NewGuid(), initial.Value.Revision, GuildSpendOrigin.OperatorAttested,
            new(8, 4, 2, 1, .1m), new string('A', 64));

        var corrected = await f.Store.CorrectOperatorSpendAsync(f.Caller, quest.Quest.Id, effect.EffectId, correction, default);

        Assert.True(corrected.IsSuccess, corrected.Error.Message);

        Assert.True(corrected.Value.Receipt.Facts.Complete);

        Assert.True(corrected.Value.Receipt.Facts.UnknownUsd);

        Assert.True(corrected.Value.Receipt.Facts.UnknownTokens);

        Assert.Equal(2, corrected.Value.Receipt.Facts.LocalTokens!.CachedTokens);

        await using (SqliteCommand status = connection.CreateCommand())
        {
            status.CommandText = "SELECT Status FROM BillableOperations";

            Assert.Equal((long)BillableOperationStatus.Failed, await status.ExecuteScalarAsync());
        }

        await f.ExecuteAsync("DELETE FROM guild_events");

        await f.ExecuteAsync("DELETE FROM BillableOperations");

        Result<GuildBudgetEffect> replay = await f.Store.SettleProviderEffectAsync(execution, daily, effect, bill, default);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.Equal(initial.Value, replay.Value);

        f.Restart();

        await using SqliteConnection reopened = await f.OpenAsync();

        await using SqliteTransaction transaction = reopened.BeginTransaction();

        Result<GuildStoredInitialSpend?> retained = await GuildSpendSourceStore.LoadInitialWithinAsync(reopened, transaction, effect.EffectId, default);

        Assert.True(retained.IsSuccess, retained.Error.Message);

        Assert.Equal(initial.Value, retained.Value!.Outcome);

        Assert.Equal(BillableOperationStatus.Failed, retained.Value.Snapshot.Status);

        Assert.Equal(2, retained.Value.Snapshot.InputTokens);

        Assert.Equal(3, retained.Value.Snapshot.OutputTokens);

        Assert.Equal(2, retained.Value.Snapshot.CachedTokens);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lost_provider_response_never_invokes_projection_or_mints_completion(bool canceled)
    {
        await using Fixture f = await Fixture.CreateAsync();

        await using SqliteConnection connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        BudgetReservationService daily = new(db);

        f.UseBudgetParticipant(new GuildBudgetLedger(daily));

        GuildQuestStored quest = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildQuestDriver driver = await f.DriverAsync(quest);

        GuildQuestDispatchRecord dispatch = (await f.Store.PrepareDispatchAsync(driver, Guid.NewGuid(),
            ReceiptDigest("lost-received"), quest.Quest.RunId, false, default)).Value;

        GuildLocalInferenceExecution execution = new(f.Store, driver, dispatch);

        GuildBudgetEffectRequest effect = new(Guid.NewGuid(), quest.Quest.Id, dispatch.DispatchId, quest.Quest.RunId,
            GuildBudgetEffectKind.LocalProvider, Guid.NewGuid(), null, null, null);

        bool projected = false;

        Task<Result<string>> Invoke() => f.Store.ExecuteAndSettleProviderResponseAsync<string>(execution, daily, effect,
            _ => canceled ? throw new OperationCanceledException("canceled after handoff") : throw new IOException("lost response"), _ =>
            {
                projected = true;

                return new Error(ErrorCodes.Guild.ReconciliationRequired, "unexpected");
            }, default);

        if (canceled) await Assert.ThrowsAnyAsync<OperationCanceledException>(Invoke);
        else await Assert.ThrowsAsync<IOException>(Invoke);

        Assert.False(projected);

        Assert.Equal(0L, await f.CountAsync("BillableOperations"));

        await using SqliteCommand state = connection.CreateCommand();

        state.CommandText = "SELECT StateCode FROM guild_budget_effects";

        Assert.Equal(1L, await state.ExecuteScalarAsync());

        DateTimeOffset now = DateTimeOffset.UtcNow;

        BillableOperationRecord bill = new(quest.Quest.RunId, BillableOperationType.Chat, "provider", "model", "guild",
            now, now, 0, 0, 0, 0, "{}", 0m, BillableOperationStatus.Failed, null);

        Assert.True((await f.Store.SettleProviderEffectAsync(execution, daily, effect with { }, bill, default)).IsFailure);

        Assert.True((await f.Store.SettleProviderEffectAsync(execution, daily, effect with { SourceId = Guid.NewGuid() }, bill, default)).IsFailure);

        Assert.True((await f.Store.SettleProviderEffectAsync(new(f.Store, driver, dispatch), daily, effect, bill, default)).IsFailure);

        Result<GuildBudgetEffect> billOnly = await f.Store.SettleProviderEffectAsync(execution, daily, effect, bill, default);

        Assert.True(billOnly.IsSuccess, billOnly.Error.Message);

        Assert.Equal(GuildBudgetEffectState.PossiblyDispatched, billOnly.Value.State);

        Assert.True(billOnly.Value.UnknownTokens);

        int sends = 0;

        Result<string> retry = await f.Store.ExecuteAndSettleProviderResponseAsync(execution, daily,
            effect with { EffectId = Guid.NewGuid(), SourceId = Guid.NewGuid() },
            _ => { sends++; return Task.FromResult(Result<string>.Success("unexpected")); }, _ => bill, default);

        Assert.True(retry.IsFailure);

        Assert.Equal(0, sends);

        await f.ExecuteAsync("DELETE FROM guild_events");

        Result<GuildBudgetEffect> replay = await f.Store.SettleProviderEffectAsync(execution, daily, effect, bill, default);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.Equal(billOnly.Value, replay.Value);

        f.Restart();

        await using SqliteConnection reopened = await f.OpenAsync();

        await using SqliteTransaction transaction = reopened.BeginTransaction();

        Result<GuildStoredInitialSpend?> retained = await GuildSpendSourceStore.LoadInitialWithinAsync(reopened, transaction, effect.EffectId, default);

        Assert.True(retained.IsSuccess, retained.Error.Message);

        Assert.Equal(GuildBudgetEffectState.PossiblyDispatched, retained.Value!.Outcome.State);

        Assert.Equal(BillableOperationStatus.Failed, retained.Value.Snapshot.Status);
    }

    [Theory]
    [InlineData("INSERT ON BillableOperations")]
    [InlineData("INSERT ON guild_budget_evidence")]
    [InlineData("UPDATE ON guild_budget_effects")]
    [InlineData("UPDATE ON guild_quests")]
    [InlineData("INSERT ON guild_events")]
    [InlineData("INSERT ON guild_spend_sources")]
    [InlineData("INSERT ON guild_spend_receipts")]
    public async Task Received_response_settlement_failure_rolls_back_and_cannot_authorize_resend(string target)
    {
        await using Fixture f = await Fixture.CreateAsync();

        await using SqliteConnection connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        BudgetReservationService daily = new(db);

        f.UseBudgetParticipant(new GuildBudgetLedger(daily));

        GuildQuestStored quest = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildQuestDriver driver = await f.DriverAsync(quest);

        GuildQuestDispatchRecord dispatch = (await f.Store.PrepareDispatchAsync(driver, Guid.NewGuid(),
            ReceiptDigest("received-fault"), quest.Quest.RunId, false, default)).Value;

        GuildLocalInferenceExecution execution = new(f.Store, driver, dispatch);

        GuildBudgetEffectRequest effect = new(Guid.NewGuid(), quest.Quest.Id, dispatch.DispatchId, quest.Quest.RunId,
            GuildBudgetEffectKind.LocalProvider, Guid.NewGuid(), null, null, null);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        BillableOperationRecord bill = new(quest.Quest.RunId, BillableOperationType.Chat, "provider", "model", "guild",
            now, now, 7, 0, 0, 0, "{}", 0m, BillableOperationStatus.Failed, null);

        int sends = 0;

        Result<string> failed = await f.Store.ExecuteAndSettleProviderResponseAsync(execution, daily, effect,
            async _ =>
            {
                sends++;

                await f.ExecuteAsync($"CREATE TRIGGER fixture_abort_received BEFORE {target} BEGIN SELECT RAISE(ABORT,'injected'); END");

                return Result<string>.Success("answer");
            }, _ => bill, default);

        Assert.True(failed.IsFailure);

        Assert.Equal(1, sends);

        Assert.Equal(0L, await f.CountAsync("BillableOperations"));

        Assert.Equal(0L, await f.CountAsync("guild_budget_evidence"));

        await f.ExecuteAsync("DROP TRIGGER fixture_abort_received");

        Result<string> retried = await f.Store.ExecuteAndSettleProviderResponseAsync(execution, daily,
            effect with { EffectId = Guid.NewGuid(), SourceId = Guid.NewGuid() },
            _ => { sends++; return Task.FromResult(Result<string>.Success("unexpected")); }, _ => bill, default);

        Assert.True(retried.IsFailure);

        Assert.Equal(1, sends);

        await using SqliteCommand state = connection.CreateCommand();

        state.CommandText = "SELECT StateCode FROM guild_budget_effects";

        Assert.Equal(1L, await state.ExecuteScalarAsync());
    }

}
