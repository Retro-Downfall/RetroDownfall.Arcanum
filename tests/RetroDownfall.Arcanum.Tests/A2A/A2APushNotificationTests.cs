using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;

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
/// Issue #67: push notifications in both directions. Inbound, a peer's callback is honoured through the
/// same egress gate as any other outbound request. Outbound, a Sending can hand back its concurrency
/// slot and be settled by a callback — including one that arrives after the process which dispatched it
/// has gone.
/// </summary>
[Collection("OutboundUrlGuardDns")]
public sealed class A2APushNotificationTests : IDisposable
{

    private const string FakeAgentHost = "push-agent.example.test";

    private const string PeerCallbackHost = "peer-callback.example.test";

    private const string DiscoveryUrl = $"http://{FakeAgentHost}/";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly IDnsResolver _originalResolver;

    public A2APushNotificationTests()
    {

        _originalResolver = OutboundUrlGuard.DnsResolver;

        FakeDnsResolver fake = new();

        fake.Add(FakeAgentHost, IPAddress.Parse("93.184.216.34"));

        fake.Add(PeerCallbackHost, IPAddress.Parse("93.184.216.35"));

        fake.Add("127.0.0.1", IPAddress.Parse("127.0.0.1"));

        OutboundUrlGuard.DnsResolver = fake;

    }

    public void Dispose() => OutboundUrlGuard.DnsResolver = _originalResolver;

    // ── inbound: whose callbacks this instance will honour ─────────────────────────────────────────

    [Fact]
    public async Task PushRegistry_WhenTheSurfaceIsOff_RefusesRegistration()
    {

        A2APushNotificationRegistry registry = CreateRegistry(pushEnabled: false);

        Result<PushNotificationConfig> result = await registry.RegisterAsync(
            "task-1",
            new PushNotificationConfig { Url = $"https://{PeerCallbackHost}/hook" });

        // The card advertises pushNotifications only when the surface is on; accepting a config while it
        // is off would promise a peer notifications nothing will ever send.
        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Sending.PushNotificationsDisabled, result.Error.Code);

    }

    [Fact]
    public async Task PushRegistry_SsrfBlockedCallback_IsRefused()
    {

        A2APushNotificationRegistry registry = CreateRegistry(pushEnabled: true);

        Result<PushNotificationConfig> result = await registry.RegisterAsync(
            "task-1",
            new PushNotificationConfig { Url = "http://127.0.0.1/hook" });

        // A peer-supplied URL this instance will POST to on a schedule the peer chooses is an SSRF
        // primitive; it goes through the same guard as every other egress (§11.11).
        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Sending.PushNotificationRejected, result.Error.Code);

    }

    [Fact]
    public async Task PushRegistry_CallbackOutsideTheAllowlist_IsRefused()
    {

        A2APushNotificationRegistry registry = CreateRegistry(
            pushEnabled: true,
            allowedRemoteAgents: [$"https://{FakeAgentHost}"]);

        Result<PushNotificationConfig> result = await registry.RegisterAsync(
            "task-1",
            new PushNotificationConfig { Url = $"https://{PeerCallbackHost}/hook" });

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Sending.AgentNotAllowed, result.Error.Code);

    }

    [Fact]
    public async Task PushRegistry_AllowedCallback_IsRememberedAndForgettable()
    {

        A2APushNotificationRegistry registry = CreateRegistry(
            pushEnabled: true,
            allowedRemoteAgents: [$"https://{PeerCallbackHost}"]);

        Result<PushNotificationConfig> result = await registry.RegisterAsync(
            "task-1",
            new PushNotificationConfig { Url = $"https://{PeerCallbackHost}/hook", Token = "peer-secret" });

        Assert.True(result.IsSuccess);

        Assert.Equal($"https://{PeerCallbackHost}/hook", registry.Resolve("task-1")!.Url);

        Assert.Single(registry.List("task-1"));

        registry.Remove("task-1");

        Assert.Null(registry.Resolve("task-1"));

    }

    [Fact]
    public async Task PushRegistry_UrlWithoutAScheme_IsRefused()
    {

        A2APushNotificationRegistry registry = CreateRegistry(pushEnabled: true);

        Result<PushNotificationConfig> result = await registry.RegisterAsync(
            "task-1",
            new PushNotificationConfig { Url = "not-a-url" });

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Sending.PushNotificationRejected, result.Error.Code);

    }

    [Fact]
    public async Task Dispatcher_PostsTheTaskStateWithTheRegisteredToken()
    {

        RecordingCallbackHandler handler = new();

        A2APushNotificationDispatcher dispatcher = new(
            new FakeHttpClientFactory(handler),
            new TestOptionsMonitor<ArcanumSettings>(Settings(pushEnabled: true)),
            NullLogger<A2APushNotificationDispatcher>.Instance);

        await dispatcher.NotifyAsync(
            new PushNotificationConfig { Url = $"https://{PeerCallbackHost}/hook", Token = "peer-secret" },
            "task-1",
            "completed");

        (string Body, string? Token) delivered = Assert.Single(handler.Delivered);

        Assert.Equal("peer-secret", delivered.Token);

        using JsonDocument payload = JsonDocument.Parse(delivered.Body);

        Assert.Equal("task-1", payload.RootElement.GetProperty("taskId").GetString());

        Assert.Equal("completed", payload.RootElement.GetProperty("state").GetString());

    }

    [Fact]
    public async Task Dispatcher_DoesNotPostToACallbackTheGuardNowBlocks()
    {

        RecordingCallbackHandler handler = new();

        A2APushNotificationDispatcher dispatcher = new(
            new FakeHttpClientFactory(handler),
            new TestOptionsMonitor<ArcanumSettings>(Settings(pushEnabled: true)),
            NullLogger<A2APushNotificationDispatcher>.Instance);

        // Re-checked at delivery, not only at registration: DNS and the allowlist can both change while a
        // long Sending runs.
        await dispatcher.NotifyAsync(
            new PushNotificationConfig { Url = "http://127.0.0.1/hook" },
            "task-1",
            "completed");

        Assert.Empty(handler.Delivered);

    }

    [Fact]
    public void AgentCardCapability_TracksTheConfiguredSurface()
    {

        Assert.False(Settings(pushEnabled: false).ResolveA2A().PushNotificationsEnabled);

        Assert.True(Settings(pushEnabled: true).ResolveA2A().PushNotificationsEnabled);

    }

    // ── outbound: the callback that settles a slot-free Sending ────────────────────────────────────

    [Fact]
    public void CallbackToken_MatchesOnlyItsOwnSecret()
    {

        string token = A2ACallbackToken.Mint();

        string hash = A2ACallbackToken.Hash(token);

        Assert.NotEqual(token, hash);

        Assert.True(A2ACallbackToken.Matches(token, hash));

        Assert.False(A2ACallbackToken.Matches(A2ACallbackToken.Mint(), hash));

        Assert.False(A2ACallbackToken.Matches(null, hash));

        Assert.False(A2ACallbackToken.Matches(token, null));

    }

    [Fact]
    public void CallbackRegistry_WakesOnlyTheCorrectlyAuthenticatedCaller()
    {

        A2ASendingCallbackRegistry registry = new(NullLogger<A2ASendingCallbackRegistry>.Instance);

        string token = A2ACallbackToken.Mint();

        using SemaphoreSlim signal = new(0);

        using IDisposable registration = registry.Register("config-1", A2ACallbackToken.Hash(token), signal);

        // An unauthenticated post must not settle someone else's Sending.
        Assert.Equal(A2ACallbackOutcome.Unauthenticated, registry.TrySignal("config-1", "wrong"));

        Assert.Equal(0, signal.CurrentCount);

        Assert.Equal(A2ACallbackOutcome.Delivered, registry.TrySignal("config-1", token));

        Assert.Equal(1, signal.CurrentCount);

    }

    [Fact]
    public void CallbackRegistry_ForAConfigNobodyIsWaitingOn_ReportsNoLiveWaiter()
    {

        A2ASendingCallbackRegistry registry = new(NullLogger<A2ASendingCallbackRegistry>.Instance);

        // Not an error: this is what a callback for a Sending registered by a previous process looks
        // like, and the durable record is what settles it.
        Assert.Equal(A2ACallbackOutcome.NoLiveWaiter, registry.TrySignal("config-unknown", "anything"));

        string token = A2ACallbackToken.Mint();

        using SemaphoreSlim signal = new(0);

        using (registry.Register("config-1", A2ACallbackToken.Hash(token), signal))
        {

            Assert.Equal(A2ACallbackOutcome.Delivered, registry.TrySignal("config-1", token));

        }

        // Disposing the registration is what stops a finished Sending leaving a live callback endpoint.
        Assert.Equal(A2ACallbackOutcome.NoLiveWaiter, registry.TrySignal("config-1", token));

    }

    [Fact]
    public async Task DispatchSendingAsync_CallbackMode_ReleasesTheSlotAndSettlesOnTheCallback()
    {

        using GateFirstAgentHandler agentHandler = new();

        using TestServer server = await CreateFakeRemoteAgentServerAsync(agentHandler, advertisesPush: true);

        using HttpMessageHandler serverHandler = server.CreateHandler();

        using CallbackRegistrationCapturingHandler handler = new(serverHandler);

        A2ASendingCallbackRegistry callbacks = new(NullLogger<A2ASendingCallbackRegistry>.Instance);

        // Exactly one slot, so the second Sending below can only admit if the first genuinely gave its
        // slot back rather than holding it for the whole remote run.
        ArcanumSettings settings = Settings(pushEnabled: true, callbackBaseUrl: $"https://{PeerCallbackHost}");

        settings.Execution.MaxConcurrentA2ATasks = 1;

        A2AClientService client = CreateClient(handler, settings, callbacks);

        Task<Result<A2ADispatchResult>> callbackDispatch = client.DispatchSendingAsync(
            "long running work",
            null,
            DiscoveryUrl,
            mode: A2ADispatchMode.Callback);

        Assert.True(
            await handler.WaitForRegistrationAsync(Patience),
            "the client never registered a callback with the peer.");

        Assert.False(callbackDispatch.IsCompleted);

        // The proof: a second Sending on the same client — and therefore the same one-slot gate — admits
        // and finishes while the first is still waiting on its peer.
        Result<A2ADispatchResult> second = await client
            .DispatchSendingAsync("quick work", null, DiscoveryUrl)
            .WaitAsync(Patience);

        Assert.True(second.IsSuccess);

        Assert.Equal("second answer", second.Value.ResponseText);

        Assert.False(callbackDispatch.IsCompleted);

        agentHandler.Release("callback answer");

        // A peer posts a callback per transition; the client re-reads the task on each one and settles
        // when it finds a terminal state.
        Result<A2ADispatchResult> result = await SignalUntilSettledAsync(callbacks, handler, callbackDispatch);

        Assert.True(result.IsSuccess);

        Assert.Equal("callback answer", result.Value.ResponseText);

    }

    private static async Task<Result<A2ADispatchResult>> SignalUntilSettledAsync(
        A2ASendingCallbackRegistry callbacks,
        CallbackRegistrationCapturingHandler handler,
        Task<Result<A2ADispatchResult>> dispatch)
    {

        DateTimeOffset deadline = DateTimeOffset.UtcNow + Patience;

        while (!dispatch.IsCompleted && DateTimeOffset.UtcNow < deadline)
        {

            Assert.Equal(
                A2ACallbackOutcome.Delivered,
                callbacks.TrySignal(handler.ConfigId ?? "?", handler.Token ?? "?"));

            await Task.WhenAny(dispatch, Task.Delay(50)).ConfigureAwait(false);

        }

        return await dispatch.WaitAsync(Patience).ConfigureAwait(false);

    }

    [Fact]
    public async Task DispatchSendingAsync_CallbackThatNeverArrives_KeepsWaitingWithoutHoldingCapacity()
    {

        using GateFirstAgentHandler agentHandler = new();

        using TestServer server = await CreateFakeRemoteAgentServerAsync(agentHandler, advertisesPush: true);

        using HttpMessageHandler serverHandler = server.CreateHandler();

        using CallbackRegistrationCapturingHandler handler = new(serverHandler);

        ArcanumSettings settings = Settings(pushEnabled: true, callbackBaseUrl: $"https://{PeerCallbackHost}");

        settings.Execution.MaxConcurrentA2ATasks = 1;

        A2AClientService client = CreateClient(
            handler,
            settings,
            new A2ASendingCallbackRegistry(NullLogger<A2ASendingCallbackRegistry>.Instance));

        Task<Result<A2ADispatchResult>> orphaned = client.DispatchSendingAsync(
            "work whose callback is lost",
            null,
            DiscoveryUrl,
            mode: A2ADispatchMode.Callback);

        Assert.True(await handler.WaitForRegistrationAsync(Patience), "the client never registered a callback.");

        Assert.True(
            await handler.WaitForInitialTaskReadAsync(Patience),
            "the client never completed its initial authoritative task read.");

        // The peer finishes and its notification is lost in transit — nothing ever signals this instance.
        agentHandler.Release("an answer nobody was told about");

        // There is no whole-operation deadline, so the Sending keeps waiting rather than inventing
        // an outcome…
        await Task.Delay(500);

        Assert.False(orphaned.IsCompleted);

        // …and the capacity it gave back stays given back, so other work is unaffected by the loss.
        Result<A2ADispatchResult> unaffected = await client
            .DispatchSendingAsync("unrelated work", null, DiscoveryUrl)
            .WaitAsync(Patience);

        Assert.True(unaffected.IsSuccess);

    }

    [Fact]
    public async Task DispatchSendingAsync_CallbackModeWhenThePeerSettlesBeforeTheCallbackIsRegistered_StillSettles()
    {

        using SettleBeforeRegistrationAgentHandler agentHandler = new();

        using TestServer server = await CreateFakeRemoteAgentServerAsync(agentHandler, advertisesPush: true);

        using HttpMessageHandler serverHandler = server.CreateHandler();

        // The peer finishes in the window between SendMessage and the callback registration, so the
        // transition that would have notified this instance happened before there was anywhere to post it.
        using CallbackRegistrationCapturingHandler handler = new(
            serverHandler,
            agentHandler.SendMessageAnswered,
            agentHandler.Settled);

        A2AClientService client = CreateClient(
            handler,
            Settings(pushEnabled: true, callbackBaseUrl: $"https://{PeerCallbackHost}"),
            new A2ASendingCallbackRegistry(NullLogger<A2ASendingCallbackRegistry>.Instance));

        Result<A2ADispatchResult> result = await client
            .DispatchSendingAsync("work the peer finishes instantly", null, DiscoveryUrl, mode: A2ADispatchMode.Callback)
            .WaitAsync(Patience);

        // The snapshot SendMessage handed back said `submitted`, and no notification will ever arrive for
        // a transition that predates the registration — so trusting it is a permanent wait. The
        // authoritative state has to be read once before waiting on a signal that has been and gone.
        Assert.True(result.IsSuccess);

        Assert.Equal("instant answer", result.Value.ResponseText);

    }

    [Fact]
    public async Task DispatchSendingAsync_CallbackModeAgainstAPeerThatCannotAcceptOne_WaitsInlineInstead()
    {

        using GateAgentHandler agentHandler = new();

        agentHandler.Release("answered inline");

        using TestServer server = await CreateFakeRemoteAgentServerAsync(agentHandler, advertisesPush: false);

        using HttpMessageHandler serverHandler = server.CreateHandler();

        using CallbackRegistrationCapturingHandler handler = new(serverHandler);

        A2AClientService client = CreateClient(
            handler,
            Settings(pushEnabled: true, callbackBaseUrl: $"https://{PeerCallbackHost}"),
            new A2ASendingCallbackRegistry(NullLogger<A2ASendingCallbackRegistry>.Instance));

        Result<A2ADispatchResult> result = await client
            .DispatchSendingAsync("do the thing", null, DiscoveryUrl, mode: A2ADispatchMode.Callback)
            .WaitAsync(Patience);

        // Peer capability decides. A peer that never advertised push notifications is not asked for one,
        // and the Sending behaves exactly as it did before callback mode existed.
        Assert.True(result.IsSuccess);

        Assert.Equal("answered inline", result.Value.ResponseText);

        Assert.Null(handler.ConfigId);

    }

    [Fact]
    public async Task DispatchSendingAsync_CallbackModeWithNoCallbackBaseUrl_WaitsInlineInstead()
    {

        using GateAgentHandler agentHandler = new();

        agentHandler.Release("answered inline");

        using TestServer server = await CreateFakeRemoteAgentServerAsync(agentHandler, advertisesPush: true);

        using HttpMessageHandler serverHandler = server.CreateHandler();

        using CallbackRegistrationCapturingHandler handler = new(serverHandler);

        A2AClientService client = CreateClient(
            handler,
            Settings(pushEnabled: true),
            new A2ASendingCallbackRegistry(NullLogger<A2ASendingCallbackRegistry>.Instance));

        Result<A2ADispatchResult> result = await client
            .DispatchSendingAsync("do the thing", null, DiscoveryUrl, mode: A2ADispatchMode.Callback)
            .WaitAsync(Patience);

        // Without a reachable base URL this instance cannot tell a peer where to post, so asking it to
        // would register a callback that can never be delivered.
        Assert.True(result.IsSuccess);

        Assert.Null(handler.ConfigId);

    }

    [Fact]
    public async Task DispatchSendingAsync_CallbackModeCancelled_StillCancelsTheRemoteTask()
    {

        using GateAgentHandler agentHandler = new();

        using TestServer server = await CreateFakeRemoteAgentServerAsync(agentHandler, advertisesPush: true);

        using HttpMessageHandler serverHandler = server.CreateHandler();

        using CallbackRegistrationCapturingHandler handler = new(serverHandler);

        A2AClientService client = CreateClient(
            handler,
            Settings(pushEnabled: true, callbackBaseUrl: $"https://{PeerCallbackHost}"),
            new A2ASendingCallbackRegistry(NullLogger<A2ASendingCallbackRegistry>.Instance));

        using CancellationTokenSource cts = new();

        Task<Result<A2ADispatchResult>> dispatch = client.DispatchSendingAsync(
            "long running work",
            null,
            DiscoveryUrl,
            cancellationToken: cts.Token,
            mode: A2ADispatchMode.Callback);

        Assert.True(await handler.WaitForRegistrationAsync(Patience), "the client never registered a callback.");

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatch);

        // Waiting on a callback rather than a poll does not change who owes the peer a cancel (issue #12).
        Assert.True(
            await agentHandler.WaitForCancelAsync(TimeSpan.FromSeconds(10)),
            "callback mode did not cancel the remote task after local cancellation.");

    }

    [Fact]
    public void CallbackPath_IsDerivedFromTheConfiguredServerPath()
    {

        Assert.Equal(
            "/api/conclave/a2a/callbacks",
            A2AClientService.ResolveCallbackPath(new ConclaveA2ASettings()));

        // An operator path outside /api is mounted under it, exactly as the server surface is (§5.7.1).
        Assert.Equal(
            "/api/peers/callbacks",
            A2AClientService.ResolveCallbackPath(new ConclaveA2ASettings { ServerPath = "/peers" }));

        // "/apiary" merely starts with the same four characters as "/api"; it is not a path inside it.
        // A2AServerEndpoints.ResolveServerPath mounts it at /api/apiary/a2a, and the callback route has
        // to land in the same place or a peer is handed a URL nothing is listening on.
        Assert.Equal(
            "/api/apiary/a2a/callbacks",
            A2AClientService.ResolveCallbackPath(new ConclaveA2ASettings { ServerPath = "/apiary/a2a" }));

    }

    // ── harness ────────────────────────────────────────────────────────────────────────────────────

    private static ArcanumSettings Settings(
        bool pushEnabled,
        string? callbackBaseUrl = null,
        string[]? allowedRemoteAgents = null) => new()
    {
        Features = new FeatureSettings
        {
            Conclave = true,
            A2AClient = true,
            A2AServer = true,
        },
        Integrations = new IntegrationSettings
        {
            A2A = new A2AIntegrationSettings
            {
                PushNotifications = pushEnabled,
                PushCallbackBaseUrl = callbackBaseUrl ?? string.Empty,
                AllowedRemoteAgents = allowedRemoteAgents ?? [],
            },
        },
    };

    private static A2APushNotificationRegistry CreateRegistry(
        bool pushEnabled,
        string[]? allowedRemoteAgents = null) =>
        new(
            new TestOptionsMonitor<ArcanumSettings>(Settings(pushEnabled, allowedRemoteAgents: allowedRemoteAgents)),
            NullLogger<A2APushNotificationRegistry>.Instance);

    private static A2AClientService CreateClient(
        HttpMessageHandler handler,
        ArcanumSettings settings,
        A2ASendingCallbackRegistry callbacks) =>
        new(
            new FakeHttpClientFactory(handler),
            new TestOptionsMonitor<ArcanumSettings>(settings),
            NullLogger<A2AClientService>.Instance,
            scopeFactory: null,
            callbacks);

    private static async Task<TestServer> CreateFakeRemoteAgentServerAsync(
        IAgentHandler agentHandler,
        bool advertisesPush)
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
            Capabilities = new AgentCapabilities { Streaming = false, PushNotifications = advertisesPush },
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

                        // The peer is an Arcanum-shaped server so it can actually accept a push config —
                        // the SDK's own server refuses every push-notification method.
                        ArcanumA2AServer server = new(
                            agentHandler,
                            new InMemoryTaskStore(),
                            new ChannelEventNotifier(),
                            new A2APushNotificationRegistry(
                                new TestOptionsMonitor<ArcanumSettings>(
                                    Settings(pushEnabled: advertisesPush, allowedRemoteAgents: [$"https://{PeerCallbackHost}"])),
                                NullLogger<A2APushNotificationRegistry>.Instance),
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

    /// <summary>Records the config id and secret the client handed the peer.</summary>
    /// <param name="sendAnswered">
    /// Completed once the peer has answered a <c>SendMessage</c>, so a test double can hold its remote
    /// task open until the client has the submitted snapshot in hand.
    /// </param>
    /// <param name="beforeRegistering">
    /// Awaited before the callback registration reaches the peer, so a test can put a remote transition
    /// inside the window between the send and the registration.
    /// </param>
    private sealed class CallbackRegistrationCapturingHandler(
        HttpMessageHandler inner,
        TaskCompletionSource? sendAnswered = null,
        Task? beforeRegistering = null) : DelegatingHandler(inner)
    {

        private readonly TaskCompletionSource _registered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _initialTaskRead = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string? ConfigId { get; private set; }

        public string? Token { get; private set; }

        public async Task<bool> WaitForRegistrationAsync(TimeSpan timeout)
        {

            Task completed = await Task.WhenAny(_registered.Task, Task.Delay(timeout)).ConfigureAwait(false);

            return ReferenceEquals(completed, _registered.Task);

        }

        public async Task<bool> WaitForInitialTaskReadAsync(TimeSpan timeout)
        {

            Task completed = await Task.WhenAny(_initialTaskRead.Task, Task.Delay(timeout)).ConfigureAwait(false);

            return ReferenceEquals(completed, _initialTaskRead.Task);

        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {

            string body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (body.Contains("\"CreateTaskPushNotificationConfig\"", StringComparison.Ordinal))
            {

                using JsonDocument document = JsonDocument.Parse(body);

                JsonElement config = document.RootElement
                    .GetProperty("params")
                    .GetProperty("config");

                ConfigId = config.GetProperty("id").GetString();

                Token = config.GetProperty("token").GetString();

                if (beforeRegistering is not null)
                {

                    await beforeRegistering.ConfigureAwait(false);

                }

                // Signalled only after the peer has answered, so a test that acts on this knows the
                // client has finished registering rather than merely started.
                HttpResponseMessage registration = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

                _registered.TrySetResult();

                return registration;

            }

            HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (_registered.Task.IsCompleted
                && body.Contains("\"GetTask\"", StringComparison.Ordinal))
            {

                _initialTaskRead.TrySetResult();

            }

            if (body.Contains("\"SendMessage\"", StringComparison.Ordinal))
            {

                sendAnswered?.TrySetResult();

            }

            return response;

        }

    }

    /// <summary>Captures what an inbound push notification actually carried.</summary>
    private sealed class RecordingCallbackHandler : HttpMessageHandler
    {

        public ConcurrentBag<(string Body, string? Token)> Delivered { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {

            string body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            string? token = request.Headers.TryGetValues(A2APushNotificationHeaders.NotificationToken, out IEnumerable<string>? values)
                ? values.FirstOrDefault()
                : null;

            Delivered.Add((body, token));

            return new HttpResponseMessage(HttpStatusCode.Accepted);

        }

    }

    /// <summary>
    /// Holds the <em>first</em> task open until released and answers every later one immediately, so a
    /// second Sending can prove the first gave its concurrency slot back.
    /// </summary>
    private sealed class GateFirstAgentHandler : IAgentHandler, IDisposable
    {

        private readonly TaskCompletionSource<string> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _entered;

        public async Task ExecuteAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken)
        {

            TaskUpdater updater = new(eventQueue, context.TaskId, context.ContextId);

            await updater.SubmitAsync(cancellationToken).ConfigureAwait(false);

            await updater.StartWorkAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

            string text = Interlocked.Increment(ref _entered) == 1
                ? await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false)
                : "second answer";

            await updater.AddArtifactAsync([Part.FromText(text)], cancellationToken: cancellationToken).ConfigureAwait(false);

            await updater.CompleteAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        }

        public Task CancelAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken)
        {

            _release.TrySetCanceled();

            return Task.CompletedTask;

        }

        public void Release(string text) => _release.TrySetResult(text);

        public void Dispose() => _release.TrySetCanceled();

    }

    /// <summary>
    /// Finishes its remote task in the window between the client's <c>SendMessage</c> and its callback
    /// registration — the fast-peer race a callback can never report, because there was nowhere to post it
    /// when the transition happened.
    /// </summary>
    private sealed class SettleBeforeRegistrationAgentHandler : IAgentHandler, IDisposable
    {

        private readonly TaskCompletionSource _settled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SendMessageAnswered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Settled => _settled.Task;

        public async Task ExecuteAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken)
        {

            TaskUpdater updater = new(eventQueue, context.TaskId, context.ContextId);

            await updater.SubmitAsync(cancellationToken).ConfigureAwait(false);

            // Held only until the client owns the submitted snapshot, so the race is deterministic rather
            // than a hope about which side wins.
            await SendMessageAnswered.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

            await updater.StartWorkAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

            await updater
                .AddArtifactAsync([Part.FromText("instant answer")], cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            await updater.CompleteAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

            _settled.TrySetResult();

        }

        public Task CancelAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public void Dispose()
        {

            SendMessageAnswered.TrySetCanceled();

            _settled.TrySetCanceled();

        }

    }

    /// <summary>Holds the remote task open until released, and records a peer cancel.</summary>
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

}
