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
    public async Task Local_wait_checkpoint_and_pending_resume_preserve_completed_unpriced_accounting_without_repeating_work()
    {
        await using Fixture f = await Fixture.CreateAsync();

        await using SqliteConnection connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        BudgetReservationService daily = new(db, new TestOptionsMonitor<ArcanumSettings>(new()));

        f.UseBudgetParticipant(new GuildBudgetLedger(daily));

        GuildQuestStored row = await ReceiptRootAsync(f);

        GuildLocalExecutionBinding binding = await CreateLocalAsync(f, row, LocalInput(row));

        f.UseLocalAdmission(new LocalAdmission());

        GuildQuestDriver driver = await f.DriverAsync(row);

        GuildQuestDispatchRecord dispatch = (await f.Store.PrepareDispatchAsync(driver, row.Quest.Id,
            binding.Input.Digest, row.Quest.RunId, false, default)).Value;

        GuildLocalInferenceExecution execution = new(f.Store, driver, dispatch);

        GuildBudgetEffectRequest effect = new(Guid.NewGuid(), row.Quest.Id, dispatch.DispatchId, row.Quest.RunId,
            GuildBudgetEffectKind.LocalProvider, Guid.NewGuid(), null, null, null);

        Assert.True((await f.Store.ExecuteProviderEffectAsync(execution, daily, effect,
            _ => Task.FromResult(Result<string>.Success("completed public result")), default)).IsSuccess);

        BillableOperationRecord bill = new(row.Quest.RunId, BillableOperationType.Chat, "provider", "model", "guild",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 7, 3, 0, 0, "{}", 0m, BillableOperationStatus.Completed, null);

        var settled = await f.Store.SettleProviderEffectAsync(execution, daily, effect, bill, default);

        Assert.True(settled.IsSuccess, settled.Error.Message);

        Assert.True(settled.Value.UnknownUsd);

        var confirmed = await f.Store.RecordDispatchEvidenceAsync(driver,
            new(dispatch.DispatchId, dispatch.Revision + 1, GuildDispatchEvidence.Confirmed, binding.ApprenticeId), default);

        Assert.True(confirmed.IsSuccess, confirmed.Error.Message);

        Guid continuation = Guid.NewGuid();

        var waiting = await f.Store.WaitForInputAsync(driver, Guid.NewGuid(), continuation,
            new(dispatch.DispatchId, WaitSnapshot(binding)), default);

        Assert.True(waiting.IsSuccess, waiting.Error.Message);

        var accepted = await f.Store.AcceptContinuationAsync(f.Caller, row.Quest.Id,
            new(Guid.NewGuid(), waiting.Value.Quest.Revision, continuation, "text/plain", "Continue with the known result"), default);

        Assert.True(accepted.IsSuccess, accepted.Error.Message);

        await f.ExecuteAsync($"UPDATE LongRunningOperations SET LeaseOwner=NULL,LeaseExpiresAt=NULL WHERE Id='{row.Quest.OperationId:N}'");

        var pending = await f.Store.ReadLocalPendingAsync(f.Caller, row.Quest.Id, default);

        Assert.True(pending.IsSuccess, pending.Error.Message);

        Assert.False(pending.Value.RequiresAttention);

        Assert.Equal(continuation, pending.Value.Request!.DispatchId);

        Assert.Equal(1L, await f.CountAsync("guild_budget_effects"));

        Assert.Equal(1L, await f.CountAsync("BillableOperations"));

        await using SqliteCommand unknown = connection.CreateCommand();

        unknown.CommandText = "SELECT UnknownUsd FROM guild_budget_effects";

        Assert.Equal(1L, await unknown.ExecuteScalarAsync());
    }
}
