using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Theory]
    [InlineData("quest")]
    [InlineData("version-zero")]
    [InlineData("version-future")]
    [InlineData("missing-payload")]
    [InlineData("missing-reference")]
    [InlineData("other-reference")]
    [InlineData("noncanonical-reference")]
    [InlineData("policy")]
    public async Task Read_linkage_refuses_a_checkpoint_or_policy_that_no_longer_names_the_accepted_quest(string corruption)
    {
        await using Fixture f = await Fixture.CreateAsync();

        var accepted = await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default);

        Assert.True(accepted.IsSuccess, accepted.Error.Message);

        await using (SqliteConnection connection = await f.OpenAsync())
        {
            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = corruption switch
            {
                "quest" => "UPDATE LongRunningOperations SET CheckpointPayload=$value",
                "version-zero" => "UPDATE LongRunningOperations SET CheckpointVersion=0",
                "version-future" => "UPDATE LongRunningOperations SET CheckpointVersion=2",
                "missing-payload" => "UPDATE LongRunningOperations SET CheckpointPayload=NULL",
                "missing-reference" => "UPDATE LongRunningOperations SET CheckpointReference=NULL",
                "other-reference" or "noncanonical-reference" => "UPDATE LongRunningOperations SET CheckpointReference=$value",
                "policy" => "UPDATE LongRunningOperations SET RecoveryPolicy=3",
                _ => throw new InvalidOperationException()
            };

            if (corruption == "quest")
                command.Parameters.AddWithValue("$value", GuildQuestCheckpoint.Encode(Guid.NewGuid()));
            else if (corruption == "other-reference")
                command.Parameters.AddWithValue("$value", Guid.NewGuid().ToString("D").ToUpperInvariant());
            else if (corruption == "noncanonical-reference")
                command.Parameters.AddWithValue("$value", accepted.Value.Quest.Id.ToString("N"));

            _ = await command.ExecuteNonQueryAsync();
        }

        var read = await f.Store.GetAsync(f.Caller, accepted.Value.Quest.Id, default);

        Assert.Equal(ErrorCodes.Guild.Unavailable, read.Error.Code);

        var replay = await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default);

        Assert.Equal(ErrorCodes.Guild.Unavailable, replay.Error.Code);

        Assert.Equal(1, f.Host.Resolutions);

        Assert.Equal(1L, await f.CountAsync("fixture_allocations"));
    }

    [Theory]
    [InlineData("Name", false)]
    [InlineData("GoalMimeType", false)]
    [InlineData("IntentDigest", true)]
    [InlineData("ResolutionDigest", true)]
    [InlineData("GoalDigest", true)]
    public async Task Read_corrupt_quest_metadata_is_refused_before_large_values_are_materialized(string column, bool blob)
    {
        await using Fixture f = await Fixture.CreateAsync();

        var accepted = await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default);

        Assert.True(accepted.IsSuccess, accepted.Error.Message);

        await CorruptQuestMetadataAsync(f, "guild_quests", column, blob);

        await AssertBoundedCorruptReadAsync(f, accepted.Value.Quest.Id);
    }

    [Theory]
    [InlineData("ReasonCode", false)]
    [InlineData("MimeType", false)]
    [InlineData("Digest", true)]
    public async Task Read_corrupt_result_metadata_is_refused_before_large_values_are_materialized(string column, bool blob)
    {
        await using Fixture f = await Fixture.CreateAsync();

        var accepted = await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default);

        Assert.True(accepted.IsSuccess, accepted.Error.Message);

        string quest = accepted.Value.Quest.Id.ToString("D").ToUpperInvariant();

        await f.ExecuteAsync($"""
            INSERT INTO guild_result_receipts(QuestId,ResultId,Digest,OutcomeCode,NoDispatchProven,WorkStoppedProven,MutationId,AcceptedRevision,CreatedAtUtc)
            VALUES ('{quest}','AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA',zeroblob(32),3,0,0,'BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB',2,'2026-11-06T12:00:00.0000000Z');
            UPDATE LongRunningOperations SET State=4,TerminalErrorCode=NULL,LeaseOwner=NULL,LeaseExpiresAt=NULL;
            """);

        await CorruptQuestMetadataAsync(f, "guild_result_receipts", column, blob);

        await AssertBoundedCorruptReadAsync(f, accepted.Value.Quest.Id);
    }

    [Fact]
    public async Task Read_corrupt_oversized_checkpoint_is_refused_before_ledger_payload_materialization()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var accepted = await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default);

        Assert.True(accepted.IsSuccess, accepted.Error.Message);

        await f.ExecuteAsync("UPDATE LongRunningOperations SET CheckpointPayload=zeroblob(8388608)");

        await AssertBoundedCorruptReadAsync(f, accepted.Value.Quest.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Read_execution_convergence_preserves_a_surviving_child_outcome_despite_unsettled_accounting(bool uncertainAccounting)
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored parent = await ReceiptRootAsync(f);

        GuildQuestDriver parentDriver = await f.DriverAsync(parent);

        GuildQuestStored child = await f.ChildAsync(parent, parentDriver);

        GuildQuestDriver childDriver = await f.DriverAsync(child, "converging-child");

        var childResult = await f.Store.CommitResultAsync(childDriver, new(Guid.NewGuid(), ReceiptDigest("converging-child")),
            child.Quest.Revision, ReceiptResult(child.Quest.Id) with { NoDispatchProven = true }, default);

        Assert.True(childResult.IsSuccess, childResult.Error.Message);

        var parentResult = await f.Store.CommitResultAsync(parentDriver, new(Guid.NewGuid(), ReceiptDigest("converging-parent")),
            parent.Quest.Revision, ReceiptResult(parent.Quest.Id) with { NoDispatchProven = true }, default);

        Assert.True(parentResult.IsSuccess, parentResult.Error.Message);

        await f.ExecuteAsync("UPDATE guild_result_outbox SET DeliveryStateCode=3,Revision=Revision+1");

        GuildQuestCleanupParticipant cleanup = new(f.Connections, TimeProvider.System);

        GuildPortalCleanupParticipant portalCleanup = new(TimeProvider.System);

        GuildCleanupOwner owner = new(2, parent.Quest.SessionId!.Value, null);

        await using (SqliteConnection connection = await f.OpenAsync())
        {
            await using SqliteTransaction transaction = connection.BeginTransaction();

            Assert.True((await portalCleanup.SealAsync(owner, connection, transaction, default)).IsSuccess);

            Assert.True((await cleanup.SealAsync(owner, connection, transaction, default)).IsSuccess);

            Assert.True((await portalCleanup.DeleteAsync(owner, connection, transaction, default)).IsSuccess);

            Assert.True((await cleanup.DeleteAsync(owner, connection, transaction, default)).IsSuccess);

            await transaction.CommitAsync();
        }

        if (uncertainAccounting)
            await f.ExecuteAsync($"UPDATE guild_quests SET AccountingStateCode=2,Revision=Revision+1 WHERE Id='{child.Quest.Id:D}' COLLATE NOCASE");

        var after = await f.Store.GetAsync(f.Caller, child.Quest.Id, default);

        Assert.True(after.IsSuccess, after.Error.Message);

        Assert.Equal(QuestState.Completed, after.Value.Quest.State);

        Assert.True(after.Value.Quest.CancellationRequested);

        Assert.True(after.Value.Quest.CancellationConverged);

        Assert.Equal(uncertainAccounting ? GuildAccountingState.Uncertain : GuildAccountingState.Settled,
            after.Value.Quest.AccountingState);

        Assert.NotEqual(parent.Quest.SessionId, after.Value.Quest.SessionId);
    }

    private static async Task AssertBoundedCorruptReadAsync(Fixture f, Guid quest)
    {
        await using SqliteConnection connection = await f.OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);

        long before = GC.GetAllocatedBytesForCurrentThread();

        Task<Result<GuildQuestStored>> pending = GuildQuestStore.ReadWithinAsync(connection, transaction, quest, default);

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Microsoft.Data.Sqlite executes this fixture's reads synchronously. Keep the allocation
        // assertion honest if that dependency ever gains asynchronous I/O on these methods.
        Assert.True(pending.IsCompleted, "The allocation probe requires an inline SQLite fixture read.");

        var read = await pending;

        Assert.Equal(ErrorCodes.Guild.Unavailable, read.Error.Code);

        Assert.True(allocated < 4 * 1024 * 1024, $"Corrupt metadata caused {allocated:N0} managed bytes to be materialized.");
    }

    private static async Task CorruptQuestMetadataAsync(Fixture f, string table, string column, bool blob)
    {
        await using SqliteConnection connection = await f.OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction();

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = "SELECT sql FROM sqlite_master WHERE type='trigger' AND name=$name";

        string triggerName = table + "_guard_update";

        command.Parameters.AddWithValue("$name", triggerName);

        string trigger = Assert.IsType<string>(await command.ExecuteScalarAsync());

        command.Parameters.Clear();

        command.CommandText = "PRAGMA ignore_check_constraints=ON; DROP TRIGGER " + triggerName;

        _ = await command.ExecuteNonQueryAsync();

        command.CommandText = "UPDATE " + table + " SET " + column + "=" + (blob ? "zeroblob(8388608)" : "CAST(zeroblob(8388608) AS TEXT)");

        _ = await command.ExecuteNonQueryAsync();

        command.CommandText = trigger;

        _ = await command.ExecuteNonQueryAsync();

        command.CommandText = "PRAGMA ignore_check_constraints=OFF";

        _ = await command.ExecuteNonQueryAsync();

        await transaction.CommitAsync();
    }
}
