using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildPortalStoreTests
{
    [Fact]
    public async Task Tree_snapshot_replays_commits_after_its_watermark()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        Result<GuildTreeSnapshotDto> snapshot = await f.Portal.CaptureTreeSnapshotAsync(f.Caller, root.Quest.Id, default);

        Assert.True(snapshot.IsSuccess, snapshot.Error.Message);

        Assert.Equal(root.Quest.Id, snapshot.Value.Summary.Id);

        Assert.Contains(snapshot.Value.Members.Items, item => item.Id == root.Quest.Id);

        await AppendReplayEventsAsync(f, root.Quest.Id, root.Quest.Id, 1);

        Result<GuildPortalReplayPage> replay = await f.Portal.ReadScopedReplayAsync(
            f.Caller, root.Quest.Id, root.Quest.Id, null, snapshot.Value.ResumeCursor, default);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.All(replay.Value.Page.Items, item => Assert.True(item.EventSequence > snapshot.Value.Watermark));

        Assert.Single(replay.Value.Page.Items);
    }

    [Fact]
    public async Task Operator_subtree_replay_excludes_siblings_and_rejects_cross_scope_cursor()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildQuestDriver driver = await f.DriverAsync(root);

        GuildQuestStored first = await f.ChildAsync(root, driver);

        root = (await f.Store.GetAsync(f.Caller, root.Quest.Id, default)).Value;

        GuildQuestStored second = await f.ChildAsync(root, driver);

        Result<GuildPortalReplayCursor> captured = await f.Portal.CaptureScopedReplayCursorAsync(
            f.Caller, root.Quest.Id, first.Quest.Id, null, default);

        Assert.True(captured.IsSuccess, captured.Error.Message);

        await AppendReplayEventsAsync(f, root.Quest.Id, second.Quest.Id, 1);

        await AppendReplayEventsAsync(f, root.Quest.Id, first.Quest.Id, 1);

        Result<GuildPortalReplayPage> page = await f.Portal.ReadScopedReplayAsync(
            f.Caller, root.Quest.Id, first.Quest.Id, null, captured.Value.Cursor, default);

        Assert.True(page.IsSuccess, page.Error.Message);

        Assert.Single(page.Value.Page.Items);

        Assert.Equal(first.Quest.Id, page.Value.Page.Items[0].QuestId);

        Result<GuildPortalReplayPage> wrong = await f.Portal.ReadScopedReplayAsync(
            f.Caller, root.Quest.Id, second.Quest.Id, null, captured.Value.Cursor, default);

        Assert.Equal(ErrorCodes.Guild.CursorInvalid, wrong.Error.Code);
    }
    [Fact]
    public async Task Operator_watch_cursor_is_retired_when_sensitivity_clock_changes()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        Result<GuildTreeSnapshotDto> snapshot = await f.Portal.CaptureTreeSnapshotAsync(f.Caller, root.Quest.Id, default);

        Assert.True(snapshot.IsSuccess, snapshot.Error.Message);

        await f.ExecuteAsync("INSERT INTO artifact_sensitivity_clock(Id,Revision) VALUES(1,1) ON CONFLICT(Id) DO UPDATE SET Revision=Revision+1");

        Result<GuildPortalReplayPage> replay = await f.Portal.ReadScopedReplayAsync(
            f.Caller, root.Quest.Id, root.Quest.Id, null, snapshot.Value.ResumeCursor, default);

        Assert.Equal(ErrorCodes.Guild.CursorGone, replay.Error.Code);
    }

    [Fact]
    public async Task Tree_terminal_waits_for_result_delivery_and_current_event_head()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildQuestDriver driver = await f.DriverAsync(root);

        GuildQuestResult result = PortalResult(root.Quest.Id, "result");

        Assert.True((await f.Store.CommitResultAsync(driver, new(Guid.NewGuid(), result.Receipt.CanonicalContentDigest),
            root.Quest.Revision, result, default)).IsSuccess);

        Assert.True((await f.Portal.DrainOutboxAsync(f.Caller, root.Quest.Id, default)).IsSuccess);

        var snapshot = await f.Portal.CaptureTreeSnapshotAsync(f.Caller, root.Quest.Id, default);

        Assert.True(snapshot.IsSuccess, snapshot.Error.Message);

        Task<Result<GuildTerminalEventDto?>> Read(string cursor) => f.Portal.ReadTreeTerminalAsync(
            f.Caller, root.Quest.Id, root.Quest.Id, cursor, default);

        var pending = await Read(snapshot.Value.ResumeCursor);

        Assert.True(pending.IsSuccess, pending.Error.Message);

        Assert.Null(pending.Value);

        Assert.True((await f.Store.AcknowledgeRootResultAsync(f.Caller, root.Quest.Id, result.Receipt.ResultId,
            result.Receipt.CanonicalContentDigest, 1, default)).IsSuccess);

        var stale = await Read(snapshot.Value.ResumeCursor);

        Assert.True(stale.IsSuccess, stale.Error.Message);

        Assert.Null(stale.Value);

        var replay = await f.Portal.ReadScopedReplayAsync(f.Caller, root.Quest.Id, root.Quest.Id, null,
            snapshot.Value.ResumeCursor, default);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        var terminal = await Read(replay.Value.ResumeCursor);

        Assert.True(terminal.IsSuccess, terminal.Error.Message);

        Assert.NotNull(terminal.Value);

        Assert.Equal(QuestState.Completed, terminal.Value.State);
    }

    [Fact]
    public async Task Scoped_watch_reports_a_missing_durable_event_as_cursor_gone()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        Result<GuildTreeSnapshotDto> snapshot = await f.Portal.CaptureTreeSnapshotAsync(f.Caller, root.Quest.Id, default);

        Assert.True(snapshot.IsSuccess, snapshot.Error.Message);

        await AppendReplayEventsAsync(f, root.Quest.Id, root.Quest.Id, 2);

        await f.ExecuteAsync($"DELETE FROM guild_events WHERE EventSequence={snapshot.Value.Watermark + 1}");

        Result<GuildPortalReplayPage> replay = await f.Portal.ReadScopedReplayAsync(f.Caller, root.Quest.Id,
            root.Quest.Id, null, snapshot.Value.ResumeCursor, default);

        Assert.Equal(ErrorCodes.Guild.CursorGone, replay.Error.Code);
    }

    [Fact]
    public async Task Tree_terminal_reports_uncertain_descendant_accounting()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildQuestDriver parent = await f.DriverAsync(root);

        GuildQuestStored child = await f.ChildAsync(root, parent);

        GuildQuestDriver childDriver = await f.DriverAsync(child);

        GuildQuestResult childResult = PortalResult(child.Quest.Id, "child result");

        f.Budget.SettlementState = GuildAccountingState.Uncertain;

        Assert.True((await f.Store.CommitResultAsync(childDriver, new(Guid.NewGuid(), childResult.Receipt.CanonicalContentDigest),
            child.Quest.Revision, childResult, default)).IsSuccess);

        f.Budget.SettlementState = GuildAccountingState.Settled;

        GuildQuestResult rootResult = PortalResult(root.Quest.Id, "root result");

        Assert.True((await f.Store.CommitResultAsync(parent, new(Guid.NewGuid(), rootResult.Receipt.CanonicalContentDigest),
            root.Quest.Revision, rootResult, default)).IsSuccess);

        Assert.True((await f.Portal.DrainOutboxAsync(f.Caller, root.Quest.Id, default)).IsSuccess);

        Assert.True((await f.Store.AcknowledgeRootResultAsync(f.Caller, root.Quest.Id, rootResult.Receipt.ResultId,
            rootResult.Receipt.CanonicalContentDigest, 1, default)).IsSuccess);

        Result<GuildTreeSnapshotDto> snapshot = await f.Portal.CaptureTreeSnapshotAsync(f.Caller, root.Quest.Id, default);

        Assert.True(snapshot.IsSuccess, snapshot.Error.Message);

        Result<GuildTerminalEventDto?> terminal = await f.Portal.ReadTreeTerminalAsync(f.Caller, root.Quest.Id,
            root.Quest.Id, snapshot.Value.ResumeCursor, default);

        Assert.True(terminal.IsSuccess, terminal.Error.Message);

        Assert.NotNull(terminal.Value);

        Assert.Equal(GuildAccountingState.Uncertain, terminal.Value.AccountingState);
    }

}
