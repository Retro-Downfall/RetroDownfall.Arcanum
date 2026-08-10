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

    private static ArcanumSettings EnabledSettings(string[]? allowedRemoteAgents = null) => new()
    {
        Features = new FeatureSettings
        {
            Conclave = true,
            A2AClient = true,
        },
        Integrations = new IntegrationSettings
        {
            A2A = new A2AIntegrationSettings
            {
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
    public async Task DispatchSendingAsync_MaxExternalTasksReached_QueuesAndEventuallyRuns()
    {
        ArcanumSettings settings = EnabledSettings();
        settings.Execution.MaxConcurrentA2ATasks = 2;
        int maxExternalTasks = settings.Execution.MaxConcurrentA2ATasks;
        using GateAgentHandler gateHandler = new(maxExternalTasks);

        using TestServer server = await CreateFakeRemoteAgentServerAsync(gateHandler);

        using HttpMessageHandler handler = server.CreateHandler();

        A2AClientService client = CreateClient(handler, settings);

        Task<Result<A2ADispatchResult>>[] admittedCalls = Enumerable
            .Range(0, maxExternalTasks)
            .Select(index => client.DispatchSendingAsync($"admitted-{index}", null, DiscoveryUrl))
            .ToArray();

        await gateHandler.WaitUntilEnteredAsync();

        Task<Result<A2ADispatchResult>> queued =
            client.DispatchSendingAsync("queued", null, DiscoveryUrl);

        Assert.False(queued.IsCompleted);

        gateHandler.Release("admitted answer");

        Result<A2ADispatchResult>[] results = await Task.WhenAll([.. admittedCalls, queued]);

        Assert.All(results, static result => Assert.True(result.IsSuccess));

    }

    [Fact]
    public async Task DispatchSendingAsync_StampsThisNodeOntoTheDelegationChain()
    {

        ChainCapturingAgentHandler agentHandler = new("done");

        using TestServer server = await CreateFakeRemoteAgentServerAsync(agentHandler);

        using HttpMessageHandler handler = server.CreateHandler();

        A2AClientService client = CreateClient(handler, EnabledSettings());

        Result<A2ADispatchResult> result = await client.DispatchSendingAsync("delegate this", null, DiscoveryUrl);

        Assert.True(result.IsSuccess);

        // Without this the receiving agent has no way to notice the work has already passed through us.
        Assert.Equal([ConclaveDelegationChain.NodeId], agentHandler.ObservedChain);

    }

    [Fact]
    public async Task DispatchSendingAsync_LocalCancellation_CancelsTheRemoteTaskInsteadOfAbandoningIt()
    {

        using GateAgentHandler gateHandler = new(1);

        using TestServer server = await CreateFakeRemoteAgentServerAsync(gateHandler);

        using HttpMessageHandler serverHandler = server.CreateHandler();

        // Cancelling once the client has begun following the remote task (rather than the instant the
        // remote starts work) is the case that matters and the only deterministic one: by then the client
        // holds the remote task id. "Following" is a subscription or a poll depending on what the peer
        // advertises (issue #66); either proves the same thing.
        using PollAwareHandler handler = new(serverHandler);

        A2AClientService client = CreateClient(handler, EnabledSettings());

        using CancellationTokenSource cts = new();

        Task<Result<A2ADispatchResult>> dispatch =
            client.DispatchSendingAsync("long running work", null, DiscoveryUrl, cancellationToken: cts.Token);

        Assert.True(
            await handler.WaitUntilFollowingTaskAsync(TimeSpan.FromSeconds(30)),
            "dispatch_sending never followed the remote task. Requests seen: " + string.Join(" | ", handler.Seen));

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatch);

        // Abandoning the HTTP call leaves the remote Apprentice running and billing. The peer must be
        // told to cancel (issue #12: cancellation propagation in both directions).
        Assert.True(
            await gateHandler.WaitForCancelAsync(TimeSpan.FromSeconds(10)),
            "dispatch_sending did not send an A2A cancel to the remote agent after local cancellation.");

    }

    [Fact]
    public async Task DispatchSendingAsync_RemoteEntersInputRequired_ReturnsActionableFailureInsteadOfHanging()
    {

        using TestServer server = await CreateFakeRemoteAgentServerAsync(new InputRequiredAgentHandler("need more detail"));

        using HttpMessageHandler handler = server.CreateHandler();

        A2AClientService client = CreateClient(handler, EnabledSettings());

        // input-required is not an A2A terminal state, so a naive "poll until terminal" loop spins forever
        // and pins a MaxConcurrentA2ATasks slot. A blocking Sending cannot supply the extra input, so this
        // has to end as an actionable failure.
        Result<A2ADispatchResult> result = await client
            .DispatchSendingAsync("do the thing", null, DiscoveryUrl)
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Sending.TaskRejected, result.Error.Code);

        Assert.Contains("need more detail", result.Error.Message, StringComparison.Ordinal);

    }

    [Fact]
    public async Task DispatchSendingAsync_CardAdvertisesADisallowedInterface_IsRejectedEvenWhenItIsNotTheFirst()
    {

        // The A2A SDK picks an interface by protocol-binding preference, not by position, so validating
        // only SupportedInterfaces[0] lets a hostile card steer the connection to an unchecked URL.
        AgentCard card = BuildFakeCard();

        card.SupportedInterfaces =
        [
            new AgentInterface { Url = $"http://{FakeAgentHost}/agent", ProtocolBinding = "JSONRPC", ProtocolVersion = "1.0" },
            new AgentInterface { Url = "http://evil.example.test/agent", ProtocolBinding = "HTTP+JSON", ProtocolVersion = "1.0" },
        ];

        using TestServer server = await CreateFakeRemoteAgentServerAsync(new EchoingAgentHandler("nope"), card);

        using HttpMessageHandler handler = server.CreateHandler();

        A2AClientService client = CreateClient(handler, EnabledSettings(allowedRemoteAgents: [$"http://{FakeAgentHost}"]));

        Result<A2ADispatchResult> result = await client.DispatchSendingAsync("do the thing", null, DiscoveryUrl);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Sending.AgentNotAllowed, result.Error.Code);

    }

    [Fact]
    public async Task DispatchSendingAsync_ExplicitAgentCardUrl_IsFetchedAtThatExactPath()
    {

        // Arcanum publishes its own Agent Card at {ServerPath}/agent-card and deliberately does NOT serve
        // the SDK's unauthenticated /.well-known path, so a hard-coded well-known probe cannot discover
        // another Arcanum at all.
        using TestServer server = await CreateFakeRemoteAgentServerAsync(
            new EchoingAgentHandler("reached"),
            cardPath: "/api/conclave/a2a/agent-card",
            mapWellKnown: false);

        using HttpMessageHandler handler = server.CreateHandler();

        A2AClientService client = CreateClient(handler, EnabledSettings());

        Result<A2ADispatchResult> result = await client.DispatchSendingAsync(
            "do the thing",
            null,
            $"http://{FakeAgentHost}/api/conclave/a2a/agent-card");

        Assert.True(result.IsSuccess);

        Assert.Equal("reached", result.Value.ResponseText);

    }

    [Fact]
    public async Task DispatchSendingAsync_SendsTheConfiguredOutboundCredentialHeader()
    {

        const string envVar = "ARCANUM_TEST_A2A_OUTBOUND_KEY";

        global::System.Environment.SetEnvironmentVariable(envVar, "peer-secret");

        try
        {

            using TestServer server = await CreateFakeRemoteAgentServerAsync(new EchoingAgentHandler("ok"));

            using HttpMessageHandler serverHandler = server.CreateHandler();

            using HeaderCapturingHandler handler = new(serverHandler);

            // The allowlist is what vouches for a credential target: agent_url reaches the service verbatim
            // from the model, so an empty allowlist withholds the credential from every target.
            ArcanumSettings settings = EnabledSettings([DiscoveryUrl]);

            settings.Integrations!.A2A!.OutboundCredentialEnvironmentVariable = envVar;

            A2AClientService client = CreateClient(handler, settings);

            Result<A2ADispatchResult> result = await client.DispatchSendingAsync("do the thing", null, DiscoveryUrl);

            Assert.True(result.IsSuccess);

            // Without this, the Archmage Client cannot reach ANY authenticated peer — including another
            // Arcanum, whose A2A routes all sit behind ApiKeyEndpointFilter.
            Assert.All(
                handler.ObservedCredentialHeaders,
                static value => Assert.Equal("peer-secret", value));

            Assert.NotEmpty(handler.ObservedCredentialHeaders);

        }
        finally
        {

            global::System.Environment.SetEnvironmentVariable(envVar, null);

        }

    }

    [Fact]
    public async Task DispatchSendingAsync_NoOutboundCredentialConfigured_SendsNoCredentialHeader()
    {

        using TestServer server = await CreateFakeRemoteAgentServerAsync(new EchoingAgentHandler("ok"));

        using HttpMessageHandler serverHandler = server.CreateHandler();

        using HeaderCapturingHandler handler = new(serverHandler);

        A2AClientService client = CreateClient(handler, EnabledSettings());

        await client.DispatchSendingAsync("do the thing", null, DiscoveryUrl);

        Assert.Empty(handler.ObservedCredentialHeaders);

    }

    private static A2AClientService CreateClient(HttpMessageHandler handler, ArcanumSettings settings) =>
        new(new FakeHttpClientFactory(handler), new TestOptionsMonitor<ArcanumSettings>(settings), NullLogger<A2AClientService>.Instance);

    private static async Task<TestServer> CreateFakeRemoteAgentServerAsync(
        IAgentHandler agentHandler,
        AgentCard? card = null,
        string? cardPath = null,
        bool mapWellKnown = true)
    {

        AgentCard advertised = card ?? BuildFakeCard();

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

                        if (mapWellKnown)
                        {

                            endpoints.MapWellKnownAgentCard(advertised);

                        }

                        if (cardPath is not null)
                        {

                            endpoints.MapGet(
                                cardPath,
                                () => Microsoft.AspNetCore.Http.Results.Json(
                                    advertised,
                                    (System.Text.Json.Serialization.Metadata.JsonTypeInfo<AgentCard>)
                                        A2AJsonUtilities.DefaultOptions.GetTypeInfo(typeof(AgentCard))));

                        }

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

    /// <summary>
    /// Forwards to the fake remote agent while signalling the first call that follows the remote task —
    /// a <c>tasks/get</c> poll or a <c>tasks/subscribe</c> stream — which is the point at which the client
    /// provably holds the remote task id.
    /// </summary>
    private sealed class PollAwareHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {

        private readonly TaskCompletionSource _firstPoll = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public System.Collections.Concurrent.ConcurrentBag<string> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {

            // The JSON-RPC binding posts every call to the same path, so the method name only appears in
            // the body. The HTTP+JSON binding puts it in the path. Match either.
            string path = request.RequestUri?.AbsolutePath ?? string.Empty;

            string body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            Seen.Add($"{request.Method} {path} :: {(body.Length > 120 ? body[..120] : body)}");

            if (body.Contains("\"GetTask\"", StringComparison.Ordinal)
                || body.Contains("\"SubscribeToTask\"", StringComparison.Ordinal)
                || body.Contains("tasks/get", StringComparison.OrdinalIgnoreCase)
                || body.Contains("tasks/subscribe", StringComparison.OrdinalIgnoreCase)
                || (path.Contains("/tasks", StringComparison.OrdinalIgnoreCase)
                    && !path.Contains("agent-card", StringComparison.OrdinalIgnoreCase)))
            {

                _firstPoll.TrySetResult();

            }

            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        }

        public async Task<bool> WaitUntilFollowingTaskAsync(TimeSpan timeout)
        {

            Task completed = await Task.WhenAny(_firstPoll.Task, Task.Delay(timeout)).ConfigureAwait(false);

            return ReferenceEquals(completed, _firstPoll.Task);

        }

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

    /// <summary>Completes immediately, recording the delegation chain the caller stamped on the message.</summary>
    private sealed class ChainCapturingAgentHandler(string responseText) : IAgentHandler
    {

        public string[] ObservedChain { get; private set; } = [];

        public async Task ExecuteAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken)
        {

            ObservedChain = ConclaveDelegationChain.Read(context.Message?.Metadata);

            TaskUpdater updater = new(eventQueue, context.TaskId, context.ContextId);

            await updater.SubmitAsync(cancellationToken).ConfigureAwait(false);

            await updater.StartWorkAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

            await updater.AddArtifactAsync([Part.FromText(responseText)], cancellationToken: cancellationToken).ConfigureAwait(false);

            await updater.CompleteAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        }

        public Task CancelAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken) =>
            Task.CompletedTask;

    }

    /// <summary>Parks the task in the non-terminal <c>input-required</c> state and never finishes.</summary>
    private sealed class InputRequiredAgentHandler(string reason) : IAgentHandler
    {

        public async Task ExecuteAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken)
        {

            TaskUpdater updater = new(eventQueue, context.TaskId, context.ContextId);

            await updater.SubmitAsync(cancellationToken).ConfigureAwait(false);

            await updater.StartWorkAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

            await updater.RequireInputAsync(
                new Message { Role = Role.Agent, MessageId = Guid.NewGuid().ToString("N"), Parts = [Part.FromText(reason)] },
                cancellationToken).ConfigureAwait(false);

        }

        public Task CancelAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken) =>
            Task.CompletedTask;

    }

    /// <summary>Records the outbound credential header the Archmage Client attached, if any.</summary>
    private sealed class HeaderCapturingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {

        public System.Collections.Concurrent.ConcurrentBag<string> ObservedCredentialHeaders { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {

            if (request.Headers.TryGetValues(A2AClientService.DefaultOutboundCredentialHeader, out IEnumerable<string>? values))
            {

                foreach (string value in values)
                {

                    ObservedCredentialHeaders.Add(value);

                }

            }

            return base.SendAsync(request, cancellationToken);

        }

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
    private sealed class GateAgentHandler(int expectedEntries) : IAgentHandler, IDisposable
    {

        private readonly TaskCompletionSource _allEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource<string> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _cancelObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _enteredCount;

        public async Task ExecuteAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken)
        {

            TaskUpdater updater = new(eventQueue, context.TaskId, context.ContextId);

            await updater.SubmitAsync(cancellationToken).ConfigureAwait(false);

            await updater.StartWorkAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

            if (Interlocked.Increment(ref _enteredCount) == expectedEntries)
            {
                _allEntered.TrySetResult();
            }

            string text = await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

            await updater.AddArtifactAsync([Part.FromText(text)], cancellationToken: cancellationToken).ConfigureAwait(false);

            await updater.CompleteAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        }

        public Task CancelAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken)
        {

            _cancelObserved.TrySetResult();

            _release.TrySetCanceled();

            return Task.CompletedTask;

        }

        public Task WaitUntilEnteredAsync() => _allEntered.Task;

        public async Task<bool> WaitForCancelAsync(TimeSpan timeout)
        {

            Task completed = await Task.WhenAny(_cancelObserved.Task, Task.Delay(timeout)).ConfigureAwait(false);

            return ReferenceEquals(completed, _cancelObserved.Task);

        }

        public void Release(string text) => _release.TrySetResult(text);

        public void Dispose()
        {

            _allEntered.TrySetCanceled();

            _cancelObserved.TrySetCanceled();

        }

    }

}
