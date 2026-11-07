using System.Text;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Theory]
    [InlineData("ProducerAgentId")]
    [InlineData("ProducerRunId")]
    public async Task Retained_parent_continuation_cannot_claim_an_unrelated_agent_or_run(string column)
    {
        await using Fixture f = await Fixture.CreateAsync();

        var accepted = await AcceptIntegrityContinuationAsync(f, true);

        await ReplaceContinuationMetadataAsync(f, column, Guid.NewGuid().ToString("D").ToUpperInvariant());

        var read = await f.Store.GetContinuationAsync(f.Caller, accepted.Quest.Quest.Id, accepted.Request.ContinuationId, default);

        Assert.Equal(ErrorCodes.Guild.Unavailable, read.Error.Code);
    }

    [Theory]
    [InlineData("OperatorPrincipalId", false)]
    [InlineData("OperatorPrincipalId", true)]
    [InlineData("ProducerAgentId", false)]
    [InlineData("ProducerAgentId", true)]
    [InlineData("ProducerRunId", false)]
    [InlineData("ProducerRunId", true)]
    [InlineData("ProducerQuestId", false)]
    [InlineData("ProducerQuestId", true)]
    public async Task Retained_continuation_replay_refuses_oversized_or_wrong_type_provenance_before_materializing(string column, bool wrongType)
    {
        await using Fixture f = await Fixture.CreateAsync();

        var accepted = await AcceptIntegrityContinuationAsync(f, column != "OperatorPrincipalId");

        Guid identity = column switch
        {
            "ProducerAgentId" => accepted.Caller.Execution!.AgentId,
            "ProducerRunId" => accepted.Caller.Execution!.RunId,
            "ProducerQuestId" => accepted.Caller.Execution!.QuestId,
            _ => accepted.Caller.PrincipalId!.Value,
        };

        object corrupt = wrongType ? Encoding.UTF8.GetBytes(identity.ToString("D").ToUpperInvariant()) : new string('X', 8 * 1024 * 1024);

        long baselineBefore = GC.GetAllocatedBytesForCurrentThread();

        Task<Result<GuildQuestStored>> baselinePending = f.Store.AcceptContinuationAsync(accepted.Caller,
            accepted.Quest.Quest.Id, accepted.Request, default);

        long baselineAllocated = GC.GetAllocatedBytesForCurrentThread() - baselineBefore;

        Assert.True(baselinePending.IsCompleted, "The allocation probe requires an inline SQLite fixture replay.");

        Assert.True((await baselinePending).IsSuccess);

        await ReplaceContinuationMetadataAsync(f, column, corrupt);

        long before = GC.GetAllocatedBytesForCurrentThread();

        Task<Result<GuildQuestStored>> pending = f.Store.AcceptContinuationAsync(accepted.Caller,
            accepted.Quest.Quest.Id, accepted.Request, default);

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(pending.IsCompleted, "The allocation probe requires an inline SQLite fixture replay.");

        var replay = await pending;

        Assert.Equal(ErrorCodes.Guild.Unavailable, replay.Error.Code);

        // Both calls pay the admitted API's schema/authority inspection cost. Bound the additional
        // allocation attributable to corrupt metadata, rather than that unrelated fixed overhead.
        Assert.True(allocated - baselineAllocated < 4 * 1024 * 1024,
            $"Clean replay allocated {baselineAllocated:N0}; corrupt replay allocated {allocated:N0}; excess {allocated - baselineAllocated:N0} managed bytes.");

        Assert.Equal(1L, await f.CountAsync("guild_continuations"));
    }

    private static async Task<(GuildQuestStored Quest, GuildCallerContext Caller, GuildContinueRequest Request)> AcceptIntegrityContinuationAsync(Fixture f, bool parentAnswer)
    {
        GuildQuestStored root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildQuestDriver parent = await f.DriverAsync(root);

        GuildQuestStored quest = parentAnswer ? await f.ChildAsync(root, parent) : root;

        GuildCallerContext caller = parentAnswer
            ? new(GuildCallerKind.Run, f.Caller.PrincipalId!.Value, Guid.NewGuid(), f.Host.InstallationGeneration, parent.Execution)
            : f.Caller;

        Guid continuation = Guid.NewGuid();

        var waiting = await f.Store.WaitForInputAsync(parentAnswer ? await f.DriverAsync(quest) : parent, Guid.NewGuid(), continuation, default);

        Assert.True(waiting.IsSuccess, waiting.Error.Message);

        GuildContinueRequest request = new(Guid.NewGuid(), waiting.Value.Quest.Revision, continuation, "text/plain", "Proceed");

        var accepted = await f.Store.AcceptContinuationAsync(caller, quest.Quest.Id, request, default);

        Assert.True(accepted.IsSuccess, accepted.Error.Message);

        return (accepted.Value, caller, request);
    }

    private static async Task ReplaceContinuationMetadataAsync(Fixture f, string column, object value)
    {
        await using SqliteConnection connection = await f.OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction();

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = "SELECT sql FROM sqlite_master WHERE type='trigger' AND name='guild_continuations_guard_update'";

        string trigger = Assert.IsType<string>(await command.ExecuteScalarAsync());

        command.CommandText = "PRAGMA ignore_check_constraints=ON; DROP TRIGGER guild_continuations_guard_update";

        _ = await command.ExecuteNonQueryAsync();

        command.CommandText = "UPDATE guild_continuations SET " + column + "=$value";

        command.Parameters.AddWithValue("$value", value);

        _ = await command.ExecuteNonQueryAsync();

        command.Parameters.Clear();

        command.CommandText = trigger;

        _ = await command.ExecuteNonQueryAsync();

        command.CommandText = "PRAGMA ignore_check_constraints=OFF";

        _ = await command.ExecuteNonQueryAsync();

        await transaction.CommitAsync();
    }
}
