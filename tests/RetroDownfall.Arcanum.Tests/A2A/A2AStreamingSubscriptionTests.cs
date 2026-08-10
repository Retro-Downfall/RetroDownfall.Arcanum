using System.Collections.Concurrent;
using System.Net;
using System.Text;

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

/// <summary>
/// Issue #66: a peer that advertises <c>Capabilities.Streaming</c> is subscribed to rather than polled,
/// so remote state changes arrive when they happen instead of up to two seconds later — without
/// changing what "settled" means, losing cancellation propagation, or flooding the Chronicle.
/// </summary>
[Collection("OutboundUrlGuardDns")]
public sealed class A2AStreamingSubscriptionTests : IDisposable
{

    private const string FakeAgentHost = "streaming-agent.example.test";

    private const string DiscoveryUrl = $"http://{FakeAgentHost}/";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly IDnsResolver _originalResolver;

    public A2AStreamingSubscriptionTests()
    {

        _originalResolver = OutboundUrlGuard.DnsResolver;

        FakeDnsResolver fake = new();

        fake.Add(FakeAgentHost, IPAddress.Parse("93.184.216.34"));

        OutboundUrlGuard.DnsResolver = fake;

    }

    public void Dispose() => OutboundUrlGuard.DnsResolver = _originalResolver;

    [Fact]
    public async Task DispatchSendingAsync_StreamingCapablePeer_SubscribesInsteadOfPolling()
    {

        using GateAgentHandler agentHandler = new();

        using TestServer server = await CreateFakeRemoteAgentServerAsync(agentHandler, streaming: true);

        using HttpMessageHandler serverHandler = server.CreateHandler();

        using MethodRecordingHandler handler = new(serverHandler);

        A2AClientService client = CreateClient(handler, EnabledSettings());

        Task<Result<A2ADispatchResult>> dispatch = client.DispatchSendingAsync("do the thing", null, DiscoveryUrl);

        Assert.True(
            await handler.WaitForMethodAsync("SubscribeToTask", Patience),
            "the client never subscribed to the streaming peer. Methods seen: " + handler.Describe());

        agentHandler.Release("streamed answer");

        Result<A2ADispatchResult> result = await dispatch.WaitAsync(Patience);

        Assert.True(result.IsSuccess);

        Assert.Equal("streamed answer", result.Value.ResponseText);

        // The whole point of #66: a settled task is read once for its artifacts and usage, not polled on
        // a two-second backoff for the entire remote run.
        Assert.True(
            handler.Count("GetTask") <= 1,
            $"expected at most one settle read, saw {handler.Count("GetTask")}. Methods: {handler.Describe()}");

    }

    [Fact]
    public async Task DispatchSendingAsync_NonStreamingPeer_StillPolls()
    {

        using GateAgentHandler agentHandler = new();

        using TestServer server = await CreateFakeRemoteAgentServerAsync(agentHandler, streaming: false);

        using HttpMessageHandler serverHandler = server.CreateHandler();

        using MethodRecordingHandler handler = new(serverHandler);

        A2AClientService client = CreateClient(handler, EnabledSettings());

        Task<Result<A2ADispatchResult>> dispatch = client.DispatchSendingAsync("do the thing", null, DiscoveryUrl);

        Assert.True(
            await handler.WaitForMethodAsync("GetTask", Patience),
            "the client never polled the non-streaming peer. Methods seen: " + handler.Describe());

        agentHandler.Release("polled answer");

        Result<A2ADispatchResult> result = await dispatch.WaitAsync(Patience);

        Assert.True(result.IsSuccess);

        Assert.Equal("polled answer", result.Value.ResponseText);

        // Peer capability decides, not configuration: a peer that never advertised streaming is never
        // asked to stream.
        Assert.Equal(0, handler.Count("SubscribeToTask"));

    }

    [Fact]
    public async Task DispatchSendingAsync_SubscriptionEndsWithoutSettling_DegradesToPolling()
    {

        using GateAgentHandler agentHandler = new();

        using TestServer server = await CreateFakeRemoteAgentServerAsync(agentHandler, streaming: true);

        using HttpMessageHandler serverHandler = server.CreateHandler();

        // A stream that opens and immediately ends is the realistic mid-Sending drop: a proxy timeout, a
        // half-closed connection. The Sending must survive it, not fail on it.
        using MethodRecordingHandler handler = new(
            serverHandler,
            intercept: static _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(string.Empty, Encoding.UTF8, "text/event-stream"),
            },
            interceptMethod: "SubscribeToTask");

        A2AClientService client = CreateClient(handler, EnabledSettings());

        Task<Result<A2ADispatchResult>> dispatch = client.DispatchSendingAsync("do the thing", null, DiscoveryUrl);

        Assert.True(
            await handler.WaitForMethodAsync("GetTask", Patience),
            "the client did not fall back to polling after the subscription dropped. Methods: " + handler.Describe());

        agentHandler.Release("answered after the stream dropped");

        Result<A2ADispatchResult> result = await dispatch.WaitAsync(Patience);

        Assert.True(result.IsSuccess);

        Assert.Equal("answered after the stream dropped", result.Value.ResponseText);

    }

    [Fact]
    public async Task DispatchSendingAsync_SubscriptionFailsOutright_DegradesToPolling()
    {

        using GateAgentHandler agentHandler = new();

        using TestServer server = await CreateFakeRemoteAgentServerAsync(agentHandler, streaming: true);

        using HttpMessageHandler serverHandler = server.CreateHandler();

        // A peer that advertises streaming and then refuses it is a lie the Sending has to survive.
        using MethodRecordingHandler handler = new(
            serverHandler,
            intercept: static _ => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            interceptMethod: "SubscribeToTask");

        A2AClientService client = CreateClient(handler, EnabledSettings());

        Task<Result<A2ADispatchResult>> dispatch = client.DispatchSendingAsync("do the thing", null, DiscoveryUrl);

        Assert.True(
            await handler.WaitForMethodAsync("GetTask", Patience),
            "the client did not fall back to polling after the subscription failed. Methods: " + handler.Describe());

        agentHandler.Release("answered anyway");

        Result<A2ADispatchResult> result = await dispatch.WaitAsync(Patience);

        Assert.True(result.IsSuccess);

        Assert.Equal("answered anyway", result.Value.ResponseText);

    }

    [Fact]
    public async Task DispatchSendingAsync_StreamingPath_LocalCancellationStillCancelsTheRemoteTask()
    {

        using GateAgentHandler agentHandler = new();

        using TestServer server = await CreateFakeRemoteAgentServerAsync(agentHandler, streaming: true);

        using HttpMessageHandler serverHandler = server.CreateHandler();

        using MethodRecordingHandler handler = new(serverHandler);

        A2AClientService client = CreateClient(handler, EnabledSettings());

        using CancellationTokenSource cts = new();

        Task<Result<A2ADispatchResult>> dispatch =
            client.DispatchSendingAsync("long running work", null, DiscoveryUrl, cancellationToken: cts.Token);

        Assert.True(
            await handler.WaitForMethodAsync("SubscribeToTask", Patience),
            "the client never subscribed. Methods seen: " + handler.Describe());

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatch);

        // Abandoning a subscription leaves the remote running and billing exactly as abandoning a poll
        // loop did (issue #12); the cleanup deadline still has to reach the peer.
        Assert.True(
            await agentHandler.WaitForCancelAsync(TimeSpan.FromSeconds(10)),
            "the streaming path did not cancel the remote task after local cancellation.");

    }

    [Fact]
    public async Task DispatchSendingAsync_StreamingPeerParksAtInputRequired_ReturnsAContinuation()
    {

        using TestServer server = await CreateFakeRemoteAgentServerAsync(
            new InputRequiredAgentHandler("need more detail"),
            streaming: true);

        using HttpMessageHandler handler = server.CreateHandler();

        A2AClientService client = CreateClient(handler, EnabledSettings());

        Result<A2ADispatchResult> result = await client
            .DispatchSendingAsync("do the thing", null, DiscoveryUrl, mode: A2ADispatchMode.Continuable)
            .WaitAsync(Patience);

        // input-required is not terminal in A2A, so the streaming path has to settle on it exactly as the
        // poll loop does — otherwise a streaming peer deadlocks where a polled one continues (§5.7.1.3).
        Assert.True(result.IsSuccess);

        Assert.NotNull(result.Value.Continuation);

        Assert.Equal(A2AContinuationNeed.Input, result.Value.Continuation!.Need);

    }

    [Fact]
    public async Task DispatchSendingAsync_StreamingPeerRepeatsAState_EmitsNoDuplicateProgressFrames()
    {

        using TestServer server = await CreateFakeRemoteAgentServerAsync(
            new ChattyAgentHandler("finished"),
            streaming: true);

        using HttpMessageHandler handler = server.CreateHandler();

        A2AClientService client = CreateClient(handler, EnabledSettings());

        List<A2ASendingProgress> observed = [];

        Progress<A2ASendingProgress> progress = new(observed.Add);

        Result<A2ADispatchResult> result = await client
            .DispatchSendingAsync("do the thing", null, DiscoveryUrl, progress: progress)
            .WaitAsync(Patience);

        Assert.True(result.IsSuccess);

        // Progress is still reported per remote transition, not per pushed frame: a keepalive-heavy
        // stream must not flood the Chronicle any more than a 2 s poll loop did (issue #61).
        await WaitForAsync(() => observed.Count > 0, Patience);

        string[] states = [.. observed.Select(static p => p.RemoteState)];

        for (int i = 1; i < states.Length; i++)
        {

            Assert.NotEqual(states[i - 1], states[i]);

        }

    }

    // ---------------------------------------------------------------------------------------------

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {

        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;

        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {

            await Task.Delay(25).ConfigureAwait(false);

        }

    }

    private static ArcanumSettings EnabledSettings() => new()
    {
        Features = new FeatureSettings
        {
            Conclave = true,
            A2AClient = true,
        },
        Integrations = new IntegrationSettings
        {
            A2A = new A2AIntegrationSettings(),
        },
    };

    private static A2AClientService CreateClient(HttpMessageHandler handler, ArcanumSettings settings) =>
        new(new FakeHttpClientFactory(handler), new TestOptionsMonitor<ArcanumSettings>(settings), NullLogger<A2AClientService>.Instance);

    private static async Task<TestServer> CreateFakeRemoteAgentServerAsync(IAgentHandler agentHandler, bool streaming)
    {

        AgentCard advertised = new()
        {
            Name = "Fake Remote Agent",
            Description = "Test double A2A agent.",
            Version = "1.0.0",
            SupportedInterfaces =
            [
                new AgentInterface { Url = $"http://{FakeAgentHost}/agent", ProtocolBinding = "JSONRPC", ProtocolVersion = "1.0" },
            ],
            Capabilities = new AgentCapabilities { Streaming = streaming, PushNotifications = false },
            DefaultInputModes = ["text/plain"],
            DefaultOutputModes = ["text/plain"],
        };

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

                        endpoints.MapWellKnownAgentCard(advertised);

                    });

                });

            });

        IHost host = await hostBuilder.StartAsync().ConfigureAwait(false);

        return host.GetTestServer();

    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {

        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);

    }

    /// <summary>
    /// Records which JSON-RPC methods reached the peer, and can stand in for one of them so a
    /// subscription can be made to drop or fail without touching the fake agent.
    /// </summary>
    private sealed class MethodRecordingHandler(
        HttpMessageHandler inner,
        Func<HttpRequestMessage, HttpResponseMessage>? intercept = null,
        string? interceptMethod = null) : DelegatingHandler(inner)
    {

        private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);

        private readonly ConcurrentDictionary<string, TaskCompletionSource> _waiters =
            new(StringComparer.Ordinal);

        public int Count(string method) => _counts.TryGetValue(method, out int count) ? count : 0;

        public string Describe() =>
            string.Join(", ", _counts.Select(static e => $"{e.Key}×{e.Value}"));

        public async Task<bool> WaitForMethodAsync(string method, TimeSpan timeout)
        {

            TaskCompletionSource waiter = _waiters.GetOrAdd(
                method,
                static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

            if (Count(method) > 0)
            {

                return true;

            }

            Task completed = await Task.WhenAny(waiter.Task, Task.Delay(timeout)).ConfigureAwait(false);

            return ReferenceEquals(completed, waiter.Task);

        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {

            string body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            string? method = ExtractMethod(body);

            if (method is not null)
            {

                _counts.AddOrUpdate(method, 1, static (_, current) => current + 1);

                _waiters
                    .GetOrAdd(method, static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
                    .TrySetResult();

            }

            if (intercept is not null && method is not null && string.Equals(method, interceptMethod, StringComparison.Ordinal))
            {

                return intercept(request);

            }

            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        }

        private static string? ExtractMethod(string body)
        {

            const string marker = "\"method\":\"";

            int start = body.IndexOf(marker, StringComparison.Ordinal);

            if (start < 0)
            {

                return null;

            }

            start += marker.Length;

            int end = body.IndexOf('"', start);

            return end < 0 ? null : body[start..end];

        }

    }

    /// <summary>Holds the remote task open until the test releases it, and records a peer cancel.</summary>
    private sealed class GateAgentHandler : IAgentHandler, IDisposable
    {

        private readonly TaskCompletionSource<string> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _cancelObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ExecuteAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken)
        {

            TaskUpdater updater = new(eventQueue, context.TaskId, context.ContextId);

            await updater.SubmitAsync(cancellationToken).ConfigureAwait(false);

            await updater.StartWorkAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

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

        public void Release(string text) => _release.TrySetResult(text);

        public async Task<bool> WaitForCancelAsync(TimeSpan timeout)
        {

            Task completed = await Task.WhenAny(_cancelObserved.Task, Task.Delay(timeout)).ConfigureAwait(false);

            return ReferenceEquals(completed, _cancelObserved.Task);

        }

        public void Dispose()
        {

            _release.TrySetCanceled();

            _cancelObserved.TrySetCanceled();

        }

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

    /// <summary>Pushes the same <c>Working</c> status repeatedly before finishing.</summary>
    private sealed class ChattyAgentHandler(string responseText) : IAgentHandler
    {

        public async Task ExecuteAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken)
        {

            TaskUpdater updater = new(eventQueue, context.TaskId, context.ContextId);

            await updater.SubmitAsync(cancellationToken).ConfigureAwait(false);

            for (int i = 0; i < 4; i++)
            {

                await updater
                    .StartWorkAsync(
                        new Message { Role = Role.Agent, MessageId = Guid.NewGuid().ToString("N"), Parts = [Part.FromText("still working")] },
                        cancellationToken)
                    .ConfigureAwait(false);

            }

            await updater.AddArtifactAsync([Part.FromText(responseText)], cancellationToken: cancellationToken).ConfigureAwait(false);

            await updater.CompleteAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        }

        public Task CancelAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken) =>
            Task.CompletedTask;

    }

}
