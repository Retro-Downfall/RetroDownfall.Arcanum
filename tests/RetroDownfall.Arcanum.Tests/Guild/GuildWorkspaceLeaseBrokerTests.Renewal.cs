using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildWorkspaceLeaseBrokerTests
{
    [Fact]
    public async Task Injected_timer_renews_every_five_minutes_until_host_cancellation()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var acquired = await f.AcquireAsync("a.txt");

        Assert.True(acquired.IsSuccess, acquired.Error.Message);

        using CancellationTokenSource stop = new();

        GuildWorkspaceLeaseRenewer renewer = new(f.Broker, f.Time);

        Task running = renewer.RunAsync(stop.Token);

        try
        {
            await f.Time.WhenArmedTimersAsync(count => count == 1, TimeSpan.FromSeconds(5));

            for (int tick = 0; tick < 4; tick++)
            {
                Assert.Equal(TimeSpan.FromMinutes(5), f.Time.NextDueIn);

                f.Time.Advance(TimeSpan.FromMinutes(5));

                await f.Time.WhenArmedTimersAsync(count => count == 1, TimeSpan.FromSeconds(5));

                var page = await f.Broker.ListAsync(f.Policy.Caller, f.Policy.WorkspaceId, null, null, new(PageSize: 10), default);

                Assert.True(page.IsSuccess, page.Error.Message);

                Assert.Equal(f.Time.GetUtcNow().AddMinutes(15), Assert.Single(page.Value.Items).ExpiresAt);
            }
        }
        finally
        {
            stop.Cancel();

            await running;
        }

        Assert.Equal(0, f.Time.ArmedTimers);
    }

    [Fact]
    public async Task Heartbeats_keep_a_writer_live_beyond_the_nominal_duration()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var lease = await f.AcquireAsync("a.txt");

        Assert.True(lease.IsSuccess, lease.Error.Message);

        var admitted = await f.Broker.AdmitMutationAsync(f.Policy.Authority, f.Plan("a.txt"), default);

        Assert.True(admitted.IsSuccess, admitted.Error.Message);

        try
        {
            for (int interval = 0; interval < 20; interval++)
            {
                f.Time.Advance(TimeSpan.FromMinutes(5));

                Assert.True((await f.Broker.RenewHeldAsync(default)).IsSuccess);

                Assert.True((await f.Broker.ValidateCommitAsync(admitted.Value, default)).IsSuccess);
            }
        }
        finally
        {
            Assert.True((await f.Broker.CompleteMutationAsync(admitted.Value, default)).IsSuccess);
        }
    }

    [Fact]
    public async Task Explicit_renew_rejects_wrong_epoch_and_revision_without_changing_ownership()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var lease = await f.AcquireAsync("a.txt");

        Assert.True(lease.IsSuccess, lease.Error.Message);

        Guid key = Guid.NewGuid();

        var wrong = await f.Broker.RenewAsync(f.Policy.Caller, new(key, new string('A', 64)), lease.Value.Id,
            new(key, lease.Value.Revision, lease.Value.Epoch + 1), default);

        Assert.True(wrong.IsFailure);

        f.Time.Advance(TimeSpan.FromMinutes(5));

        var renewed = await f.Broker.RenewAsync(f.Policy.Caller, new(key, new string('A', 64)), lease.Value.Id,
            new(key, lease.Value.Revision, lease.Value.Epoch), default);

        Assert.True(renewed.IsSuccess, renewed.Error.Message);

        Assert.Equal(lease.Value.Epoch, renewed.Value.Epoch);

        Assert.Equal(lease.Value.ExpiresAt.AddMinutes(5), renewed.Value.ExpiresAt);

        Guid staleKey = Guid.NewGuid();

        var stale = await f.Broker.ReleaseAsync(f.Policy.Caller, new(staleKey, new string('B', 64)), renewed.Value.Id,
            new(staleKey, lease.Value.Revision, lease.Value.Epoch), default);

        Assert.Equal(ErrorCodes.Guild.RevisionConflict, stale.Error.Code);

        Assert.Equal(ErrorCodes.Guild.LeaseConflict, (await f.AcquireAsync("a.txt")).Error.Code);
    }

    [Fact]
    public async Task Expired_explicit_lease_cannot_be_renewed_or_silently_reacquired_by_heartbeat()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var lease = await f.AcquireAsync("a.txt");

        Assert.True(lease.IsSuccess, lease.Error.Message);

        f.Time.Advance(TimeSpan.FromMinutes(15));

        Guid key = Guid.NewGuid();

        var expired = await f.Broker.RenewAsync(f.Policy.Caller, new(key, new string('A', 64)), lease.Value.Id,
            new(key, lease.Value.Revision, lease.Value.Epoch), default);

        Assert.Equal(ErrorCodes.Guild.LeaseLapsed, expired.Error.Code);

        Assert.True((await f.Broker.RenewHeldAsync(default)).IsSuccess);

        var replacement = await f.AcquireAsync("a.txt");

        Assert.True(replacement.IsSuccess, replacement.Error.Message);

        Assert.NotEqual(lease.Value.Id, replacement.Value.Id);
    }

    [Fact]
    public async Task List_reports_owned_live_and_terminal_leases_and_rejects_copied_caller()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var first = await f.AcquireAsync("a.txt");

        var second = await f.AcquireAsync("b.txt");

        Assert.True(first.IsSuccess && second.IsSuccess);

        Guid key = Guid.NewGuid();

        Assert.True((await f.Broker.ReleaseAsync(f.Policy.Caller, new(key, new string('A', 64)), first.Value.Id,
            new(key, first.Value.Revision, first.Value.Epoch), default)).IsSuccess);

        var page = await f.Broker.ListAsync(f.Policy.Caller, f.Policy.WorkspaceId, null, null, new(PageSize: 10), default);

        Assert.True(page.IsSuccess, page.Error.Message);

        Assert.Equal(2, page.Value.Items.Length);

        Assert.Contains(page.Value.Items, row => row.Id == first.Value.Id && row.State == GuildWorkspaceLeaseState.Released);

        GuildCallerContext caller = f.Policy.Caller;

        GuildCallerContext forged = new(caller.Kind, caller.PrincipalId!.Value, caller.InvocationId, caller.InstallationGeneration);

        var denied = await f.Broker.ListAsync(forged, f.Policy.WorkspaceId, null, null, new(), default);

        Assert.Equal(ErrorCodes.Guild.Forbidden, denied.Error.Code);
    }
}
