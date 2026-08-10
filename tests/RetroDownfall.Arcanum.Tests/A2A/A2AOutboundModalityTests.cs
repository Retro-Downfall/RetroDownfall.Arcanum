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

/// <summary>
/// Issue #65: an outbound Sending states what it will accept back and checks it against the peer's Agent
/// Card <em>before</em> the remote task exists, so a modality mismatch is a named local failure rather
/// than something discovered mid-exchange (or never discovered at all).
/// </summary>
[Collection("OutboundUrlGuardDns")]
public sealed class A2AOutboundModalityTests : IDisposable
{

    private const string FakeAgentHost = "modality-agent.example.test";

    private const string DiscoveryUrl = $"http://{FakeAgentHost}/";

    private readonly IDnsResolver _originalResolver;

    public A2AOutboundModalityTests()
    {

        _originalResolver = OutboundUrlGuard.DnsResolver;

        FakeDnsResolver fake = new();

        fake.Add(FakeAgentHost, IPAddress.Parse("93.184.216.34"));

        OutboundUrlGuard.DnsResolver = fake;

    }

    public void Dispose() => OutboundUrlGuard.DnsResolver = _originalResolver;

    // ---------------------------------------------------------------------------------------------
    // Policy: what gets asked for, and whether the card can produce it.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ValidateOutboundModes_UnstatedPreference_DefaultsToWhatThisInstanceConsumes()
    {

        ConclaveA2ASettings a2a = new();

        AgentCard card = CardWith(defaultOutputModes: ["text/plain"]);

        Result<A2AOutboundModality> result = A2AAgentCardPolicy.ValidateOutboundModes(a2a, card, options: null);

        Assert.True(result.IsSuccess);

        // Stating nothing on the wire leaves the peer free to answer in a modality this instance cannot
        // read; the default is the honest declaration of what it can.
        Assert.Equal(["text/plain"], result.Value.AcceptedOutputModes);

    }

    [Fact]
    public void ValidateOutboundModes_OperatorDeclaredInputModes_BecomeTheOutboundDefault()
    {

        ConclaveA2ASettings a2a = new() { InputModes = ["text/plain", "application/json"] };

        AgentCard card = CardWith(defaultOutputModes: ["application/json"]);

        Result<A2AOutboundModality> result = A2AAgentCardPolicy.ValidateOutboundModes(a2a, card, options: null);

        Assert.True(result.IsSuccess);

        Assert.Equal(["text/plain", "application/json"], result.Value.AcceptedOutputModes);

        Assert.Equal("application/json", result.Value.NegotiatedOutputMode);

    }

    [Fact]
    public void ValidateOutboundModes_Match_ReportsTheNegotiatedMode()
    {

        AgentCard card = CardWith(defaultOutputModes: ["application/json", "text/plain"]);

        Result<A2AOutboundModality> result = A2AAgentCardPolicy.ValidateOutboundModes(
            new ConclaveA2ASettings(),
            card,
            new A2ASendingOptions(AcceptedOutputModes: ["text/plain"]));

        Assert.True(result.IsSuccess);

        Assert.Equal("text/plain", result.Value.NegotiatedOutputMode);

    }

    [Fact]
    public void ValidateOutboundModes_Mismatch_NamesBothSides()
    {

        AgentCard card = CardWith(defaultOutputModes: ["application/json"]);

        Result<A2AOutboundModality> result = A2AAgentCardPolicy.ValidateOutboundModes(
            new ConclaveA2ASettings(),
            card,
            new A2ASendingOptions(AcceptedOutputModes: ["audio/wav"]));

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Sending.ModalityMismatch, result.Error.Code);

        // Both halves, matching the inbound rejection's shape: what was asked for, and what is available.
        Assert.Contains("audio/wav", result.Error.Message, StringComparison.Ordinal);

        Assert.Contains("application/json", result.Error.Message, StringComparison.Ordinal);

    }

    [Fact]
    public void ValidateOutboundModes_CardAdvertisesNothing_IsNotAMismatch()
    {

        // A peer that says nothing has not said "no". Treating silence as refusal would break every
        // dispatch that works today.
        AgentCard card = CardWith(defaultOutputModes: null);

        Result<A2AOutboundModality> result = A2AAgentCardPolicy.ValidateOutboundModes(
            new ConclaveA2ASettings(),
            card,
            new A2ASendingOptions(AcceptedOutputModes: ["audio/wav"]));

        Assert.True(result.IsSuccess);

        Assert.Null(result.Value.NegotiatedOutputMode);

    }

    [Fact]
    public void ValidateOutboundModes_EmptyCardModalityList_IsNotAMismatch()
    {

        AgentCard card = CardWith(defaultOutputModes: []);

        Result<A2AOutboundModality> result = A2AAgentCardPolicy.ValidateOutboundModes(
            new ConclaveA2ASettings(),
            card,
            new A2ASendingOptions(AcceptedOutputModes: ["audio/wav"]));

        Assert.True(result.IsSuccess);

    }

    [Fact]
    public void ValidateOutboundModes_NamedSkill_UsesThatSkillsOutputModes()
    {

        AgentCard card = CardWith(defaultOutputModes: ["text/plain"]);

        card.Skills =
        [
            new AgentSkill { Id = "transcribe", OutputModes = ["application/json"] },
        ];

        Result<A2AOutboundModality> matched = A2AAgentCardPolicy.ValidateOutboundModes(
            new ConclaveA2ASettings(),
            card,
            new A2ASendingOptions(AcceptedOutputModes: ["application/json"], SkillId: "transcribe"));

        Assert.True(matched.IsSuccess);

        Assert.Equal("application/json", matched.Value.NegotiatedOutputMode);

        // The card-level default would have matched; the skill's narrower declaration is what governs.
        Result<A2AOutboundModality> mismatched = A2AAgentCardPolicy.ValidateOutboundModes(
            new ConclaveA2ASettings(),
            card,
            new A2ASendingOptions(AcceptedOutputModes: ["text/plain"], SkillId: "transcribe"));

        Assert.True(mismatched.IsFailure);

        Assert.Equal(ErrorCodes.Sending.ModalityMismatch, mismatched.Error.Code);

    }

    [Fact]
    public void ValidateOutboundModes_SkillWithNoOwnModes_FallsBackToTheCardDefaults()
    {

        AgentCard card = CardWith(defaultOutputModes: ["text/plain"]);

        card.Skills = [new AgentSkill { Id = "anything" }];

        Result<A2AOutboundModality> result = A2AAgentCardPolicy.ValidateOutboundModes(
            new ConclaveA2ASettings(),
            card,
            new A2ASendingOptions(SkillId: "anything"));

        Assert.True(result.IsSuccess);

        Assert.Equal("text/plain", result.Value.NegotiatedOutputMode);

    }

    [Fact]
    public void ValidateOutboundModes_UnknownSkill_FailsNamingWhatTheCardAdvertises()
    {

        AgentCard card = CardWith(defaultOutputModes: ["text/plain"]);

        card.Skills = [new AgentSkill { Id = "apprentice-goal-execution" }];

        Result<A2AOutboundModality> result = A2AAgentCardPolicy.ValidateOutboundModes(
            new ConclaveA2ASettings(),
            card,
            new A2ASendingOptions(SkillId: "summarize"));

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Sending.SkillNotAdvertised, result.Error.Code);

        Assert.Contains("summarize", result.Error.Message, StringComparison.Ordinal);

        Assert.Contains("apprentice-goal-execution", result.Error.Message, StringComparison.Ordinal);

    }

    [Fact]
    public void ValidateOutboundModes_SkillRequestedButCardListsNoSkills_IsNotAMismatch()
    {

        // Same rule as modalities: a card that advertises no skills has not refused one.
        AgentCard card = CardWith(defaultOutputModes: ["text/plain"]);

        card.Skills = null!;

        Result<A2AOutboundModality> result = A2AAgentCardPolicy.ValidateOutboundModes(
            new ConclaveA2ASettings(),
            card,
            new A2ASendingOptions(SkillId: "summarize"));

        Assert.True(result.IsSuccess);

    }

    // ---------------------------------------------------------------------------------------------
    // Client: the check runs before the remote task exists.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task DispatchSendingAsync_PeerCannotProduceTheRequestedMode_FailsBeforeCreatingTheRemoteTask()
    {

        CountingAgentHandler agentHandler = new("never reached");

        AgentCard card = BuildFakeCard();

        card.DefaultOutputModes = ["application/json"];

        using TestServer server = await CreateFakeRemoteAgentServerAsync(agentHandler, card);

        using HttpMessageHandler handler = server.CreateHandler();

        A2AClientService client = CreateClient(handler, EnabledSettings());

        Result<A2ADispatchResult> result = await client.DispatchSendingAsync(
            "do the thing",
            null,
            DiscoveryUrl,
            options: new A2ASendingOptions(AcceptedOutputModes: ["audio/wav"]));

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Sending.ModalityMismatch, result.Error.Code);

        // The whole point: no remote task was created, so nothing is left running on the far side.
        Assert.Equal(0, agentHandler.Executions);

    }

    [Fact]
    public async Task DispatchSendingAsync_PeerAdvertisesNoModalities_IsStillDispatchedTo()
    {

        AgentCard card = BuildFakeCard();

        card.DefaultOutputModes = null!;

        using TestServer server = await CreateFakeRemoteAgentServerAsync(new CountingAgentHandler("answered"), card);

        using HttpMessageHandler handler = server.CreateHandler();

        A2AClientService client = CreateClient(handler, EnabledSettings());

        Result<A2ADispatchResult> result = await client.DispatchSendingAsync("do the thing", null, DiscoveryUrl);

        Assert.True(result.IsSuccess);

        Assert.Equal("answered", result.Value.ResponseText);

    }

    [Fact]
    public async Task DispatchSendingAsync_UnknownSkillId_FailsBeforeCreatingTheRemoteTask()
    {

        CountingAgentHandler agentHandler = new("never reached");

        AgentCard card = BuildFakeCard();

        card.Skills = [new AgentSkill { Id = "apprentice-goal-execution" }];

        using TestServer server = await CreateFakeRemoteAgentServerAsync(agentHandler, card);

        using HttpMessageHandler handler = server.CreateHandler();

        A2AClientService client = CreateClient(handler, EnabledSettings());

        Result<A2ADispatchResult> result = await client.DispatchSendingAsync(
            "do the thing",
            null,
            DiscoveryUrl,
            options: new A2ASendingOptions(SkillId: "transcribe-audio"));

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Sending.SkillNotAdvertised, result.Error.Code);

        Assert.Equal(0, agentHandler.Executions);

    }

    [Fact]
    public async Task DispatchSendingAsync_StatesItsAcceptedOutputModesOnTheWire()
    {

        ConfigurationCapturingAgentHandler agentHandler = new("answered");

        using TestServer server = await CreateFakeRemoteAgentServerAsync(agentHandler);

        using HttpMessageHandler handler = server.CreateHandler();

        A2AClientService client = CreateClient(handler, EnabledSettings());

        Result<A2ADispatchResult> result = await client.DispatchSendingAsync("do the thing", null, DiscoveryUrl);

        Assert.True(result.IsSuccess);

        // Never stating a preference is what let a peer answer in a modality this instance cannot read.
        Assert.Equal(["text/plain"], agentHandler.ObservedAcceptedOutputModes);

    }

    [Fact]
    public async Task ContinueSendingAsync_ValidatesModalitiesToo()
    {

        CountingAgentHandler agentHandler = new("never reached");

        AgentCard card = BuildFakeCard();

        card.DefaultOutputModes = ["application/json"];

        using TestServer server = await CreateFakeRemoteAgentServerAsync(agentHandler, card);

        using HttpMessageHandler handler = server.CreateHandler();

        A2AClientService client = CreateClient(handler, EnabledSettings());

        Result<A2ADispatchResult> result = await client.ContinueSendingAsync(
            DiscoveryUrl,
            "task-1",
            "here is the detail you asked for",
            options: new A2ASendingOptions(AcceptedOutputModes: ["audio/wav"]));

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Sending.ModalityMismatch, result.Error.Code);

        Assert.Equal(0, agentHandler.Executions);

    }

    // ---------------------------------------------------------------------------------------------

    private static AgentCard CardWith(string[]? defaultOutputModes) => new()
    {
        Name = "Peer",
        Version = "1.0.0",
        DefaultOutputModes = defaultOutputModes is null ? null! : [.. defaultOutputModes],
    };

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

    private static A2AClientService CreateClient(HttpMessageHandler handler, ArcanumSettings settings) =>
        new(new FakeHttpClientFactory(handler), new TestOptionsMonitor<ArcanumSettings>(settings), NullLogger<A2AClientService>.Instance);

    private static async Task<TestServer> CreateFakeRemoteAgentServerAsync(IAgentHandler agentHandler, AgentCard? card = null)
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

                        endpoints.MapWellKnownAgentCard(advertised);

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
        Capabilities = new AgentCapabilities { Streaming = false, PushNotifications = false },
        DefaultInputModes = ["text/plain"],
        DefaultOutputModes = ["text/plain"],
    };

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {

        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);

    }

    /// <summary>Completes immediately and counts how many times the peer was actually asked to work.</summary>
    private sealed class CountingAgentHandler(string responseText) : IAgentHandler
    {

        private int _executions;

        public int Executions => Volatile.Read(ref _executions);

        public async Task ExecuteAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken)
        {

            _ = Interlocked.Increment(ref _executions);

            TaskUpdater updater = new(eventQueue, context.TaskId, context.ContextId);

            await updater.SubmitAsync(cancellationToken).ConfigureAwait(false);

            await updater.StartWorkAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

            await updater.AddArtifactAsync([Part.FromText(responseText)], cancellationToken: cancellationToken).ConfigureAwait(false);

            await updater.CompleteAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        }

        public Task CancelAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken) =>
            Task.CompletedTask;

    }

    /// <summary>Records the send configuration the caller put on the wire.</summary>
    private sealed class ConfigurationCapturingAgentHandler(string responseText) : IAgentHandler
    {

        public string[] ObservedAcceptedOutputModes { get; private set; } = [];

        public async Task ExecuteAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken)
        {

            ObservedAcceptedOutputModes = [.. context.Configuration?.AcceptedOutputModes ?? []];

            TaskUpdater updater = new(eventQueue, context.TaskId, context.ContextId);

            await updater.SubmitAsync(cancellationToken).ConfigureAwait(false);

            await updater.StartWorkAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

            await updater.AddArtifactAsync([Part.FromText(responseText)], cancellationToken: cancellationToken).ConfigureAwait(false);

            await updater.CompleteAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        }

        public Task CancelAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken) =>
            Task.CompletedTask;

    }

}
