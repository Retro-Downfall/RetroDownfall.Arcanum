using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Fact]
    public async Task A_proven_recovered_input_wait_can_accept_its_answer_after_host_restart()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored row = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildQuestDriver driver = await f.DriverAsync(row);

        Guid continuation = Guid.NewGuid();

        var waiting = await f.Store.WaitForInputAsync(driver, Guid.NewGuid(), continuation, default);

        Assert.True(waiting.IsSuccess, waiting.Error.Message);

        LongRunningOperation operation;

        await using (SqliteConnection connection = await f.OpenAsync())
        {
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);

            operation = (await LongRunningOperationStore.ReadWithinAsync(connection, transaction, row.Quest.OperationId, default))!;
        }

        GuildQuestRecoveryHandler recovery = new(f.Connections, f.Gate, TimeProvider.System, []);

        LongRunningOperationRecoveryResult disposition = await recovery.RecoverAsync(operation, default);

        Assert.Equal(ErrorCodes.Guild.AwaitingInput, disposition.ErrorCode);

        await using (SqliteConnection connection = await f.OpenAsync())
        {
            using SqliteTransaction transaction = connection.BeginTransaction();

            Assert.True(await LongRunningOperationStore.TryTransitionWithinAsync(connection, transaction, operation.Id,
                operation.Revision, operation.LeaseOwner, disposition.State, DateTimeOffset.UtcNow, disposition.ErrorCode, default));

            await transaction.CommitAsync();
        }

        f.Restart();

        var accepted = await f.Store.AcceptContinuationAsync(f.Caller, row.Quest.Id,
            new(Guid.NewGuid(), waiting.Value.Quest.Revision, continuation, "text/plain", "Authorized answer"), default);

        Assert.True(accepted.IsSuccess, accepted.Error.Message);

        Assert.Equal(QuestState.Working, accepted.Value.Quest.State);

        Assert.Equal(1L, await f.CountAsync("guild_continuations"));
    }

    [Fact]
    public async Task An_answer_cannot_clear_unresolved_reconciliation_even_if_an_old_question_is_retained()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored row = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildQuestDriver driver = await f.DriverAsync(row);

        Guid continuation = Guid.NewGuid();

        var waiting = await f.Store.WaitForInputAsync(driver, Guid.NewGuid(), continuation, default);

        Assert.True(waiting.IsSuccess, waiting.Error.Message);

        await f.ExecuteAsync($"UPDATE LongRunningOperations SET State=7,TerminalErrorCode='Guild.ReconciliationRequired',Revision=Revision+1 WHERE Id='{row.Quest.OperationId:N}'");

        var answer = await f.Store.AcceptContinuationAsync(f.Caller, row.Quest.Id,
            new(Guid.NewGuid(), waiting.Value.Quest.Revision, continuation, "text/plain", "Continue"), default);

        Assert.True(answer.IsFailure, "A retained question cannot settle unknown execution evidence.");

        Assert.Equal(0L, await f.CountAsync("guild_continuations"));

        Assert.True((await f.Store.GetAsync(f.Caller, row.Quest.Id, default)).Value.Quest.ReconciliationRequired);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Locate_permission_does_not_let_a_peer_or_receipt_producer_answer_an_operator_question(bool peer)
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored row = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildQuestDriver driver = await f.DriverAsync(row);

        Guid continuation = Guid.NewGuid();

        var waiting = await f.Store.WaitForInputAsync(driver, Guid.NewGuid(), continuation, default);

        Assert.True(waiting.IsSuccess, waiting.Error.Message);

        GuildCallerContext observer = peer
            ? new("fixture-peer", "revision-one", Guid.NewGuid(), f.Host.InstallationGeneration)
            : new(GuildCallerKind.HostReceipt, Guid.NewGuid(), Guid.NewGuid(), f.Host.InstallationGeneration,
                receiptId: Guid.NewGuid(), receiptSource: "fixture-adapter");

        f.Host.Caller = observer;

        var answer = await f.Store.AcceptContinuationAsync(observer, row.Quest.Id,
            new(Guid.NewGuid(), waiting.Value.Quest.Revision, continuation, "text/plain", "Continue"), default);

        Assert.True(answer.IsFailure, "A body producer or peer cannot borrow operator continuation authority from visibility.");

        Assert.Equal(0L, await f.CountAsync("guild_continuations"));
    }

    [Fact]
    public async Task A_question_is_not_dispatch_permission_and_an_accepted_answer_replays_before_revision_checks()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored row = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildQuestDriver driver = await f.DriverAsync(row);

        Guid continuation = Guid.NewGuid();

        var waiting = await f.Store.WaitForInputAsync(driver, Guid.NewGuid(), continuation, default);

        Assert.True(waiting.IsSuccess, waiting.Error.Message);

        Assert.Equal(QuestState.InputRequired, waiting.Value.Quest.State);

        var premature = await f.Store.PrepareDispatchAsync(driver with { OperationRevision = driver.OperationRevision + 1 },
            continuation, new string('A', 64), Guid.NewGuid(), true, default);

        Assert.True(premature.IsFailure);

        GuildContinueRequest answer = new(Guid.NewGuid(), waiting.Value.Quest.Revision, continuation, "text/plain", "Proceed with option one.");

        var accepted = await f.Store.AcceptContinuationAsync(f.Caller, row.Quest.Id, answer, default);

        Assert.True(accepted.IsSuccess, accepted.Error.Message);

        Assert.Equal(QuestState.Working, accepted.Value.Quest.State);

        Assert.Equal(1L, await f.CountAsync("guild_continuations"));

        long contentCount = await f.CountAsync("guild_content");

        f.Restart();

        var replay = await f.Store.AcceptContinuationAsync(f.Caller, row.Quest.Id, answer, default);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.True(replay.Value.Quest.Receipt!.Replayed);

        Assert.Equal(accepted.Value.Quest.Revision, replay.Value.Quest.Revision);

        Assert.Equal(contentCount, await f.CountAsync("guild_content"));

        var changed = await f.Store.AcceptContinuationAsync(f.Caller, row.Quest.Id, answer with { Body = "Different answer" }, default);

        Assert.Equal(ErrorCodes.Guild.IdempotencyConflict, changed.Error.Code);
    }

    [Theory]
    [InlineData("guild_content", "INSERT")]
    [InlineData("guild_continuations", "INSERT")]
    [InlineData("guild_quests", "UPDATE")]
    [InlineData("LongRunningOperations", "UPDATE")]
    public async Task Answer_acceptance_is_atomic_at_every_durable_boundary(string table, string operation)
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored row = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildQuestDriver driver = await f.DriverAsync(row);

        Guid continuation = Guid.NewGuid();

        var waiting = await f.Store.WaitForInputAsync(driver, Guid.NewGuid(), continuation, default);

        Assert.True(waiting.IsSuccess, waiting.Error.Message);

        GuildContinueRequest answer = new(Guid.NewGuid(), waiting.Value.Quest.Revision, continuation, "text/plain", "The answer");

        long contents = await f.CountAsync("guild_content");

        await f.ExecuteAsync($"CREATE TRIGGER fixture_answer_fault BEFORE {operation} ON \"{table}\" BEGIN SELECT RAISE(ABORT,'fixture answer fault'); END;");

        var failed = await f.Store.AcceptContinuationAsync(f.Caller, row.Quest.Id, answer, default);

        Assert.True(failed.IsFailure);

        Assert.Equal(contents, await f.CountAsync("guild_content"));

        Assert.Equal(0L, await f.CountAsync("guild_continuations"));

        Assert.Equal(QuestState.InputRequired, (await f.Store.GetAsync(f.Caller, row.Quest.Id, default)).Value.Quest.State);

        await f.ExecuteAsync("DROP TRIGGER fixture_answer_fault");

        Assert.True((await f.Store.AcceptContinuationAsync(f.Caller, row.Quest.Id, answer, default)).IsSuccess);
    }

    [Fact]
    public async Task An_unoffered_continuation_is_refused_without_creating_content()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored row = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        var result = await f.Store.AcceptContinuationAsync(f.Caller, row.Quest.Id,
            new(Guid.NewGuid(), row.Quest.Revision, Guid.NewGuid(), "text/plain", "Unsolicited answer"), default);

        Assert.True(result.IsFailure);

        Assert.Equal(0L, await f.CountAsync("guild_continuations"));

        Assert.Equal(2L, await f.CountAsync("guild_content"));
    }

    [Fact]
    public async Task A_completed_parent_cannot_admit_a_fresh_child_using_a_retained_signet()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored row = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildQuestDriver driver = await f.DriverAsync(row);

        await f.ExecuteAsync($"UPDATE LongRunningOperations SET State=4,Revision=Revision+1 WHERE Id='{row.Quest.OperationId:N}'");

        GuildCallerContext caller = new(GuildCallerKind.Run, f.Caller.PrincipalId!.Value, Guid.NewGuid(), f.Host.InstallationGeneration, driver.Execution);

        Guid key = Guid.NewGuid();

        var child = await f.Store.IssueAsync(caller, new(f.Host.InstallationGeneration, row.Quest.Id, null, Guid.NewGuid(), key),
            row.Quest.Id, new(key, row.Quest.Revision, null, "Child", "Do more work", new()), default);

        Assert.True(child.IsFailure, "A terminal parent must be closed to new descendants even while its credential remains live.");

        Assert.Equal(1L, await f.CountAsync("guild_quests"));
    }
}
