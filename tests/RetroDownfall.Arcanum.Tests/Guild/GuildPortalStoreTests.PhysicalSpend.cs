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

        BillableOperationRecord bill = new(quest.Quest.RunId, BillableOperationType.Chat, "provider", "model", "guild",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 7, 3, 0, 0, "{}", 0m, BillableOperationStatus.Completed, null);

        var settled = await f.Store.SettleProviderEffectAsync(execution, daily, effect, bill, default);

        Assert.True(settled.IsSuccess, settled.Error.Message);

        Assert.Equal(10, settled.Value.KnownTokens);

        Assert.True((await f.Store.SettleProviderEffectAsync(execution, daily, effect, bill, default)).IsSuccess);

        Assert.Equal(1, sends);

        var replay = await f.Portal.ReadScopedReplayAsync(f.Caller, quest.Quest.Id, quest.Quest.Id, null, snapshot.Value.ResumeCursor, default);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        var spend = Assert.Single(replay.Value.Page.Items.Where(item => item.EventKind == GuildEventKind.SpendChanged));

        Assert.Equal(quest.Quest.Id, spend.QuestId);

        Assert.True(spend.EventSequence > snapshot.Value.Watermark);

        var reread = await f.Portal.ReadScopedReplayAsync(f.Caller, quest.Quest.Id, quest.Quest.Id, null, snapshot.Value.ResumeCursor, default);

        Assert.True(reread.IsSuccess, reread.Error.Message);

        var repeated = Assert.Single(reread.Value.Page.Items.Where(item => item.EventKind == GuildEventKind.SpendChanged));

        Assert.Equal(spend.EventSequence, repeated.EventSequence);

        Assert.Equal(spend.QuestId, repeated.QuestId);

        Assert.Equal(1L, await f.CountAsync("BillableOperations"));

        Assert.Equal(1L, await f.CountAsync("guild_budget_evidence"));
    }
}
