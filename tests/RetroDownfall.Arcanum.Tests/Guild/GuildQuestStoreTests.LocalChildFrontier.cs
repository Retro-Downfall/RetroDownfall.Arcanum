using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Theory]
    [InlineData("no-children", true)]
    [InlineData("direct-interruption", false)]
    [InlineData("grandchild-interruption", false)]
    [InlineData("parked-input", false)]
    [InlineData("generic-recovery", false)]
    [InlineData("live", false)]
    [InlineData("stopped", true)]
    [InlineData("possible-exposure", false)]
    public async Task Local_child_frontier_separates_explicit_interruption_from_resumable_or_live_descendants(string disposition, bool stopped)
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored parent = await ReceiptRootAsync(f);

        _ = await CreateLocalAsync(f, parent, LocalInput(parent));

        GuildQuestDriver parentDriver = await f.DriverAsync(parent);

        if (disposition != "no-children")
        {
            GuildQuestStored child = await f.ChildAsync(parent, parentDriver);

            GuildLocalExecutionBinding childBinding = await CreateLocalAsync(f, child,
                disposition == "parked-input"
                    ? LocalInput(child) with { Mode = GuildLocalExecutionMode.Apprentice }
                    : LocalInput(child));

            GuildQuestDriver childDriver = await f.DriverAsync(child, "frontier-child");

            if (disposition == "grandchild-interruption")
            {
                child = await f.ChildAsync(child, childDriver);

                childBinding = await CreateLocalAsync(f, child, LocalInput(child));

                childDriver = await f.DriverAsync(child, "frontier-grandchild");
            }

            if (disposition is "direct-interruption" or "grandchild-interruption")
            {
                GuildQuestDispatchRecord dispatch = (await f.Store.PrepareDispatchAsync(childDriver, Guid.NewGuid(),
                    ReceiptDigest("interrupted frontier"), child.Quest.RunId, false, default)).Value;

                var parked = await f.Store.ParkLocalExecutionAsync(childDriver, default);

                Assert.True(parked.IsSuccess, parked.Error.Message);

                Assert.True(parked.Value.Quest.ReconciliationRequired);

                Assert.Equal(ErrorCodes.Guild.LocalExecutionInterrupted, (await ReadFrontierOperationAsync(f, child)).TerminalErrorCode);
            }
            else if (disposition == "parked-input")
            {
                GuildQuestDispatchRecord dispatch = (await f.Store.PrepareDispatchAsync(childDriver, Guid.NewGuid(),
                    ReceiptDigest("waiting frontier"), child.Quest.RunId, false, default)).Value;

                Guid question = Guid.NewGuid(), continuation = Guid.NewGuid();

                GuildLocalWaitSnapshot snapshot = WaitSnapshot(childBinding);

                snapshot = snapshot with { Checkpoint = snapshot.Checkpoint with { ParentApprenticeId = childBinding.ParentApprenticeId } };

                var waiting = await f.Store.WaitForInputAsync(childDriver, question, continuation,
                    new(dispatch.DispatchId, snapshot), default);

                Assert.True(waiting.IsSuccess, waiting.Error.Message);

                LongRunningOperation operation = await ReadFrontierOperationAsync(f, child);

                childDriver = childDriver with { OperationRevision = operation.Revision };

                var parked = await f.Store.ParkLocalExecutionAsync(childDriver, default);

                Assert.True(parked.IsSuccess, parked.Error.Message);

                var readable = await f.Store.GetAsync(f.Caller, child.Quest.Id, default);

                Assert.True(readable.IsSuccess, readable.Error.Message);

                Assert.True(readable.Value.Quest.ReconciliationRequired);

                Assert.NotNull(readable.Value.Quest.AwaitingInput);

                Assert.Equal(question, readable.Value.Quest.AwaitingInput!.QuestionId);

                Assert.Equal(ErrorCodes.Guild.AwaitingInput, (await ReadFrontierOperationAsync(f, child)).TerminalErrorCode);
            }
            else if (disposition == "generic-recovery")
            {
                await TransitionFrontierOperationAsync(f, child, LongRunningOperationState.ReconciliationRequired,
                    ErrorCodes.Guild.ReconciliationRequired);

                var pending = await f.Store.ReadLocalPendingAsync(f.Caller, child.Quest.Id, default);

                Assert.True(pending.IsSuccess, pending.Error.Message);

                Assert.False(pending.Value.RequiresAttention);
            }
            else if (disposition == "stopped")
            {
                var completed = await f.Store.CommitResultAsync(childDriver,
                    new(Guid.NewGuid(), ReceiptDigest("stopped frontier")), child.Quest.Revision,
                    ReceiptResult(child.Quest.Id) with { NoDispatchProven = true }, default);

                Assert.True(completed.IsSuccess, completed.Error.Message);
            }
            else if (disposition == "possible-exposure")
            {
                var dispatch = await f.Store.PrepareDispatchAsync(childDriver, Guid.NewGuid(),
                    ReceiptDigest("exposed frontier"), child.Quest.RunId, false, default);

                Assert.True(dispatch.IsSuccess, dispatch.Error.Message);

                Assert.True((await f.Store.MarkPossiblyDispatchedAsync(childDriver,
                    dispatch.Value.DispatchId, dispatch.Value.Revision, default)).IsSuccess);

                // Even a terminal operation cannot erase unresolved physical-dispatch evidence.
                await TransitionFrontierOperationAsync(f, child, LongRunningOperationState.Completed, null);
            }
        }

        var frontier = await f.Store.LocalChildrenStoppedAsync(parentDriver, default);

        if (disposition is "direct-interruption" or "grandchild-interruption")
        {
            Assert.True(frontier.IsFailure);

            Assert.Equal(ErrorCodes.Guild.ReconciliationRequired, frontier.Error.Code);
        }
        else
        {
            Assert.True(frontier.IsSuccess, frontier.Error.Message);

            Assert.Equal(stopped, frontier.Value);
        }
    }

    [Fact]
    public async Task Local_child_frontier_does_not_count_its_own_interruption_as_a_descendant()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored parent = await ReceiptRootAsync(f);

        _ = await CreateLocalAsync(f, parent, LocalInput(parent));

        GuildQuestDriver driver = await f.DriverAsync(parent);

        await TransitionFrontierOperationAsync(f, parent, LongRunningOperationState.ReconciliationRequired,
            ErrorCodes.Guild.LocalExecutionInterrupted);

        // This observation does not count the current operation among its own descendants.
        var frontier = await f.Store.LocalChildrenStoppedAsync(driver, default);

        Assert.True(frontier.IsSuccess, frontier.Error.Message);

        Assert.True(frontier.Value);
    }

    private static async Task<LongRunningOperation> ReadFrontierOperationAsync(Fixture f, GuildQuestStored quest)
    {
        await using SqliteConnection connection = await f.OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);

        return (await LongRunningOperationStore.ReadWithinAsync(connection, transaction, quest.Quest.OperationId, default))!;
    }

    private static async Task TransitionFrontierOperationAsync(Fixture f, GuildQuestStored quest,
        LongRunningOperationState state, string? reason)
    {
        await using SqliteConnection connection = await f.OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction();

        LongRunningOperation operation = (await LongRunningOperationStore.ReadWithinAsync(connection,
            transaction, quest.Quest.OperationId, default))!;

        Assert.True(await LongRunningOperationStore.TryTransitionWithinAsync(connection, transaction, operation.Id,
            operation.Revision, operation.LeaseOwner, state, DateTimeOffset.UtcNow, reason, default));

        await transaction.CommitAsync();
    }
}
