using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.A2A;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Remote_discovery_rechecks_run_authority_at_the_physical_request(bool duringDns, bool revoke)
    {
        await using Fixture fixture = await Fixture.CreateAsync(localExecution: true);

        ConfigureRemote(fixture);

        GuildToolGrant tool = new(GuildToolSourceKind.InternalMcp, "arcanum", "dispatch_sending");

        GuildCallerContext owner = await fixture.OperatorAsync();

        var issued = await fixture.IssueAsync(owner, fixture.Request with
        {
            Grant = fixture.Request.Grant with { Tools = [tool], Delegation = [GuildDelegationKind.Local, GuildDelegationKind.Remote] },
        });

        Assert.True(issued.IsSuccess, issued.Error.Message);

        GuildLocalInferenceExecution execution = await ToolExecutionAsync(fixture, owner, issued.Value);

        using RemoteCardHandler handler = new();

        DiscoveryBarrierDns dns = new(duringDns);

        A2AClientService client = new(new RemoteHttpFactory(handler), fixture.Settings, NullLogger<A2AClientService>.Instance, dns);

        GuildConfiguredRemoteResolver resolver = new(fixture.Policy, new(fixture.Connections, fixture.Gate, TimeProvider.System), client, fixture.Settings, fixture.Authority);

        if (!duringDns && revoke) await RevokeDiscoveryRootAsync(fixture, owner, issued.Value.Quest);

        Task<Core.Primitives.Result<GuildAgentCard>> pending = resolver.ResolveAsync(execution.Driver.Caller!, RemoteDiscovery, default);

        try
        {
            if (duringDns)
            {
                Assert.Same(dns.Entered.Task, await Task.WhenAny(dns.Entered.Task, pending));

                await dns.Entered.Task;

                await RevokeDiscoveryRootAsync(fixture, owner, issued.Value.Quest);
            }
        }
        finally
        {
            dns.Release.TrySetResult();

            await pending;
        }

        var refused = await pending;

        Assert.Equal(!revoke, refused.IsSuccess);

        Assert.Equal(revoke ? 0 : 1, handler.Requests);

        Assert.Equal(revoke ? 0L : 1L, await fixture.ScalarAsync("SELECT count(*) FROM guild_agent_cards WHERE SourceKindCode=3"));

        Assert.Equal(1L, await fixture.ScalarAsync("SELECT count(*) FROM guild_quests"));
    }

    private static async Task RevokeDiscoveryRootAsync(Fixture fixture, GuildCallerContext owner, GuildQuestDto quest)
    {
        Guid key = Guid.NewGuid();

        var current = await fixture.Store.GetAsync(owner, quest.Id, default);

        Assert.True(current.IsSuccess, current.Error.Message);

        var revoked = await fixture.Authority.RevokeAsync(owner, new(key, new string('D', 64)), quest.SignetId,
            new(key, current.Value.Quest.SignetRevision, GuildCancellationReason.OperatorRequested), default);

        Assert.True(revoked.IsSuccess, revoked.Error.Message);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Remote_discovery_rechecks_transport_policy_after_DNS_resolution(bool run, bool dailyBudget)
    {
        await using Fixture fixture = await Fixture.CreateAsync(localExecution: true);

        ConfigureRemote(fixture);

        GuildCallerContext owner = await fixture.OperatorAsync();

        var issued = await fixture.IssueAsync(owner, fixture.Request with
        {
            Grant = fixture.Request.Grant with
            {
                Tools = [new(GuildToolSourceKind.InternalMcp, "arcanum", "dispatch_sending")],
                Delegation = [GuildDelegationKind.Local, GuildDelegationKind.Remote],
            },
        });

        Assert.True(issued.IsSuccess, issued.Error.Message);

        GuildLocalInferenceExecution execution = await ToolExecutionAsync(fixture, owner, issued.Value);

        using RemoteCardHandler handler = new();

        DiscoveryBarrierDns dns = new(true);

        A2AClientService client = new(new RemoteHttpFactory(handler), fixture.Settings, NullLogger<A2AClientService>.Instance, dns);

        GuildConfiguredRemoteResolver resolver = new(fixture.Policy, new(fixture.Connections, fixture.Gate, TimeProvider.System), client, fixture.Settings, fixture.Authority);

        Task<Core.Primitives.Result<GuildAgentCard>> pending = resolver.ResolveAsync(run ? execution.Driver.Caller! : owner, RemoteDiscovery, default);

        try
        {
            Assert.Same(dns.Entered.Task, await Task.WhenAny(dns.Entered.Task, pending));

            await dns.Entered.Task;

            if (dailyBudget)
            {
                fixture.Settings.CurrentValue.Cost.Budget.Enabled = true;

                fixture.Settings.CurrentValue.Cost.Budget.DailyLimitUsd = 1;
            }
            else
            {
                fixture.Settings.CurrentValue.Features.A2AClient = false;
            }
        }
        finally
        {
            dns.Release.TrySetResult();

            await pending;
        }

        var refused = await pending;

        Assert.True(refused.IsFailure);

        Assert.Equal(0, handler.Requests);

        Assert.Equal(0L, await fixture.ScalarAsync("SELECT count(*) FROM guild_agent_cards WHERE SourceKindCode=3"));
    }

    private sealed class DiscoveryBarrierDns(bool block) : IDnsResolver
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IPAddress[]> GetHostAddressesAsync(string host, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();

            if (block) await Release.Task.WaitAsync(cancellationToken);

            return [IPAddress.Parse("93.184.216.34")];
        }
    }
}
