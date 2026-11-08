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
