using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Conclave;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Fact]
    public async Task Local_wait_checkpoint_survives_restart_and_only_exact_accepted_continuation_can_read_it()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored row = await ReceiptRootAsync(f);

        GuildLocalExecutionBinding binding = await CreateLocalAsync(f, row, LocalInput(row));

        GuildQuestDriver driver = await f.DriverAsync(row);

        Guid dispatchId = Guid.NewGuid();

        GuildQuestDispatchRecord dispatch = (await f.Store.PrepareDispatchAsync(driver, dispatchId,
            ReceiptDigest("wait launch"), Guid.NewGuid(), false, default)).Value;

        Guid question = Guid.NewGuid(), continuation = Guid.NewGuid();

        GuildLocalWaitSnapshot snapshot = WaitSnapshot(binding);

        var waiting = await f.Store.WaitForInputAsync(driver, question, continuation,
            new(dispatchId, snapshot), default);

        Assert.True(waiting.IsSuccess, waiting.Error.Message);

        Assert.Equal(1L, await f.CountAsync("guild_local_wait_checkpoints"));

        Assert.Equal(snapshot.InputDigest, binding.Input.Digest);

        Assert.True((await f.Store.WaitForInputAsync(driver, question, continuation, new(dispatchId, snapshot), default)).IsSuccess);

        Assert.True((await f.Store.WaitForInputAsync(driver, question, continuation,
            new(dispatchId, snapshot with { CurrentStep = 0 }), default)).IsFailure);

        await Assert.ThrowsAsync<SqliteException>(() => f.ExecuteAsync("UPDATE guild_local_wait_checkpoints SET ByteLength=1"));

        Assert.True((await f.Store.ReadWaitCheckpointAsync(new(f.Store, driver, dispatch), continuation, default)).IsFailure);

        var accepted = await f.Store.AcceptContinuationAsync(f.Caller, row.Quest.Id,
            new(Guid.NewGuid(), waiting.Value.Quest.Revision, continuation, "text/plain", "Use the first option"), default);

        Assert.True(accepted.IsSuccess, accepted.Error.Message);

        f.Restart();

        var rebound = await f.Authority.RebindAsync(new(row.Quest.Id, row.Quest.OperationId, accepted.Value.Quest.SignetRevision), default);

        Assert.True(rebound.IsSuccess, rebound.Error.Message);

        driver = driver with { Execution = rebound.Value, OperationRevision = driver.OperationRevision + 2 };

        var preparedContinuation = await f.Store.PrepareDispatchAsync(driver, continuation,
            ReceiptDigest("wait answer"), Guid.NewGuid(), true, default);

        Assert.True(preparedContinuation.IsSuccess, preparedContinuation.Error.Message);

        GuildQuestDispatchRecord resumed = preparedContinuation.Value;

        GuildLocalInferenceExecution execution = new(f.Store, driver, resumed);

        var loaded = await f.Store.ReadWaitCheckpointAsync(execution, continuation, default);

        Assert.True(loaded.IsSuccess, loaded.Error.Message);

        Assert.Equal(1, loaded.Value.CurrentStep);

        Assert.Equal("already paid output", loaded.Value.Plan[0].Result);

        Assert.Equal("Choose an option", loaded.Value.Checkpoint.EscalationReason);

        Assert.True((await f.Store.ReadWaitCheckpointAsync(execution, Guid.NewGuid(), default)).IsFailure);

        await using SqliteConnection connection = await f.OpenAsync();

        await using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            await using SqliteCommand erase = connection.CreateCommand();

            erase.Transaction = transaction;

            erase.CommandText = "SELECT ContentId FROM guild_local_wait_checkpoints";

            Guid contentId = Guid.Parse((string)(await erase.ExecuteScalarAsync())!);

            await GuildContentLifecycle.PrepareOrdinaryPurgeAsync(connection, transaction, contentId.ToString("N"), default);

            erase.CommandText = "DELETE FROM guild_content WHERE ContentId=$id";

            erase.Parameters.AddWithValue("$id", contentId.ToString("D").ToUpperInvariant());

            Assert.Equal(1, await erase.ExecuteNonQueryAsync());

            transaction.Commit();
        }

        Assert.True((await f.Store.ReadWaitCheckpointAsync(execution, continuation, default)).IsFailure);

        Assert.Equal(1L, await f.CountAsync("guild_local_wait_checkpoints"));
    }

    [Fact]
    public async Task Local_wait_checkpoint_refuses_uncertain_dispatch_without_persisting_snapshot_or_wait()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored row = await ReceiptRootAsync(f);

        GuildLocalExecutionBinding binding = await CreateLocalAsync(f, row, LocalInput(row));

        GuildQuestDriver driver = await f.DriverAsync(row);

        GuildQuestDispatchRecord dispatch = (await f.Store.PrepareDispatchAsync(driver, Guid.NewGuid(),
            ReceiptDigest("uncertain wait"), Guid.NewGuid(), false, default)).Value;

        Assert.True((await f.Store.MarkPossiblyDispatchedAsync(driver, dispatch.DispatchId, dispatch.Revision, default)).IsSuccess);

        var waiting = await f.Store.WaitForInputAsync(driver, Guid.NewGuid(), Guid.NewGuid(),
            new(dispatch.DispatchId, WaitSnapshot(binding)), default);

        Assert.True(waiting.IsFailure);

        Assert.Equal(0L, await f.CountAsync("guild_local_wait_checkpoints"));

        Assert.Equal(QuestState.Working, (await f.Store.GetAsync(f.Caller, row.Quest.Id, default)).Value.Quest.State);
    }

    private static GuildLocalWaitSnapshot WaitSnapshot(GuildLocalExecutionBinding binding) => new(1,
        binding.Input.Digest, 1, [new() { Index = 0, Description = "Complete work", Status = "completed", Result = "already paid output" },
            new() { Index = 1, Description = "Select next option", Status = "pending" }],
        new() { CurrentStep = 1, EscalationReason = "Choose an option", LaunchRequested = true });
}
