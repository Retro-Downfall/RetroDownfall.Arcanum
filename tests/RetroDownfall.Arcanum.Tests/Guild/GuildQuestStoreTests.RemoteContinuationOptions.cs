using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.A2A;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Fact]
    public async Task Remote_continuation_receipt_conflicts_on_changed_mode_skill_or_output_negotiation()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var setup = await RemoteContinuationAsync(f);

        GuildContinueRequest request = new(Guid.NewGuid(), setup.Waiting.Quest.Revision, setup.Continuation,
            "text/plain", "Answer", new(GuildRemoteDispatchMode.Continuable, ["text/plain"], "skill-one"));

        var first = await f.Store.AcceptContinuationAsync(f.Caller, setup.Waiting.Quest.Id, request, default);

        Assert.True(first.IsSuccess, first.Error.Message);

        foreach (GuildRemoteInvocationOptions changed in new[]
        {
            new GuildRemoteInvocationOptions(GuildRemoteDispatchMode.Blocking, ["text/plain"], "skill-one"),
            new GuildRemoteInvocationOptions(GuildRemoteDispatchMode.Continuable, ["application/json"], "skill-one"),
            new GuildRemoteInvocationOptions(GuildRemoteDispatchMode.Continuable, ["text/plain"], "skill-two"),
        })
        {
            var conflict = await f.Store.AcceptContinuationAsync(f.Caller, setup.Waiting.Quest.Id,
                request with { RemoteOptions = changed }, default);

            Assert.Equal(ErrorCodes.Guild.IdempotencyConflict, conflict.Error.Code);
        }
    }

    [Fact]
    public async Task Remote_continuation_options_rehydrate_the_exact_accepted_transport_preferences()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var setup = await RemoteContinuationAsync(f);

        GuildRemoteInvocationOptions options = new(GuildRemoteDispatchMode.Continuable, ["text/plain", "application/json"], "chosen-skill");

        var accepted = await f.Store.AcceptContinuationAsync(f.Caller, setup.Waiting.Quest.Id,
            new(Guid.NewGuid(), setup.Waiting.Quest.Revision, setup.Continuation, "text/plain", "Answer", options), default);

        Assert.True(accepted.IsSuccess, accepted.Error.Message);

        f.Restart();

        await using SqliteConnection connection = await f.OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction();

        var read = await GuildQuestStore.ReadRemoteContinuationOptionsWithinAsync(connection, transaction,
            setup.Waiting.Quest.Id, setup.Continuation, default);

        Assert.True(read.IsSuccess, read.Error.Message);

        Assert.NotNull(read.Value);

        Assert.Equal(GuildRemoteDispatchMode.Continuable, read.Value.Mode);

        Assert.Equal("chosen-skill", read.Value.SkillId);

        Assert.Equal(["text/plain", "application/json"], read.Value.AcceptedOutputModes.ToArray());
    }

    private static async Task<(GuildQuestStored Waiting, Guid Continuation)> RemoteContinuationAsync(Fixture f)
    {
        GuildQuestStored root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        await using (SqliteConnection connection = await f.OpenAsync())
        {
            await using SqliteTransaction transaction = connection.BeginTransaction();

            GuildRemoteExecutionInput input = new(1, "https://remote.example/", new("peer", "https://remote.example/a2a", "1.0",
                ["JSONRPC"], [], null, null, null), A2ADispatchMode.Continuable, ["text/plain"], null, []);

            var binding = await GuildRemoteExecutionStore.CreateWithinAsync(connection, transaction, root, input, null,
                DateTimeOffset.UtcNow, default);

            Assert.True(binding.IsSuccess, binding.Error.Message);

            await transaction.CommitAsync();
        }

        GuildQuestDriver driver = await f.DriverAsync(root);

        Guid continuation = Guid.NewGuid();

        var waiting = await f.Store.WaitForInputAsync(driver, Guid.NewGuid(), continuation, default);

        Assert.True(waiting.IsSuccess, waiting.Error.Message);

        return (waiting.Value, continuation);
    }
}
