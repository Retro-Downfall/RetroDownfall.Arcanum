using System.Net;
using A2A;
using A2A.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.A2A;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public async Task Qualified_remote_tool_returns_a_question_and_answers_the_same_remote_task_to_completion()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        ConfigureRemote(f);

        f.Settings.CurrentValue.Features.Conclave = true;

        GuildCallerContext owner = await f.OperatorAsync();

        GuildToolGrant dispatch = new(GuildToolSourceKind.InternalMcp, "arcanum", "dispatch_sending");

        GuildToolGrant continuation = new(GuildToolSourceKind.InternalMcp, "arcanum", "continue_sending");

        var parent = await f.IssueAsync(owner, f.Request with { Grant = f.Request.Grant with
            { Tools = [dispatch, continuation], Delegation = [GuildDelegationKind.Remote] } });

        Assert.True(parent.IsSuccess, parent.Error.Message);

        GuildLocalInferenceExecution execution = await ToolExecutionAsync(f, owner, parent.Value);

        var input = await f.Store.ReadLocalInputAsync(execution.Driver, default);

        using RemoteCardHandler discovery = new();

        ChildQueue queue = new();

        ConclaveArchmage coordinator = new(null!, f.Settings, f.Store, queue, RemoteResolver(f, discovery));

        using GuildLocalToolRegistry registry = new(execution, input.Value, coordinator);

        var context = f.Store.CreateToolContext(execution, registry, _ => true);

        var start = await context.AdvertiseAsync(new InertChildTool(dispatch.Name), dispatch, default);

        var answer = await context.AdvertiseAsync(new InertChildTool(continuation.Name), continuation, default);

        Assert.NotNull(start);

        Assert.NotNull(answer);

        RemotePortAgent agent = new("wait");

        using IHost remote = await new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();

            web.ConfigureServices(services => services.AddRouting().AddAdHocHttpJson());

            web.Configure(app =>
            {
                app.UseRouting();

                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapWellKnownAgentCard(new AgentCard { Name = "Configured remote", Description = "Remote fixture", Version = "1",
                        SupportedInterfaces = [new() { Url = RemoteDiscovery + "a2a", ProtocolBinding = "JSONRPC", ProtocolVersion = "1.0" }],
                        Capabilities = new() { Streaming = true }, DefaultInputModes = ["text/plain"], DefaultOutputModes = ["text/plain"] });

                    endpoints.MapA2A(new A2AServer(agent, new InMemoryTaskStore(), new ChannelEventNotifier(),
                        NullLogger<A2AServer>.Instance, new A2AServerOptions()), "/a2a");
                });
            });
        }).StartAsync();

        using HttpMessageHandler transport = remote.GetTestServer().CreateHandler();

        FakeDnsResolver dns = new();

        dns.Add("configured-remote.example", IPAddress.Parse("93.184.216.34"));

        A2AClientService client = new(new RemotePortHttp(transport), f.Settings, NullLogger<A2AClientService>.Instance, dns);

        var opened = await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

        Assert.True(opened.IsSuccess, opened.Error.Message);

        await using var connection = opened.Value;

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection.Connection);

        LongRunningOperationStore operations = new(db, f.Connections);

        GuildRemotePort port = new(f.Store, f.Policy, f.Authority, operations, client, f.Gate, new(), TimeProvider.System);

        GuildQuestDto child;

        using (registry.BeginInvocation(Guid.NewGuid(), Guid.NewGuid(), dispatch))
        {
            using CancellationTokenSource lifetime = new();

            Task<object?> invoked = context.InvokeBoundAsync(start, new() { ["goal"] = "Remote goal", ["agent_url"] = RemoteDiscovery,
                ["continuable"] = true }, lifetime.Token).AsTask();

            Task<GuildQuestDto> pending = queue.Pending.Reader.ReadAsync(lifetime.Token).AsTask();

            try
            {
                Assert.Same(pending, await Task.WhenAny(pending, invoked));

                child = await pending;

                await DriveRemoteToolChildAsync(f, owner, port, child.Id, lifetime.Token);

                var question = Assert.IsType<GuildLocalChildToolResult>(await invoked);

                Assert.Equal(QuestState.InputRequired, question.Quest.State);

                Assert.Contains("Which answer?", question.Summary);

                Assert.Contains($"task_id {child.Id:D}", question.Summary);
            }
            finally
            {
                await JoinRemoteToolTestWorkAsync(lifetime, invoked, pending);
            }
        }

        using (registry.BeginInvocation(Guid.NewGuid(), Guid.NewGuid(), continuation))
        {
            using CancellationTokenSource lifetime = new();

            Task<object?> invoked = context.InvokeBoundAsync(answer, new() { ["task_id"] = child.Id.ToString("D"),
                ["agent_url"] = RemoteDiscovery, ["message"] = "The first answer", ["accepted_output_modes"] = new[] { "text/plain" } }, lifetime.Token).AsTask();

            Task<GuildQuestDto> pending = queue.Pending.Reader.ReadAsync(lifetime.Token).AsTask();

            try
            {
                Assert.Same(pending, await Task.WhenAny(pending, invoked));

                Assert.Equal(child.Id, (await pending).Id);

                await DriveRemoteToolChildAsync(f, owner, port, child.Id, lifetime.Token);

                var completed = Assert.IsType<GuildLocalChildToolResult>(await invoked);

                Assert.Equal(QuestState.Completed, completed.Quest.State);

                Assert.Contains("Remote answer", completed.Summary);
            }
            finally
            {
                await JoinRemoteToolTestWorkAsync(lifetime, invoked, pending);
            }
        }

        Assert.Equal(2, agent.Calls);

        Assert.Single(agent.TaskIds.Distinct(StringComparer.Ordinal));

        Assert.Equal(2L, await f.ScalarAsync("SELECT count(*) FROM guild_quests"));

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_continuations"));

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_result_applications"));
    }

    private static async Task DriveRemoteToolChildAsync(Fixture f, GuildCallerContext owner, GuildRemotePort port, Guid questId, CancellationToken ct)
    {
        var current = await f.Store.GetAsync(owner, questId, ct);

        Assert.True(current.IsSuccess, current.Error.Message);

        var recovery = await f.Policy.CreateRecoveryAsync(owner, questId, current.Value.Quest.OperationId,
            current.Value.Quest.SignetRevision, ct);

        Assert.True(recovery.IsSuccess, recovery.Error.Message);

        var handle = await f.Authority.RebindAsync(recovery.Value, ct);

        Assert.True(handle.IsSuccess, handle.Error.Message);

        var pending = await f.Store.ReadLocalPendingAsync(owner, questId, ct);

        Assert.True(pending.IsSuccess, pending.Error.Message);

        Assert.NotNull(pending.Value.Request);

        var driven = await port.DispatchAsync(handle.Value, pending.Value.Request, ct);

        Assert.True(driven.IsSuccess, driven.Error.Message);
    }
}
