using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildWorkspaceLeaseBrokerTests
{
    [Fact]
    public async Task Operator_management_release_drains_quest_writer_without_adopting_ownership()
    {
        await using Fixture f = await Fixture.CreateAsync();

        Guid acquireKey = Guid.NewGuid();

        Result<WorkspaceLeaseDto> acquired = await f.Broker.AcquireAsync(f.Policy.RunCaller,
            new(acquireKey, new string('A', 64)), new(acquireKey, f.Policy.WorkspaceId, [new("a.txt", GuildPathScopeKind.File)]), default);

        Assert.True(acquired.IsSuccess, acquired.Error.Message);

        Result<GuildMutationPermit> permit = await f.Broker.AdmitMutationAsync(f.Policy.RunAuthority, f.Plan("a.txt"), default);

        Assert.True(permit.IsSuccess, permit.Error.Message);

        Result<WorkspaceLeasePageDto> page = await f.Broker.ListAsync(f.Policy.RunCaller, f.Policy.WorkspaceId, null, null, new(null, 1), default);

        Assert.True(page.IsSuccess, page.Error.Message);

        WorkspaceLeaseDto current = Assert.Single(page.Value.Items);

        Guid renewKey = Guid.NewGuid();

        Assert.Equal(ErrorCodes.Guild.Forbidden, (await f.Broker.RenewAsync(f.Policy.Caller,
            new(renewKey, new string('B', 64)), current.Id, new(renewKey, current.Revision, current.Epoch), default)).Error.Code);

        Guid key = Guid.NewGuid();

        GuildMutationIdentity mutation = new(key, new string('C', 64));

        ReleaseWorkspaceLeaseRequest request = new(key, current.Revision, current.Epoch);

        Result<WorkspaceLeaseDto> released = await f.Broker.ReleaseAsync(f.Policy.Caller, mutation, current.Id, request, default);

        Assert.True(released.IsSuccess, released.Error.Message);

        Assert.Equal(GuildWorkspaceLeaseOwnerKind.Quest, released.Value.OwnerKind);

        Assert.Equal(f.Policy.RunCaller.Execution!.QuestId, released.Value.QuestId);

        Assert.Equal(GuildWorkspaceLeaseState.Draining, released.Value.State);

        Assert.Equal(1, released.Value.InFlightMutationCount);

        Assert.Equal(ErrorCodes.Guild.LeaseConflict, (await f.AcquireAsync("a.txt")).Error.Code);

        Assert.True((await f.Broker.CompleteMutationAsync(permit.Value, default)).IsSuccess);

        Result<WorkspaceLeaseDto> replay = await f.Broker.ReleaseAsync(f.Policy.Caller, mutation, current.Id, request, default);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.True(replay.Value.Receipt!.Replayed);

        Assert.Equal(released.Value.Receipt!.Id, replay.Value.Receipt.Id);

        Assert.Equal(GuildWorkspaceLeaseState.Draining, replay.Value.State);

        Assert.True((await f.AcquireAsync("a.txt")).IsSuccess);
    }
}
