using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task Cohort_live_seal_cancels_and_joins_running_primary_without_a_successful_vote(bool withdrawal, bool erased)
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var setup = await CreateCohortAuthorityAsync(f, false);

        var owner = await f.OperatorAsync();

        var member = await f.Store.GetAsync(owner, setup.Execution.QuestId, default);

        Assert.True(member.IsSuccess, member.Error.Message);

        var opened = await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

        Assert.True(opened.IsSuccess, opened.Error.Message);

        await using var connection = opened.Value;

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection.Connection);

        LongRunningOperationStore operations = new(db, f.Connections);

        var operation = await operations.GetAsync(member.Value.Quest.OperationId);

        Assert.NotNull(operation);

        var request = await f.Store.PrepareLocalScheduleAsync(owner, setup.Execution, operation.Revision, default);

        Assert.True(request.IsSuccess, request.Error.Message);

        LongRunningOperationOwnership ownership = new();

        SchedulerEngine engine = new();

        GuildLocalPort port = new(f.Store, f.Policy, f.Authority, operations, engine, new SchedulerInference(), new ChildQueue(),
            ownership, TimeProvider.System);

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));

        Task<Result<GuildDispatchObservation>> running = port.DispatchAsync(setup.Execution, request.Value, deadline.Token);

        try
        {
            await engine.Entered.Task.WaitAsync(deadline.Token);

            if (erased)
            {
                var root = await f.Store.GetAsync(owner, setup.Execution.RootQuestId, default);

                Assert.True(root.IsSuccess, root.Error.Message);

                await SealLocalOwnerAsync(f, root.Value.Quest.SessionId!.Value);

                await using var erasedConnection = await f.OpenAsync();

                await using var transaction = erasedConnection.BeginTransaction(deferred: false);

                Guid contentId = member.Value.Quest.GoalRef.ContentId;

                await GuildContentLifecycle.PrepareOrdinaryPurgeAsync(erasedConnection, transaction, contentId.ToString("N"), default);

                await using var erase = erasedConnection.CreateCommand();

                erase.Transaction = transaction;

                erase.CommandText = "DELETE FROM guild_content WHERE ContentId=$id";

                erase.Parameters.AddWithValue("$id", contentId.ToString("D").ToUpperInvariant());

                Assert.Equal(1, await erase.ExecuteNonQueryAsync());

                await transaction.CommitAsync();
            }
            else if (withdrawal)
            {
                await f.ExecuteAsync($"""
                    UPDATE guild_cohorts SET MembershipVersion=MembershipVersion+1,Revision=Revision+1 WHERE Id='{setup.Cohort.Id:D}' COLLATE NOCASE;
                    UPDATE guild_cohort_members SET StateCode=2,RemovalSequence=2 WHERE CohortId='{setup.Cohort.Id:D}' COLLATE NOCASE;
                    UPDATE guild_cohort_member_quests SET Active=0;
                    """);
            }
            else
            {
                await f.ExecuteAsync($"UPDATE guild_cohorts SET StateCode=3,DecisionCode=3,ReasonCode='Guild.Canceled',ClosedMembershipVersion=MembershipVersion,Revision=Revision+1 WHERE Id='{setup.Cohort.Id:D}' COLLATE NOCASE");
            }

            int effects = 0;

            var denied = await f.Authority.AdmitToolAsync(setup.Execution, CohortAuthorityTool, true,
                (_, _) => Task.FromResult(Result<bool>.Success(++effects > 0)), default);

            Assert.True(denied.IsFailure);

            Assert.Equal(0, effects);

            if (!erased)
            {
                var current = await f.Store.GetAsync(owner, member.Value.Quest.Id, default);

            GuildCancelQuestRequest stop = new(Guid.NewGuid(), current.Value.Quest.Revision, GuildCancellationReason.OperatorRequested);

            var canceled = await f.Store.RequestCancellationAsync(owner,
                new(stop.IdempotencyKey, GuildQuestIntent.Compute(member.Value.Quest.Id, stop).Value), member.Value.Quest.Id, stop, default);

            Assert.True(canceled.IsSuccess, canceled.Error.Message);
            }

            while (!engine.InvocationToken.IsCancellationRequested) await Task.Delay(20, deadline.Token);

            Assert.False(running.IsCompleted);

            Assert.True(ownership.IsClaimed(member.Value.Quest.OperationId));

            engine.Release.TrySetResult();

            var stopped = await running.WaitAsync(deadline.Token);

            Assert.True(stopped.IsSuccess, stopped.Error.Message);

            Assert.Equal(QuestState.Canceled, stopped.Value.State);

            Assert.False(ownership.IsClaimed(member.Value.Quest.OperationId));

            Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_result_receipts WHERE OutcomeCode=5"));

            Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_cohort_contributions WHERE OutcomeCode=1"));
        }
        finally
        {
            engine.Release.TrySetResult();

            await running;
        }
    }
}
