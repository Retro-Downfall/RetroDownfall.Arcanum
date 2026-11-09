using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Theory]
    [InlineData("accepted")]
    [InlineData("disconnect")]
    [InlineData("rejected")]
    [InlineData("uncertain")]
    [InlineData("stopped")]
    public async Task Remote_cancellation_recovery_distinguishes_request_outcomes_from_durable_stop_after_restart(string response)
    {
        await using Fixture f = await Fixture.CreateAsync();

        var row = await ReceiptRootAsync(f);

        var driver = await f.DriverAsync(row);

        var prepared = await f.Store.PrepareDispatchAsync(driver, Guid.NewGuid(), ReceiptDigest("remote invocation"),
            row.Quest.RunId, false, default);

        Assert.True(prepared.IsSuccess, prepared.Error.Message);

        var dispatched = await f.Store.MarkPossiblyDispatchedAsync(driver, prepared.Value.DispatchId, prepared.Value.Revision, default);

        Assert.True(dispatched.IsSuccess, dispatched.Error.Message);

        var remote = await f.Store.RecordDispatchEvidenceAsync(driver,
            new(prepared.Value.DispatchId, dispatched.Value.Dispatch.Revision, GuildDispatchEvidence.Confirmed,
                RemoteTaskId: "original-peer-task", RemoteContextId: "original-peer-context"), default);

        Assert.True(remote.IsSuccess, remote.Error.Message);

        row = (await f.Store.GetAsync(f.Caller, row.Quest.Id, default)).Value;

        Assert.Equal(QuestState.Working, row.Quest.State);

        GuildCancelQuestRequest cancel = new(Guid.NewGuid(), row.Quest.Revision, GuildCancellationReason.OperatorRequested);

        GuildMutationIdentity mutation = new(cancel.IdempotencyKey, GuildQuestIntent.Compute(row.Quest.Id, cancel).Value);

        var accepted = await f.Store.RequestCancellationAsync(f.Caller, mutation, row.Quest.Id, cancel, default);

        Assert.True(accepted.IsSuccess, accepted.Error.Message);

        Assert.False(accepted.Value.CancellationConverged);

        Assert.Equal(0L, await f.CountAsync("fixture_settlements"));

        if (response == "stopped")
        {
            driver = driver with { OperationRevision = (await ReadFrontierOperationAsync(f, row)).Revision };

            GuildQuestResult stop = new(new(row.Quest.Id, Guid.NewGuid(), ReceiptDigest(""), null,
                QuestState.Canceled, LongRunningOperationRecoveryOutcomes.Cancelled), null, WorkStoppedProven: true);

            var committed = await f.Store.CommitResultAsync(driver, new(Guid.NewGuid(), ReceiptDigest("stop receipt")),
                accepted.Value.Revision, stop, default);

            Assert.True(committed.IsSuccess, committed.Error.Message);
        }

        f.Restart();

        CancellationRecoveryObserver observer = new(response);

        GuildQuestRecoveryHandler recovery = new(f.Connections, f.Gate, TimeProvider.System, [observer]);

        var operation = await ReadFrontierOperationAsync(f, row);

        for (int retry = 0; retry < 2; retry++)
        {
            var recovered = await recovery.RecoverAsync(operation, default);

            Assert.Equal(response == "stopped" ? LongRunningOperationState.Completed : LongRunningOperationState.ReconciliationRequired,
                recovered.State);

            var replay = await f.Store.RequestCancellationAsync(f.Caller, mutation, row.Quest.Id, cancel, default);

            Assert.True(replay.IsSuccess, replay.Error.Message);

            Assert.True(replay.Value.Receipt!.Replayed);

            Assert.Equal(response == "stopped", replay.Value.CancellationConverged);
        }

        var retained = (await f.Store.GetAsync(f.Caller, row.Quest.Id, default)).Value.Quest;

        Assert.Equal(response == "stopped" ? QuestState.Canceled : QuestState.Working, retained.State);

        Assert.Equal(response == "stopped" ? 0 : 2, observer.Targets.Count);

        Assert.All(observer.Targets, target =>
        {
            Assert.Equal("original-peer-task", target.RemoteTaskId);

            Assert.Equal("original-peer-context", target.RemoteContextId);

            Assert.Equal(prepared.Value.DispatchId, target.DispatchId);

            Assert.True(target.CancellationRequested);
        });

        Assert.Equal(1L, await f.CountAsync("guild_dispatches"));

        Assert.Equal(1L, await f.CountAsync("guild_quest_cancellations"));

        Assert.Equal(response == "stopped" ? 1L : 0L, await f.CountAsync("fixture_settlements"));
    }

    private sealed class CancellationRecoveryObserver(string response) : IGuildQuestRecoveryObserver
    {
        internal List<GuildQuestRecoveryTarget> Targets { get; } = [];

        public bool CanObserve(GuildQuestRecoveryTarget target) => target.RemoteTaskId == "original-peer-task";

        public Task<Result> ObserveAsync(GuildQuestRecoveryTarget target, CancellationToken cancellationToken)
        {
            Targets.Add(target);

            return response switch
            {
                "disconnect" => Task.FromException<Result>(new HttpRequestException("The peer disconnected before confirming a stop.")),
                "rejected" => Task.FromResult(Result.Failure(new Error(ErrorCodes.Guild.Rejected, "The peer rejected this cancellation request."))),
                "uncertain" => Task.FromResult(Result.Failure(new Error(ErrorCodes.Guild.ReconciliationRequired, "The peer outcome is unknown."))),
                _ => Task.FromResult(Result.Success())
            };
        }
    }
}
