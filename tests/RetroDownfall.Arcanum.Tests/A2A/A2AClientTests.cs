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

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.A2A;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.A2A;

[Collection("OutboundUrlGuardDns")]
public sealed class A2AClientTests : IDisposable
{

    private const string FakeAgentHost = "fake-agent.example.test";

    private const string DiscoveryUrl = $"http://{FakeAgentHost}/";

    private readonly IDnsResolver _originalResolver;

    public A2AClientTests()
    {

        _originalResolver = OutboundUrlGuard.DnsResolver;

        FakeDnsResolver fake = new();

        fake.Add(FakeAgentHost, IPAddress.Parse("93.184.216.34"));

        fake.Add("127.0.0.1", IPAddress.Parse("127.0.0.1"));

        OutboundUrlGuard.DnsResolver = fake;

    }

    public void Dispose()
    {

        OutboundUrlGuard.DnsResolver = _originalResolver;

    }

    private static ArcanumSettings EnabledSettings(int maxExternalTasks = 50, string[]? allowedRemoteAgents = null) => new()
    {
        Conclave = new ConclaveSettings
        {
            Enabled = true,
            A2A = new ConclaveA2ASettings
            {
                Enabled = true,
                ClientEnabled = true,
                MaxExternalTasks = maxExternalTasks,
                AllowedRemoteAgents = allowedRemoteAgents ?? [],
            },
        },
    };

    [Fact]
    public async Task DispatchSendingAsync_Disabled_ReturnsFailureWithoutHttp()
    {

        RecordingHttpHandler handler = new(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        A2AClientService client = CreateClient(handler, new ArcanumSettings());

        Result<A2ADispatchResult> result = await client.DispatchSendingAsync("do the thing", null, DiscoveryUrl);

        Assert.True(result.IsFailure);

        Assert.Equal("Sending.Disabled", result.Error.Code);

        Assert.Empty(handler.Requests);

    }

    [Fact]
    public async Task DispatchSendingAsync_EmptyGoal_ReturnsFailureWithoutHttp()
    {

        RecordingHttpHandler handler = new(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        A2AClientService client = CreateClient(handler, EnabledSettings());

        Result<A2ADispatchResult> result = await client.DispatchSendingAsync("   ", null, DiscoveryUrl);

        Assert.True(result.IsFailure);

        Assert.Empty(handler.Requests);

    }

    [Fact]
    public async Task DispatchSendingAsync_NotInAllowlist_ReturnsFailureWithoutHttp()
    {

        RecordingHttpHandler handler = new(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        A2AClientService client = CreateClient(handler, EnabledSettings(allowedRemoteAgents: ["https://some-other-agent.example.test/"]));

        Result<A2ADispatchResult> result = await client.DispatchSendingAsync("do the thing", null, DiscoveryUrl);

        Assert.True(result.IsFailure);

        Assert.Equal("Sending.AgentNotAllowed", result.Error.Code);

        Assert.Empty(handler.Requests);

    }

    [Fact]
    public async Task DispatchSendingAsync_InAllowlist_ByOrigin_ProceedsPastAllowlistCheck()
    {

        // Allowlisted by origin only (no path); the actual dispatch will still fail past this point
        // because there is no real remote agent listening, but Empty(handler.Requests) would be wrong —
        // this proves the allowlist gate itself passed and a real HTTP attempt was made.
        RecordingHttpHandler handler = new(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));

        A2AClientService client = CreateClient(handler, EnabledSettings(allowedRemoteAgents: [$"http://{FakeAgentHost}"]));

        await client.DispatchSendingAsync("do the thing", null, DiscoveryUrl);

        Assert.NotEmpty(handler.Requests);

    }

    [Fact]
    public async Task DispatchSendingAsync_LoopbackUrl_RejectedBySsrfGuardWithoutHttp()
    {

        RecordingHttpHandler handler = new(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        A2AClientService client = CreateClient(handler, EnabledSettings());

        Result<A2ADispatchResult> result = await client.DispatchSendingAsync("do the thing", null, "http://127.0.0.1/agent");

        Assert.True(result.IsFailure);

        Assert.Equal("Sending.AgentUnreachable", result.Error.Code);

        Assert.Empty(handler.Requests);

    }

    [Fact]
    public async Task DispatchSendingAsync_HappyPath_ReturnsRemoteAgentText()
    {

        using TestServer server = await CreateFakeRemoteAgentServerAsync(new EchoingAgentHandler("The remote agent's answer."));

        using HttpMessageHandler handler = server.CreateHandler();

        A2AClientService client = CreateClient(handler, EnabledSettings());

        Result<A2ADispatchResult> result = await client.DispatchSendingAsync("what is the answer?", "test sending", DiscoveryUrl);

        Assert.True(result.IsSuccess);

        Assert.Equal("The remote agent's answer.", result.Value.ResponseText);

    }

    [Fact]
    public async Task DispatchSendingAsync_RemoteRejects_ReturnsTaskRejected()
    {

        using TestServer server = await CreateFakeRemoteAgentServerAsync(new RejectingAgentHandler("no thanks"));

        using HttpMessageHandler handler = server.CreateHandler();

        A2AClientService client = CreateClient(handler, EnabledSettings());

        Result<A2ADispatchResult> result = await client.DispatchSendingAsync("do the thing", null, DiscoveryUrl);

        Assert.True(result.IsFailure);

        Assert.Equal("Sending.TaskRejected", result.Error.Code);

    }

    [Fact]
    public async Task DispatchSendingAsync_MaxExternalTasksReached_RejectsAndReleasesOnCompletion()
    {

        using GateAgentHandler gateHandler = new();

        using TestServer server = await CreateFakeRemoteAgentServerAsync(gateHandler);

        using HttpMessageHandler handler = server.CreateHandler();

        A2AClientService client = CreateClient(handler, EnabledSettings(maxExternalTasks: 1));

        Task<Result<A2ADispatchResult>> firstCall = client.DispatchSendingAsync("first", null, DiscoveryUrl);

        await gateHandler.WaitUntilEnteredAsync();

        Result<A2ADispatchResult> secondResult = await client.DispatchSendingAsync("second", null, DiscoveryUrl);

        Assert.True(secondResult.IsFailure);

        Assert.Equal("Sending.MaxTasksReached", secondResult.Error.Code);

        gateHandler.Release("first call's answer");

        Result<A2ADispatchResult> firstResult = await firstCall;

        Assert.True(firstResult.IsSuccess);

        // The slot must be released once the in-flight call completes, not leaked.
        Result<A2ADispatchResult> thirdResult = await client.DispatchSendingAsync("third", null, DiscoveryUrl);

        Assert.True(thirdResult.IsSuccess);

    }

    private static A2AClientService CreateClient(HttpMessageHandler handler, ArcanumSettings settings) =>
        new(new FakeHttpClientFactory(handler), new TestOptionsMonitor<ArcanumSettings>(settings), NullLogger<A2AClientService>.Instance);

    private static async Task<TestServer> CreateFakeRemoteAgentServerAsync(IAgentHandler agentHandler)
    {

        IHostBuilder hostBuilder = new HostBuilder()
            .ConfigureWebHost(webHost =>
            {

                webHost.UseTestServer();

                webHost.ConfigureServices(static services => services.AddRouting());

                webHost.Configure(app =>
                {

                    app.UseRouting();

                    app.UseEndpoints(endpoints =>
                    {

                        A2AServer server = new(
                            agentHandler,
                            new InMemoryTaskStore(),
                            new ChannelEventNotifier(),
                            NullLogger<A2AServer>.Instance,
                            new A2AServerOptions { AutoAppendHistory = true });

                        endpoints.MapA2A(server, "/agent");

                        endpoints.MapWellKnownAgentCard(BuildFakeCard());

                    });

                });

            });

        IHost host = await hostBuilder.StartAsync().ConfigureAwait(false);

        return host.GetTestServer();

    }

    private static AgentCard BuildFakeCard() => new()
    {
        Name = "Fake Remote Agent",
        Description = "Test double A2A agent.",
        Version = "1.0.0",
        SupportedInterfaces =
        [
            new AgentInterface { Url = $"http://{FakeAgentHost}/agent", ProtocolBinding = "JSONRPC", ProtocolVersion = "1.0" },
        ],
        Capabilities = new AgentCapabilities { Streaming = true, PushNotifications = false },
        DefaultInputModes = ["text/plain"],
        DefaultOutputModes = ["text/plain"],
    };

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {

        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);

    }

    private sealed class RecordingHttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {

            Requests.Add(request);

            return responder(request);

        }

    }

    /// <summary>Replies immediately as a completed task carrying a single text artifact.</summary>
    private sealed class EchoingAgentHandler(string responseText) : IAgentHandler
    {

        public async Task ExecuteAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken)
        {

            TaskUpdater updater = new(eventQueue, context.TaskId, context.ContextId);

            await updater.SubmitAsync(cancellationToken).ConfigureAwait(false);

            await updater.StartWorkAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

            await updater.AddArtifactAsync([Part.FromText(responseText)], cancellationToken: cancellationToken).ConfigureAwait(false);

            await updater.CompleteAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        }

        public Task CancelAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken) =>
            Task.CompletedTask;

    }

    /// <summary>Always rejects the incoming task.</summary>
    private sealed class RejectingAgentHandler(string reason) : IAgentHandler
    {

        public async Task ExecuteAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken)
        {

            TaskUpdater updater = new(eventQueue, context.TaskId, context.ContextId);

            await updater.SubmitAsync(cancellationToken).ConfigureAwait(false);

            await updater.RejectAsync(
                new Message { Role = Role.Agent, MessageId = Guid.NewGuid().ToString("N"), Parts = [Part.FromText(reason)] },
                cancellationToken).ConfigureAwait(false);

        }

        public Task CancelAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken) =>
            Task.CompletedTask;

    }

    /// <summary>Blocks in ExecuteAsync until released by the test, to hold a concurrency slot open.</summary>
    private sealed class GateAgentHandler : IAgentHandler, IDisposable
    {

        private readonly SemaphoreSlim _entered = new(0, 1);

        private readonly TaskCompletionSource<string> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ExecuteAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken)
        {

            TaskUpdater updater = new(eventQueue, context.TaskId, context.ContextId);

            await updater.SubmitAsync(cancellationToken).ConfigureAwait(false);

            await updater.StartWorkAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

            _entered.Release();

            string text = await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

            await updater.AddArtifactAsync([Part.FromText(text)], cancellationToken: cancellationToken).ConfigureAwait(false);

            await updater.CompleteAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        }

        public Task CancelAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task WaitUntilEnteredAsync() => _entered.WaitAsync();

        public void Release(string text) => _release.TrySetResult(text);

        public void Dispose() => _entered.Dispose();

    }

}
