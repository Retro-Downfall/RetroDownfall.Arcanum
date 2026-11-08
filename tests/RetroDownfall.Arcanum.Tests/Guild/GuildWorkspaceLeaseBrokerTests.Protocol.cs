using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildWorkspaceLeaseBrokerTests
{
    [Fact]
    public async Task One_plan_reuses_multiple_covering_leases_and_reserves_uncovered_artifacts_atomically()
    {
        await using Fixture f = await Fixture.CreateAsync();

        Assert.True((await f.AcquireAsync("a.txt")).IsSuccess);

        Assert.True((await f.AcquireAsync("b.txt")).IsSuccess);

        GuildMutationPlan plan = new(f.Policy.WorkspaceId,
            [new("a.txt", GuildPathScopeKind.File), new("b.txt", GuildPathScopeKind.File), new("temp.txt", GuildPathScopeKind.File)], new string('C', 64));

        var admitted = await f.Broker.AdmitMutationAsync(f.Policy.Authority, plan, default);

        Assert.True(admitted.IsSuccess, admitted.Error.Message);

        Assert.True((await f.Broker.ValidateCommitAsync(admitted.Value, default)).IsSuccess);

        Assert.Equal(ErrorCodes.Guild.LeaseConflict, (await f.AcquireAsync("temp.txt")).Error.Code);

        Assert.True((await f.Broker.CompleteMutationAsync(admitted.Value, default)).IsSuccess);

        Assert.True((await f.AcquireAsync("temp.txt")).IsSuccess);

        Assert.Equal(ErrorCodes.Guild.LeaseConflict, (await f.AcquireAsync("a.txt")).Error.Code);
    }

    [Fact]
    public async Task Explicit_renewal_and_release_replay_exact_acceptance_before_stale_revision_checks()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var acquired = await f.AcquireAsync("a.txt");

        Assert.True(acquired.IsSuccess, acquired.Error.Message);

        Guid renewalKey = Guid.NewGuid();

        GuildMutationIdentity renewal = new(renewalKey, new string('C', 64));

        RenewWorkspaceLeaseRequest request = new(renewalKey, acquired.Value.Revision, acquired.Value.Epoch);

        var renewed = await f.Broker.RenewAsync(f.Policy.Caller, renewal, acquired.Value.Id, request, default);

        Assert.True(renewed.IsSuccess, renewed.Error.Message);

        var replay = await f.Broker.RenewAsync(f.Policy.Caller, renewal, acquired.Value.Id, request, default);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.NotNull(renewed.Value.Receipt);

        Assert.True(replay.Value.Receipt!.Replayed);

        Assert.Equal(renewed.Value.Receipt.Id, replay.Value.Receipt.Id);

        Assert.Equal(renewed.Value.ExpiresAt, replay.Value.ExpiresAt);

        Guid releaseKey = Guid.NewGuid();

        GuildMutationIdentity release = new(releaseKey, new string('D', 64));

        ReleaseWorkspaceLeaseRequest releaseRequest = new(releaseKey, renewed.Value.Revision, renewed.Value.Epoch);

        var released = await f.Broker.ReleaseAsync(f.Policy.Caller, release, acquired.Value.Id, releaseRequest, default);

        Assert.True(released.IsSuccess, released.Error.Message);

        var releaseReplay = await f.Broker.ReleaseAsync(f.Policy.Caller, release, acquired.Value.Id, releaseRequest, default);

        Assert.True(releaseReplay.IsSuccess, releaseReplay.Error.Message);

        Assert.True(releaseReplay.Value.Receipt!.Replayed);

        Assert.Equal(released.Value.Receipt!.Id, releaseReplay.Value.Receipt.Id);

        var wrongBody = await f.Broker.ReleaseAsync(f.Policy.Caller, release with { CanonicalRequestDigest = new string('E', 64) },
            acquired.Value.Id, releaseRequest, default);

        Assert.Equal(ErrorCodes.Guild.IdempotencyConflict, wrongBody.Error.Code);
    }
}
