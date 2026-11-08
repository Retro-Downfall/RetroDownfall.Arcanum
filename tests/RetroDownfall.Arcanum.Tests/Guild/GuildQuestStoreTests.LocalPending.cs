using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Fact]
    public async Task Local_pending_uses_only_committed_start_input_and_never_mints_a_child()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored row = await ReceiptRootAsync(f);

        GuildLocalExecutionBinding binding = await CreateLocalAsync(f, row, LocalInput(row));

        var pending = await f.Store.ReadLocalPendingAsync(f.Caller, row.Quest.Id, default);

        Assert.True(pending.IsSuccess, pending.Error.Message);

        Assert.False(pending.Value.RequiresAttention);

        Assert.NotNull(pending.Value.Request);

        Assert.Equal(binding.Input.Digest, pending.Value.Request.InvocationDigest);

        Assert.Equal(row.Quest.Id, pending.Value.Request.DispatchId);

        Assert.Equal(1L, await f.CountAsync("Apprentices"));

        Assert.Equal(1L, await f.CountAsync("LongRunningOperations"));
    }

    [Theory]
    [InlineData("Guild.ReconciliationRequired", true)]
    [InlineData("Guild.LocalExecutionInterrupted", false)]
    [InlineData("unknown-owner-reason", false)]
    public async Task Local_pending_reopens_only_proven_generic_recovery_with_current_authority(string reason, bool allowed)
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored row = await ReceiptRootAsync(f);

        _ = await CreateLocalAsync(f, row, LocalInput(row));

        await f.ExecuteAsync($"UPDATE LongRunningOperations SET State=7,TerminalErrorCode='{reason}',Revision=Revision+1 WHERE Id='{row.Quest.OperationId:N}'");

        var pending = await f.Store.ReadLocalPendingAsync(f.Caller, row.Quest.Id, default);

        Assert.True(pending.IsSuccess, pending.Error.Message);

        Assert.Equal(!allowed, pending.Value.RequiresAttention);

        var rebound = await f.Authority.RebindAsync(new(row.Quest.Id, row.Quest.OperationId, row.Quest.SignetRevision), default);

        Assert.True(rebound.IsSuccess, rebound.Error.Message);

        var prepared = await f.Store.PrepareLocalScheduleAsync(f.Caller, rebound.Value, pending.Value.Operation.Revision, default);

        Assert.Equal(allowed, prepared.IsSuccess);

        Assert.Equal(0L, await f.CountAsync("guild_dispatches"));

        Assert.Equal(1L, await f.CountAsync("Apprentices"));
    }

    [Fact]
    public async Task Local_pending_never_resumes_possibly_dispatched_work()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored row = await ReceiptRootAsync(f);

        _ = await CreateLocalAsync(f, row, LocalInput(row));

        GuildQuestDriver driver = await f.DriverAsync(row);

        var dispatch = await f.Store.PrepareDispatchAsync(driver, row.Quest.Id, ReceiptDigest("uncertain pending"), row.Quest.RunId, false, default);

        Assert.True(dispatch.IsSuccess, dispatch.Error.Message);

        Assert.True((await f.Store.MarkPossiblyDispatchedAsync(driver, row.Quest.Id, dispatch.Value.Revision, default)).IsSuccess);

        var pending = await f.Store.ReadLocalPendingAsync(f.Caller, row.Quest.Id, default);

        Assert.True(pending.IsSuccess, pending.Error.Message);

        Assert.True(pending.Value.RequiresAttention);

        Assert.Null(pending.Value.Request);
    }
}
