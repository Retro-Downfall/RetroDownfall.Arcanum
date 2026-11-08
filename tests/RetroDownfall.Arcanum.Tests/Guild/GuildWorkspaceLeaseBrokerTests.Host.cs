using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildWorkspaceLeaseBrokerTests
{
    [Fact]
    public async Task No_lease_or_mutation_can_be_admitted_after_the_host_lock_closes()
    {
        await using Fixture f = await Fixture.CreateAsync();

        f.Host.Dispose();

        Assert.True((await f.AcquireAsync("a.txt")).IsFailure);

        Assert.True((await f.Broker.AdmitMutationAsync(f.Policy.Authority, f.Plan("b.txt"), default)).IsFailure);
    }

    [Fact]
    public async Task Active_mutation_pins_the_host_lock_even_when_its_owner_closes()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var admitted = await f.Broker.AdmitMutationAsync(f.Policy.Authority, f.Plan("a.txt"), default);

        Assert.True(admitted.IsSuccess, admitted.Error.Message);

        try
        {
            f.Host.Dispose();

            var contender = GuildWorkspaceLeaseHostLock.TryAcquire(f.HostLockPath);

            if (contender.IsSuccess) contender.Value.Dispose();

            Assert.True(contender.IsFailure);

            Assert.True((await f.Broker.ValidateCommitAsync(admitted.Value, default)).IsFailure);
        }
        finally
        {
            Assert.True((await f.Broker.CompleteMutationAsync(admitted.Value, default)).IsSuccess);
        }

        var next = GuildWorkspaceLeaseHostLock.TryAcquire(f.HostLockPath);

        Assert.True(next.IsSuccess, next.Error.Message);

        next.Value.Dispose();
    }

    [Fact]
    public async Task Broker_disposal_closes_admission_but_releases_the_lock_only_after_io_drains()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var admitted = await f.Broker.AdmitMutationAsync(f.Policy.Authority, f.Plan("a.txt"), default);

        Assert.True(admitted.IsSuccess, admitted.Error.Message);

        await f.Broker.DisposeAsync();

        Assert.True((await f.AcquireAsync("b.txt")).IsFailure);

        var premature = GuildWorkspaceLeaseHostLock.TryAcquire(f.HostLockPath);

        if (premature.IsSuccess) premature.Value.Dispose();

        Assert.True(premature.IsFailure);

        Assert.True((await f.Broker.CompleteMutationAsync(admitted.Value, default)).IsSuccess);

        var next = GuildWorkspaceLeaseHostLock.TryAcquire(f.HostLockPath);

        Assert.True(next.IsSuccess, next.Error.Message);

        next.Value.Dispose();
    }
}
