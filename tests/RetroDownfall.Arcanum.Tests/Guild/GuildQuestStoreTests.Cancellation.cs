using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Fact]
    public async Task Local_cancellation_is_durable_idempotent_and_closes_new_dispatch_before_drain()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored row = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildQuestDriver driver = await f.DriverAsync(row);

        GuildCancelQuestRequest request = new(Guid.NewGuid(), row.Quest.Revision, GuildCancellationReason.OperatorRequested);

        GuildMutationIdentity mutation = new(request.IdempotencyKey, GuildQuestIntent.Compute(row.Quest.Id, request).Value);

        Result<GuildCancellationDto> canceled = await f.Store.RequestCancellationAsync(f.Caller, mutation, row.Quest.Id, request, default);

        Assert.True(canceled.IsSuccess, canceled.Error.Message);

        Assert.True(canceled.Value.CancellationRequested);

        Assert.False(canceled.Value.CancellationConverged);

        Assert.False(canceled.Value.Receipt!.Replayed);

        Assert.True((await f.Store.PrepareDispatchAsync(driver, Guid.NewGuid(), new string('A', 64), Guid.NewGuid(), false, default)).IsFailure);

        f.Restart();

        Result<GuildCancellationDto> replay = await f.Store.RequestCancellationAsync(f.Caller, mutation, row.Quest.Id, request, default);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.True(replay.Value.Receipt!.Replayed);

        Assert.Equal(canceled.Value.Receipt.AcceptedRevision, replay.Value.Receipt.AcceptedRevision);

        Assert.Equal(1L, await f.CountAsync("guild_quest_cancellations"));

        GuildCancelQuestRequest changed = request with { ReasonCode = GuildCancellationReason.OwnerDeleted };

        Result<GuildCancellationDto> conflict = await f.Store.RequestCancellationAsync(f.Caller,
            new(changed.IdempotencyKey, GuildQuestIntent.Compute(row.Quest.Id, changed).Value), row.Quest.Id, changed, default);

        Assert.True(conflict.IsFailure);

        Assert.Equal(ErrorCodes.Guild.IdempotencyConflict, conflict.Error.Code);
    }

    [Fact]
    public async Task Failed_parent_commits_its_outcome_and_durable_descendant_cancellation_frontier()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored parent = await ReceiptRootAsync(f);

        GuildQuestDriver driver = await f.DriverAsync(parent);

        GuildQuestStored child = await f.ChildAsync(parent, driver);

        GuildQuestResult failure = new(new(parent.Quest.Id, Guid.NewGuid(), ReceiptDigest(""),
            null, QuestState.Failed, ErrorCodes.Guild.Unavailable), null, NoDispatchProven: true, WorkStoppedProven: true);

        var committed = await f.Store.CommitResultAsync(driver, new(Guid.NewGuid(), ReceiptDigest("failure mutation")),
            parent.Quest.Revision, failure, default);

        Assert.True(committed.IsSuccess, committed.Error.Message);

        f.Restart();

        var persisted = await f.Store.GetAsync(f.Caller, parent.Quest.Id, default);

        Assert.True(persisted.IsSuccess, persisted.Error.Message);

        Assert.Equal(QuestState.Failed, persisted.Value.Quest.State);

        Assert.True(persisted.Value.Quest.CancellationRequested);

        Assert.False(persisted.Value.Quest.CancellationConverged);

        var frontier = await f.Store.ReadLocalCancellationFrontierAsync(f.Caller, child.Quest.Id, default);

        Assert.True(frontier.IsSuccess, frontier.Error.Message);

        Assert.True(frontier.Value.AncestorCanceled);

        Assert.Equal(1L, await f.CountAsync("guild_result_receipts"));

        var childStopped = await f.Store.CommitResultAsync(await f.DriverAsync(child),
            new(Guid.NewGuid(), ReceiptDigest("child stop")), child.Quest.Revision,
            ReceiptResult(child.Quest.Id) with { NoDispatchProven = true, WorkStoppedProven = true }, default);

        Assert.True(childStopped.IsSuccess, childStopped.Error.Message);

        persisted = await f.Store.GetAsync(f.Caller, parent.Quest.Id, default);

        Assert.Equal(QuestState.Failed, persisted.Value.Quest.State);

        Assert.True(persisted.Value.Quest.CancellationConverged);
    }

    [Fact]
    public async Task Cancellation_work_page_tracks_exact_subtree_after_restart_and_skips_terminal_nodes()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored root = await ReceiptRootAsync(f);

        GuildQuestDriver rootDriver = await f.DriverAsync(root);

        GuildQuestStored middle = await f.ChildAsync(root, rootDriver);

        GuildQuestStored sibling = await f.ChildAsync(root, rootDriver);

        GuildQuestDriver middleDriver = await f.DriverAsync(middle);

        GuildQuestStored child = await f.ChildAsync(middle, middleDriver);

        GuildQuestStored terminal = await f.ChildAsync(middle, middleDriver);

        var finished = await f.Store.CommitResultAsync(await f.DriverAsync(terminal),
            new(Guid.NewGuid(), ReceiptDigest("finished child")), terminal.Quest.Revision,
            ReceiptResult(terminal.Quest.Id) with { NoDispatchProven = true }, default);

        Assert.True(finished.IsSuccess, finished.Error.Message);

        GuildCancelQuestRequest cancel = new(Guid.NewGuid(), middle.Quest.Revision, GuildCancellationReason.OperatorRequested);

        var accepted = await f.Store.RequestCancellationAsync(f.Caller,
            new(cancel.IdempotencyKey, GuildQuestIntent.Compute(middle.Quest.Id, cancel).Value), middle.Quest.Id, cancel, default);

        Assert.True(accepted.IsSuccess, accepted.Error.Message);

        f.Restart();

        var page = await f.Store.ReadCancellationWorkPageAsync(f.Caller, middle.Quest.Id, 0, default);

        Assert.True(page.IsSuccess, page.Error.Message);

        Assert.Equal(child.Quest.Id, Assert.Single(page.Value).QuestId);

        Assert.False((await f.Store.GetAsync(f.Caller, sibling.Quest.Id, default)).Value.Quest.CancellationRequested);

        Assert.Empty((await f.Store.ReadCancellationWorkPageAsync(f.Caller, middle.Quest.Id,
            page.Value[0].IssuedSequence, default)).Value);
    }

    [Fact]
    public async Task Running_cancellation_frontier_requires_the_exact_self_execution_and_current_authority()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var root = await ReceiptRootAsync(f);

        var rootDriver = await f.DriverAsync(root);

        var child = await f.ChildAsync(root, rootDriver);

        var sibling = await f.ChildAsync(root, rootDriver);

        var childDriver = await f.DriverAsync(child);

        GuildCallerContext caller = new(GuildCallerKind.Run, f.Caller.PrincipalId!.Value, Guid.NewGuid(),
            f.Host.InstallationGeneration, childDriver.Execution);

        Assert.True((await f.Store.ReadLocalCancellationFrontierAsync(caller, child.Quest.Id, default)).IsSuccess);

        Assert.True((await f.Store.ReadLocalCancellationFrontierAsync(caller, root.Quest.Id, default)).IsFailure);

        Assert.True((await f.Store.ReadLocalCancellationFrontierAsync(caller, sibling.Quest.Id, default)).IsFailure);

        f.Restart();

        Assert.True((await f.Store.ReadLocalCancellationFrontierAsync(caller, child.Quest.Id, default)).IsFailure);
    }

    [Fact]
    public async Task Local_cancellation_requires_current_revision_and_issuer_lineage()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildQuestDriver parentDriver = await f.DriverAsync(root);

        GuildQuestStored child = await f.ChildAsync(root, parentDriver);

        GuildQuestDriver childDriver = await f.DriverAsync(child);

        GuildCallerContext caller = new(GuildCallerKind.Run, f.Caller.PrincipalId!.Value, Guid.NewGuid(), f.Host.InstallationGeneration, childDriver.Execution);

        GuildCancelQuestRequest rootCancel = new(Guid.NewGuid(), root.Quest.Revision, GuildCancellationReason.OperatorRequested);

        Result<GuildCancellationDto> ancestor = await f.Store.RequestCancellationAsync(caller,
            new(rootCancel.IdempotencyKey, GuildQuestIntent.Compute(root.Quest.Id, rootCancel).Value), root.Quest.Id, rootCancel, default);

        Assert.True(ancestor.IsFailure);

        Assert.Equal(0L, await f.CountAsync("guild_quest_cancellations"));

        GuildCancelQuestRequest stale = new(Guid.NewGuid(), child.Quest.Revision + 10, GuildCancellationReason.OperatorRequested);

        Result<GuildCancellationDto> conflict = await f.Store.RequestCancellationAsync(f.Caller,
            new(stale.IdempotencyKey, GuildQuestIntent.Compute(child.Quest.Id, stale).Value), child.Quest.Id, stale, default);

        Assert.True(conflict.IsFailure);

        Assert.Equal(ErrorCodes.Guild.RevisionConflict, conflict.Error.Code);

        Assert.Equal(0L, await f.CountAsync("guild_quest_cancellations"));
    }
}
