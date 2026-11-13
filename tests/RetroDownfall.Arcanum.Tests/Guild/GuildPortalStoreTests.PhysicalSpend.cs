using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildPortalStoreTests
{
    [Fact]
    public async Task Initial_provider_settlement_advances_the_snapshot_with_one_replayable_spend_invalidation()
    {
        await using Fixture f = await Fixture.CreateAsync();

        await using var connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        BudgetReservationService daily = new(db, new TestOptionsMonitor<ArcanumSettings>(new()));

        f.Store = new(new(f.Connections, f.Gate, TimeProvider.System), f.Authority, f.Host,
            new GuildBudgetLedger(daily), TimeProvider.System, readiness: new TestGuildLocalHostReadiness());

        f.Portal = new(f.Store, f.Authority, f.Host, TimeProvider.System);

        var issued = await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default);

        Assert.True(issued.IsSuccess, issued.Error.Message);

        GuildQuestStored quest = issued.Value;

        GuildQuestDriver driver = await f.DriverAsync(quest);

        var prepared = await f.Store.PrepareDispatchAsync(driver, Guid.NewGuid(), new string('A', 64), quest.Quest.RunId, false, default);

        Assert.True(prepared.IsSuccess, prepared.Error.Message);

        GuildLocalInferenceExecution execution = new(f.Store, driver, prepared.Value);

        GuildBudgetEffectRequest effect = new(Guid.NewGuid(), quest.Quest.Id, prepared.Value.DispatchId, quest.Quest.RunId,
            GuildBudgetEffectKind.LocalProvider, Guid.NewGuid(), null, null, null);

        int sends = 0;

        Task<Result<string>> Send(CancellationToken _) { sends++; return Task.FromResult(Result<string>.Success("answer")); }

        var called = await f.Store.ExecuteProviderEffectAsync(execution, daily, effect, Send, default);

        Assert.True(called.IsSuccess, called.Error.Message);

        var snapshot = await f.Portal.CaptureTreeSnapshotAsync(f.Caller, quest.Quest.Id, default);

        Assert.True(snapshot.IsSuccess, snapshot.Error.Message);

        var unscopedCursor = await f.Portal.CaptureReplayCursorAsync(f.Caller, quest.Quest.Id, default);

        Assert.True(unscopedCursor.IsSuccess, unscopedCursor.Error.Message);

        BillableOperationRecord bill = new(quest.Quest.RunId, BillableOperationType.Chat, "provider", "model", "guild",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 7, 3, 0, 0, "{}", 0m, BillableOperationStatus.Completed, null);

        long beforeSettlement = await f.CountAsync("guild_events");

        await f.ExecuteAsync("CREATE TRIGGER fixture_abort_initial_receipt BEFORE INSERT ON guild_spend_receipts BEGIN SELECT RAISE(ABORT,'injected'); END");

        var interrupted = await f.Store.SettleProviderEffectAsync(execution, daily, effect, bill, default);

        Assert.True(interrupted.IsFailure);

        Assert.Equal(0L, await f.CountAsync("BillableOperations"));

        Assert.Equal(0L, await f.CountAsync("guild_budget_evidence"));

        Assert.Equal(beforeSettlement, await f.CountAsync("guild_events"));

        await f.ExecuteAsync("DROP TRIGGER fixture_abort_initial_receipt");

        var settled = await f.Store.SettleProviderEffectAsync(execution, daily, effect, bill, default);

        Assert.True(settled.IsSuccess, settled.Error.Message);

        Assert.Equal(10, settled.Value.KnownTokens);

        Assert.True((await f.Store.SettleProviderEffectAsync(execution, daily, effect, bill, default)).IsSuccess);

        Assert.Equal(1, sends);

        var replay = await f.Portal.ReadScopedReplayAsync(f.Caller, quest.Quest.Id, quest.Quest.Id, null, snapshot.Value.ResumeCursor, default);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        var spend = Assert.Single(replay.Value.Page.Items.Where(item => item.EventKind == GuildEventKind.SpendChanged));

        GuildSpendSourceDto source = Assert.IsType<GuildSpendSourceDto>(spend.SpendSource);

        Assert.Equal(effect.EffectId, source.EffectId);

        Assert.Equal(effect.SourceId, source.ReceiptId);

        Assert.Equal(settled.Value.Revision, source.EvidenceRevision);

        Assert.Equal(GuildSpendOrigin.LocalResponse, source.Origin);

        var receipt = await f.Store.ReadOperatorSpendReceiptAsync(f.Caller, quest.Quest.Id, effect.EffectId, source.ReceiptId, default);

        Assert.True(receipt.IsSuccess, receipt.Error.Message);

        Assert.Equal(spend.EventSequence, receipt.Value.EventSequence);

        Assert.Equal(new GuildSpendLocalTokensDto(7, 3, 0, 0), receipt.Value.Facts.LocalTokens);

        var history = await f.Store.ListOperatorSpendReceiptsAsync(f.Caller, quest.Quest.Id, effect.EffectId, null, 10, default);

        Assert.True(history.IsSuccess, history.Error.Message);

        Assert.Equal(source.ReceiptId, Assert.Single(history.Value.Items).ReceiptId);

        Assert.Equal(quest.Quest.Id, spend.QuestId);

        Assert.True(spend.EventSequence > snapshot.Value.Watermark);

        var reread = await f.Portal.ReadScopedReplayAsync(f.Caller, quest.Quest.Id, quest.Quest.Id, null, snapshot.Value.ResumeCursor, default);

        Assert.True(reread.IsSuccess, reread.Error.Message);

        var repeated = Assert.Single(reread.Value.Page.Items.Where(item => item.EventKind == GuildEventKind.SpendChanged));

        Assert.Equal(spend.EventSequence, repeated.EventSequence);

        Assert.Equal(spend.QuestId, repeated.QuestId);

        GuildSpendCorrectionRequest correction = new(Guid.NewGuid(), settled.Value.Revision, GuildSpendOrigin.OperatorAttested,
            new(8, 4, 0, 0, .05m), new string('A', 64));

        var beforeCorrection = await f.Portal.CaptureTreeSnapshotAsync(f.Caller, quest.Quest.Id, default);

        Assert.True(beforeCorrection.IsSuccess, beforeCorrection.Error.Message);

        IGuildTreeSpendLedger ledger = f.Store;

        var beforeSpend = await ledger.ReadAsync(f.Caller, quest.Quest.Id, default);

        Assert.True(beforeSpend.IsSuccess, beforeSpend.Error.Message);

        Assert.Equal(10, beforeSpend.Value.KnownTokens);

        await f.ExecuteAsync("CREATE TRIGGER fixture_abort_correction_receipt BEFORE INSERT ON guild_spend_receipts BEGIN SELECT RAISE(ABORT,'injected'); END");

        var failedCorrection = await f.Store.CorrectOperatorSpendAsync(f.Caller, quest.Quest.Id, effect.EffectId, correction, default);

        Assert.True(failedCorrection.IsFailure);

        var failedSnapshot = await f.Portal.CaptureTreeSnapshotAsync(f.Caller, quest.Quest.Id, default);

        var failedSpend = await ledger.ReadAsync(f.Caller, quest.Quest.Id, default);

        var failedReplay = await f.Portal.ReadScopedReplayAsync(f.Caller, quest.Quest.Id, quest.Quest.Id, null, beforeCorrection.Value.ResumeCursor, default);

        Assert.True(failedSnapshot.IsSuccess, failedSnapshot.Error.Message);

        Assert.True(failedSpend.IsSuccess, failedSpend.Error.Message);

        Assert.True(failedReplay.IsSuccess, failedReplay.Error.Message);

        Assert.Equal(beforeCorrection.Value.Watermark, failedSnapshot.Value.Watermark);

        Assert.Equal(beforeSpend.Value, failedSpend.Value);

        Assert.Empty(failedReplay.Value.Page.Items);

        await f.ExecuteAsync("DROP TRIGGER fixture_abort_correction_receipt");

        var corrected = await f.Store.CorrectOperatorSpendAsync(f.Caller, quest.Quest.Id, effect.EffectId, correction, default);

        Assert.True(corrected.IsSuccess, corrected.Error.Message);

        var afterSpend = await ledger.ReadAsync(f.Caller, quest.Quest.Id, default);

        Assert.True(afterSpend.IsSuccess, afterSpend.Error.Message);

        Assert.Equal(12, afterSpend.Value.KnownTokens);

        Assert.Equal(.05m, afterSpend.Value.KnownUsd);

        var correctionReplay = await f.Portal.ReadScopedReplayAsync(f.Caller, quest.Quest.Id, quest.Quest.Id, null, beforeCorrection.Value.ResumeCursor, default);

        Assert.True(correctionReplay.IsSuccess, correctionReplay.Error.Message);

        var correctionEvent = Assert.Single(correctionReplay.Value.Page.Items.Where(item => item.SpendSource is not null));

        Assert.Equal(new GuildSpendSourceDto(effect.EffectId, corrected.Value.Receipt.ReceiptId, corrected.Value.Receipt.EvidenceRevision,
            GuildSpendOrigin.OperatorAttested), correctionEvent.SpendSource);

        Assert.Equal(corrected.Value.Receipt.EventSequence, correctionEvent.EventSequence);

        var correctionReconnect = await f.Portal.ReadScopedReplayAsync(f.Caller, quest.Quest.Id, quest.Quest.Id, null, beforeCorrection.Value.ResumeCursor, default);

        Assert.True(correctionReconnect.IsSuccess, correctionReconnect.Error.Message);

        Assert.Equal(correctionEvent, Assert.Single(correctionReconnect.Value.Page.Items.Where(item => item.SpendSource is not null)));

        var correctedReplay = await f.Portal.ReadScopedReplayAsync(f.Caller, quest.Quest.Id, quest.Quest.Id, null, snapshot.Value.ResumeCursor, default);

        Assert.True(correctedReplay.IsSuccess, correctedReplay.Error.Message);

        GuildChangeEventDto[] physicalEvents = correctedReplay.Value.Page.Items.Where(item => item.SpendSource is not null).ToArray();

        Assert.Equal(2, physicalEvents.Length);

        Assert.Equal(source, physicalEvents[0].SpendSource);

        Assert.Equal(new GuildSpendSourceDto(effect.EffectId, corrected.Value.Receipt.ReceiptId,
            corrected.Value.Receipt.EvidenceRevision, GuildSpendOrigin.OperatorAttested), physicalEvents[1].SpendSource);

        var unscoped = await f.Portal.ReadReplayAsync(f.Caller, quest.Quest.Id, unscopedCursor.Value.Cursor, default);

        Assert.True(unscoped.IsSuccess, unscoped.Error.Message);

        Assert.Equal(physicalEvents.Select(item => item.SpendSource), unscoped.Value.Page.Items.Where(item => item.SpendSource is not null).Select(item => item.SpendSource));

        var historyEvents = await f.Portal.ReadEventsAsync(f.Caller, new(quest.Quest.Id, null, null, null), default);

        Assert.True(historyEvents.IsSuccess, historyEvents.Error.Message);

        Assert.Equal(physicalEvents.Select(item => item.SpendSource), historyEvents.Value.Items.Where(item => item.SpendSource is not null).Select(item => item.SpendSource));

        await f.ExecuteAsync($"DELETE FROM guild_events WHERE RootQuestId='{quest.Quest.Id.ToString("D").ToUpperInvariant()}' AND EventSequence={spend.EventSequence}");

        long remainingEvents = await f.CountAsync("guild_events");

        var initialReplay = await f.Store.SettleProviderEffectAsync(execution, daily, effect, bill, default);

        Assert.True(initialReplay.IsSuccess, initialReplay.Error.Message);

        Assert.Equal(settled.Value.Revision, initialReplay.Value.Revision);

        Assert.Equal(remainingEvents, await f.CountAsync("guild_events"));

        var retained = await f.Store.ReadOperatorSpendReceiptAsync(f.Caller, quest.Quest.Id, effect.EffectId, source.ReceiptId, default);

        Assert.True(retained.IsSuccess, retained.Error.Message);

        Assert.Equal(spend.EventSequence, retained.Value.EventSequence);

        Assert.Equal(1L, await f.CountAsync("BillableOperations"));

        Assert.Equal(2L, await f.CountAsync("guild_budget_evidence"));
    }
}
