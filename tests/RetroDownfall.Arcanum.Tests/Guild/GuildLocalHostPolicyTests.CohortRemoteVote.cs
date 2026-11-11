using System.Net;
using A2A;
using A2A.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Infrastructure.A2A;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cohort_remote_quest_records_only_one_exact_designated_final_artifact(bool ambiguous)
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        f.Settings.CurrentValue.Features.Conclave = true;

        f.Settings.CurrentValue.Features.A2AClient = true;

        f.Settings.CurrentValue.Integrations.A2A.AllowedRemoteAgents = ["http://remote-port.example/"];

        GuildRemoteTransportDescriptor target = new("remote-port", "http://remote-port.example/agent", "1.0", ["JSONRPC"], [], null, null, null);

        GuildAgentCard card = new(Guid.Empty, Guid.Empty, 0, "Remote", "Remote peer", "1", [], ["text/plain"], ["text/plain"],
            new(false, false, false), new(GuildAgentSourceKind.Remote, null, target.PeerId, true, null));

        GuildStore roster = new(f.Connections, f.Gate, TimeProvider.System);

        var stored = await roster.UpsertCardAsync(new("remote-port", null, null, null,
            new(card, "", [], target, "http://remote-port.example/"), new string('A', 64)), default);

        Assert.True(stored.IsSuccess, stored.Error.Message);

        GuildCallerContext owner = await f.OperatorAsync();

        var root = await f.IssueAsync(owner, f.Request with { Grant = f.Request.Grant with
        {
            Delegation = [GuildDelegationKind.Local, GuildDelegationKind.Remote, GuildDelegationKind.Cohort],
        } });

        Assert.True(root.IsSuccess, root.Error.Message);

        GuildCallerContext issuer = await BindCohortRunAsync(f, owner, root.Value);

        GuildCohortStore cohorts = new(new(f.Connections, f.Gate, TimeProvider.System), f.Authority, f.Store,
            new GuildBudgetLedger(), f.Policy, TimeProvider.System);

        var cohort = await cohorts.CreateAsync(issuer, new(Guid.NewGuid(), root.Value.Quest.Id, "Remote vote",
            new(GuildConvergenceKind.First, null), new("text/plain"), null), default);

        Assert.True(cohort.IsSuccess, cohort.Error.Message);

        Guid key = Guid.NewGuid();

        var member = await f.Store.IssueCohortMemberAsync(issuer, new(f.Generation, root.Value.Quest.Id, null, key, key),
            cohort.Value.Id, cohort.Value.Revision,
            new(key, root.Value.Quest.Revision, stored.Value.Payload.Card.AgentId, "Remote member", "Contribute", new(),
                new(GuildRemoteDispatchMode.Blocking)), default);

        Assert.True(member.IsSuccess, member.Error.Message);

        using IHost peer = await new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();

            web.ConfigureServices(services => services.AddRouting().AddAdHocHttpJson());

            web.Configure(app =>
            {
                app.UseRouting();

                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapWellKnownAgentCard(new AgentCard { Name = "Remote", Description = "Peer", Version = "1",
                        SupportedInterfaces = [new() { Url = target.Endpoint, ProtocolBinding = "JSONRPC", ProtocolVersion = "1.0" }],
                        Capabilities = new(), DefaultInputModes = ["text/plain"], DefaultOutputModes = ["text/plain"] });

                    endpoints.MapA2A(new A2AServer(new CohortRemoteFinalAgent(ambiguous), new InMemoryTaskStore(), new ChannelEventNotifier(),
                        NullLogger<A2AServer>.Instance, new A2AServerOptions()), "/agent");
                });
            });
        }).StartAsync();

        using HttpMessageHandler handler = peer.GetTestServer().CreateHandler();

        FakeDnsResolver dns = new();

        dns.Add("remote-port.example", IPAddress.Parse("93.184.216.34"));

        A2AClientService client = new(new RemotePortHttp(handler), f.Settings, NullLogger<A2AClientService>.Instance, dns);

        var opened = await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

        Assert.True(opened.IsSuccess, opened.Error.Message);

        await using var connection = opened.Value;

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection.Connection);

        LongRunningOperationStore operations = new(db, f.Connections);

        GuildRemotePort port = new(f.Store, f.Policy, f.Authority, operations, client, f.Gate, new(), TimeProvider.System);

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));

        await DriveRemoteToolChildAsync(f, owner, port, member.Value.Quest.Id, deadline.Token);

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_result_receipts"));

        Assert.Equal(1L, await f.ScalarAsync($"SELECT count(*) FROM guild_cohort_contributions WHERE OutcomeCode={(ambiguous ? 6 : 1)}"));

        Assert.Equal(ambiguous ? 0L : 1L, await f.ScalarAsync("""
            SELECT count(*) FROM guild_cohort_contributions v JOIN guild_content c ON c.ContentId=v.ContentId
            WHERE CAST(c.Body AS TEXT)='Remote exact answer'
            """));

        var inspectable = await f.Store.ReadLocalResultAsync(owner, member.Value.Quest.Id, true, default);

        Assert.True(inspectable.IsSuccess, inspectable.Error.Message);

        Assert.Contains("Remote", inspectable.Value.Body);
    }

    private sealed class CohortRemoteFinalAgent(bool ambiguous) : IAgentHandler
    {
        public async Task ExecuteAsync(RequestContext context, AgentEventQueue queue, CancellationToken ct)
        {
            TaskUpdater updater = new(queue, context.TaskId, context.ContextId);

            await updater.SubmitAsync(ct);

            await updater.StartWorkAsync(cancellationToken: ct);

            await updater.AddArtifactAsync([Part.FromText("Remote exact answer")], cancellationToken: ct);

            if (ambiguous) await updater.AddArtifactAsync([Part.FromText("Remote other artifact")], cancellationToken: ct);

            await updater.CompleteAsync(cancellationToken: ct);
        }

        public Task CancelAsync(RequestContext context, AgentEventQueue queue, CancellationToken ct) => Task.CompletedTask;
    }
}
