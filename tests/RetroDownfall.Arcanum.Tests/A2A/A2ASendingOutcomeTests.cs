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
/// Outbound Sending outcomes: cost attribution, in-flight progress, and continuation of a
/// remote that stops to ask for something.
/// </summary>
[Collection("OutboundUrlGuardDns")]
public sealed class A2ASendingOutcomeTests : IDisposable
{

    private const string FakeAgentHost = "cost-agent.example.test";

    private const string DiscoveryUrl = $"http://{FakeAgentHost}/";

    private readonly IDnsResolver _originalResolver;

    public A2ASendingOutcomeTests()
    {

        _originalResolver = OutboundUrlGuard.DnsResolver;

        FakeDnsResolver fake = new();

        fake.Add(FakeAgentHost, IPAddress.Parse("93.184.216.34"));

        OutboundUrlGuard.DnsResolver = fake;

    }

    public void Dispose() => OutboundUrlGuard.DnsResolver = _originalResolver;

    private static ArcanumSettings EnabledSettings() => new()
    {
        Features = new FeatureSettings { Conclave = true, A2AClient = true },
    };

    // ── #60 cost attribution ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PeerReportsUsage_CostIsKnownAndCarriesTheReportedFigures()
    {

        using TestServer server = await CreateAgentAsync(new UsageReportingAgentHandler(totalTokens: 4321, costUsd: 0.0125m));

        using HttpMessageHandler handler = server.CreateHandler();

        A2AClientService client = CreateClient(handler);

        Result<A2ADispatchResult> result = await client.DispatchSendingAsync("do the thing", null, DiscoveryUrl);

        Assert.True(result.IsSuccess);

        Assert.True(result.Value.RemoteCost.IsKnown);

        Assert.Equal(4321, result.Value.RemoteCost.TotalTokens);

        Assert.Equal(0.0125m, result.Value.RemoteCost.CostUsd);

    }

    [Fact]
    public async Task PeerReportsNothing_CostIsExplicitlyUnknownRatherThanZero()
    {

        using TestServer server = await CreateAgentAsync(new SilentAgentHandler("done"));

        using HttpMessageHandler handler = server.CreateHandler();

        A2AClientService client = CreateClient(handler);

        Result<A2ADispatchResult> result = await client.DispatchSendingAsync("do the thing", null, DiscoveryUrl);

        Assert.True(result.IsSuccess);

        // Zero would read as "this delegated inference was free", which is never something the peer said.
        Assert.False(result.Value.RemoteCost.IsKnown);

        Assert.Null(result.Value.RemoteCost.TotalTokens);

        Assert.Null(result.Value.RemoteCost.CostUsd);

        Assert.Contains("unknown", result.Value.RemoteCost.Describe(), StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task SettledSending_StampsDistinctDispatchAndSettleInstants()
    {

        using TestServer server = await CreateAgentAsync(new SilentAgentHandler("done"));

        using HttpMessageHandler handler = server.CreateHandler();

        A2AClientService client = CreateClient(handler);

        Result<A2ADispatchResult> result = await client.DispatchSendingAsync("do the thing", null, DiscoveryUrl);

        Assert.True(result.IsSuccess);

        // Both frames used to share one timestamp, so remote wall-clock was underivable (issue #60).
        Assert.NotEqual(default, result.Value.DispatchedAt);

        Assert.NotEqual(default, result.Value.SettledAt);

        Assert.NotNull(result.Value.RemoteDuration);

        Assert.True(result.Value.RemoteDuration >= TimeSpan.Zero);

    }

    [Theory]
    [InlineData("\"not-an-object\"")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"totalTokens": "many"}""")]
    [InlineData("""{"totalTokens": -5}""")]
    public void MalformedRemoteUsage_ReadsAsUnknownRatherThanThrowing(string rawJson)
    {

        Dictionary<string, JsonElement> metadata = new()
        {
            [A2ASendingUsageMetadata.MetadataKey] = JsonDocument.Parse(rawJson).RootElement.Clone(),
        };

        Assert.False(A2ASendingUsageMetadata.Read(metadata).IsKnown);

    }

    [Fact]
    public void EmptyUsageBlock_IsUnknownRatherThanAFreeSending()
    {

        Dictionary<string, JsonElement> metadata = [];

        A2ASendingUsageMetadata.Write(metadata, totalTokens: null, costUsd: null);

        Assert.False(A2ASendingUsageMetadata.Read(metadata).IsKnown);

    }

    // ── #61 in-flight progress ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LongRunningSending_ReportsProgressOnStateChangesAndNotOnUnchangedPolls()
    {

        using StagedAgentHandler agent = new();

        using TestServer server = await CreateAgentAsync(agent);

        using HttpMessageHandler handler = server.CreateHandler();

        A2AClientService client = CreateClient(handler);

        ConcurrentQueue<A2ASendingProgress> observed = [];

        Progress<A2ASendingProgress> progress = new(observed.Enqueue);

        Task<Result<A2ADispatchResult>> dispatch =
            client.DispatchSendingAsync("long work", null, DiscoveryUrl, progress: progress);

        // Several polls happen against an unchanging Working state before the agent finishes.
        await Task.Delay(TimeSpan.FromMilliseconds(600));

        agent.Release("finished");

        Result<A2ADispatchResult> result = await dispatch.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(result.IsSuccess);

        A2ASendingProgress[] frames = [.. observed];

        Assert.NotEmpty(frames);

        // One frame per remote transition, not one per poll: a 2 s backoff against a long remote task
        // would otherwise flood the Chronicle with identical frames.
        string[] states = [.. frames.Select(static f => f.RemoteState)];

        Assert.Equal(states.Length, states.Distinct(StringComparer.Ordinal).Count());

        Assert.Contains("completed", states, StringComparer.Ordinal);

        Assert.All(frames, f => Assert.Equal(A2ASendingDirection.Outbound, f.Direction));

    }

    [Fact]
    public async Task ProgressFrames_CarryNoCredentialPromptBodyOrPrivateEndpointDetail()
    {

        const string envVar = "ARCANUM_TEST_A2A_PROGRESS_KEY";

        const string secret = "super-secret-peer-key";

        const string goal = "delete the production database, obviously not";

        global::System.Environment.SetEnvironmentVariable(envVar, secret);

        try
        {

            using StagedAgentHandler agent = new();

            using TestServer server = await CreateAgentAsync(agent);

            using HttpMessageHandler handler = server.CreateHandler();

            ArcanumSettings settings = EnabledSettings();

            settings.Integrations!.A2A!.OutboundCredentialEnvironmentVariable = envVar;

            A2AClientService client = CreateClient(handler, settings);

            ConcurrentQueue<A2ASendingProgress> observed = [];

            Task<Result<A2ADispatchResult>> dispatch = client.DispatchSendingAsync(
                goal,
                null,
                DiscoveryUrl,
                progress: new Progress<A2ASendingProgress>(observed.Enqueue));

            await Task.Delay(TimeSpan.FromMilliseconds(300));

            agent.Release("done");

            await dispatch.WaitAsync(TimeSpan.FromSeconds(30));

            foreach (A2ASendingProgress frame in observed)
            {

                string rendered = $"{frame.AgentUrl}|{frame.TaskId}|{frame.RemoteState}";

                Assert.DoesNotContain(secret, rendered, StringComparison.Ordinal);

                Assert.DoesNotContain("production database", rendered, StringComparison.OrdinalIgnoreCase);

            }

        }
        finally
        {

            global::System.Environment.SetEnvironmentVariable(envVar, null);

        }

    }

    // ── #64 continuation ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ContinuableDispatch_ReturnsAContinuationInsteadOfEndingTheSending()
    {

        using ContinuableAgentHandler agent = new("which environment?", "staging it is");

        using TestServer server = await CreateAgentAsync(agent);

        using HttpMessageHandler handler = server.CreateHandler();

        A2AClientService client = CreateClient(handler);

        Result<A2ADispatchResult> parked = await client
            .DispatchSendingAsync("deploy the thing", null, DiscoveryUrl, mode: A2ADispatchMode.Continuable)
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(parked.IsSuccess);

        Assert.NotNull(parked.Value.Continuation);

        Assert.Equal(A2AContinuationNeed.Input, parked.Value.Continuation!.Need);

        Assert.Contains("which environment?", parked.Value.Continuation.Reason, StringComparison.Ordinal);

        // The blocking mode cancels here; continuable mode must leave the remote task alive to answer.
        Assert.False(agent.WasCancelled);

    }

    [Fact]
    public async Task ContinuedSending_ReachesATerminalResultOnTheSameRemoteTask()
    {

        using ContinuableAgentHandler agent = new("which environment?", "deployed to staging");

        using TestServer server = await CreateAgentAsync(agent);

        using HttpMessageHandler handler = server.CreateHandler();

        A2AClientService client = CreateClient(handler);

        Result<A2ADispatchResult> parked = await client
            .DispatchSendingAsync("deploy the thing", null, DiscoveryUrl, mode: A2ADispatchMode.Continuable)
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(parked.IsSuccess);

        string taskId = parked.Value.Continuation!.TaskId;

        Result<A2ADispatchResult> finished = await client
            .ContinueSendingAsync(DiscoveryUrl, taskId, "staging")
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(finished.IsSuccess, finished.IsFailure ? $"{finished.Error.Code}: {finished.Error.Message}" : string.Empty);

        Assert.Equal(taskId, finished.Value.TaskId);

        Assert.Contains("deployed to staging", finished.Value.ResponseText, StringComparison.Ordinal);

        Assert.Equal("staging", agent.ObservedFollowUp);

    }

    [Fact]
    public async Task BlockingDispatch_StillEndsAtInputRequiredAndCancelsTheRemoteTask()
    {

        using ContinuableAgentHandler agent = new("which environment?", "never reached");

        using TestServer server = await CreateAgentAsync(agent);

        using HttpMessageHandler handler = server.CreateHandler();

        A2AClientService client = CreateClient(handler);

        // Default mode must not change: an Apprentice that cannot answer still needs the Sending to end
        // rather than pin a concurrency slot forever (issue #12).
        Result<A2ADispatchResult> result = await client
            .DispatchSendingAsync("deploy the thing", null, DiscoveryUrl)
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Sending.TaskRejected, result.Error.Code);

        Assert.True(await agent.WaitForCancelAsync(TimeSpan.FromSeconds(10)));

    }

    [Fact]
    public async Task ContinuedSending_KeepsTheOneDurableRecordTheDispatchOpened()
    {

        using ContinuableAgentHandler agent = new("which environment?", "deployed to staging");

        using TestServer server = await CreateAgentAsync(agent);

        using HttpMessageHandler handler = server.CreateHandler();

        OutboundSendingLedger ledger = new();

        A2AClientService client = CreateClient(handler, ledger: ledger);

        Result<A2ADispatchResult> parked = await client
            .DispatchSendingAsync("deploy the thing", null, DiscoveryUrl, mode: A2ADispatchMode.Continuable)
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(parked.IsSuccess);

        string taskId = parked.Value.Continuation!.TaskId;

        // The continuable branch deliberately leaves the row open so the answer can find it.
        Assert.Equal([taskId], ledger.Registered);

        Result<A2ADispatchResult> finished = await client
            .ContinueSendingAsync(DiscoveryUrl, taskId, "staging")
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(finished.IsSuccess, finished.IsFailure ? $"{finished.Error.Code}: {finished.Error.Message}" : string.Empty);

        // One remote task is one Sending, and DESIGN §22.2 counts one record per Sending exactly once. A
        // second row for the same task is an orphan nothing ever closes: reconciliation eventually claims
        // it, issues tasks/cancel against a task that may still be running, and tallies an extra unpriced
        // delegated Sending against the operator's budget.
        Assert.Equal([taskId], ledger.Registered);

        Assert.Single(ledger.Settled);

        Assert.Empty(ledger.OpenEntries);

    }

    // ── harness ────────────────────────────────────────────────────────────────────────────────────

    private static A2AClientService CreateClient(
        HttpMessageHandler handler,
        ArcanumSettings? settings = null,
        IA2ASendingLedger? ledger = null) =>
        new(
            new SingleHandlerHttpClientFactory(handler),
            new TestOptionsMonitor<ArcanumSettings>(settings ?? EnabledSettings()),
            NullLogger<A2AClientService>.Instance,
            ledger is null ? null : ScopeFactoryFor(ledger));

    private static IServiceScopeFactory ScopeFactoryFor(IA2ASendingLedger ledger)
    {

        ServiceCollection services = new();

        services.AddSingleton(ledger);

        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

    }

    /// <summary>
    /// The outbound half of the durable ledger, kept honestly enough that "one row per Sending" is a
    /// claim this test can actually check.
    /// </summary>
    private sealed class OutboundSendingLedger : IA2ASendingLedger
    {

        private readonly Dictionary<Guid, string> _open = [];

        public List<string> Registered { get; } = [];

        public List<string> Settled { get; } = [];

        public IReadOnlyCollection<Guid> OpenEntries => _open.Keys;

        public Task<A2ASendingLedgerEntry> RegisterOutboundAsync(
            string remoteTaskId,
            string agentUrl,
            Guid? budgetReservationId = null,
            CancellationToken cancellationToken = default)
        {

            Registered.Add(remoteTaskId);

            A2ASendingLedgerEntry entry = new(Guid.NewGuid(), "test");

            _open[entry.OperationId] = remoteTaskId;

            return Task.FromResult(entry);

        }

        public Task SettleOutboundAsync(
            A2ASendingLedgerEntry entry,
            A2ARemoteCost cost,
            CancellationToken cancellationToken = default)
        {

            if (_open.Remove(entry.OperationId, out string? taskId))
            {

                Settled.Add(taskId);

            }

            return Task.CompletedTask;

        }

        public Task ReleaseAsync(A2ASendingLedgerEntry entry, CancellationToken cancellationToken = default)
        {

            _open.Remove(entry.OperationId);

            return Task.CompletedTask;

        }

        public Task<A2ASendingLedgerEntry> RegisterInboundAsync(
            string taskId,
            Guid apprenticeId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new A2ASendingLedgerEntry(Guid.NewGuid(), "test"));

        public Task MarkParkedAsync(
            A2ASendingLedgerEntry entry,
            string? contextId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<A2AParkedSending?> FindParkedInboundAsync(
            string taskId,
            bool takeLease = true,
            CancellationToken cancellationToken = default) => Task.FromResult<A2AParkedSending?>(null);

        public Task<Guid?> FindInboundApprenticeAsync(string taskId, CancellationToken cancellationToken = default) =>
            Task.FromResult<Guid?>(null);

        public Task RecordOutboundCallbackAsync(
            A2ASendingLedgerEntry entry,
            string callbackConfigId,
            string callbackTokenHash,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<A2AOutboundCallback?> FindOutboundCallbackAsync(
            string callbackConfigId,
            CancellationToken cancellationToken = default) => Task.FromResult<A2AOutboundCallback?>(null);

        public Task<A2ASendingLedgerEntry> FindOpenOutboundAsync(
            string remoteTaskId,
            CancellationToken cancellationToken = default)
        {

            foreach ((Guid operationId, string taskId) in _open)
            {

                if (string.Equals(taskId, remoteTaskId, StringComparison.Ordinal))
                {

                    return Task.FromResult(new A2ASendingLedgerEntry(operationId, "test"));

                }

            }

            return Task.FromResult<A2ASendingLedgerEntry>(default);

        }

    }

    private static async Task<TestServer> CreateAgentAsync(IAgentHandler agentHandler)
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
            Capabilities = new AgentCapabilities { Streaming = true, PushNotifications = false },
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

    private sealed class SingleHandlerHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {

        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);

    }

    /// <summary>Completes immediately and publishes a usage block the way Arcanum's own server does.</summary>
    private sealed class UsageReportingAgentHandler(long totalTokens, decimal costUsd) : IAgentHandler
    {

        public async Task ExecuteAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken)
        {

            TaskUpdater updater = new(eventQueue, context.TaskId, context.ContextId);

            await updater.SubmitAsync(cancellationToken).ConfigureAwait(false);

            await updater.StartWorkAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

            await updater.AddArtifactAsync([Part.FromText("done")], cancellationToken: cancellationToken).ConfigureAwait(false);

            Message completion = new()
            {
                Role = Role.Agent,
                MessageId = Guid.NewGuid().ToString("N"),
                Parts = [Part.FromText("done")],
                Metadata = [],
            };

            A2ASendingUsageMetadata.Write(completion.Metadata!, totalTokens, costUsd);

            await updater.CompleteAsync(completion, cancellationToken).ConfigureAwait(false);

        }

        public Task CancelAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken) =>
            Task.CompletedTask;

    }

    /// <summary>Completes immediately with no usage block at all.</summary>
    private sealed class SilentAgentHandler(string responseText) : IAgentHandler
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

    /// <summary>Sits in Working until released, so the client polls an unchanging state several times.</summary>
    private sealed class StagedAgentHandler : IAgentHandler, IDisposable
    {

        private readonly TaskCompletionSource<string> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

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

            _release.TrySetCanceled();

            return Task.CompletedTask;

        }

        public void Release(string text) => _release.TrySetResult(text);

        public void Dispose() => _release.TrySetCanceled();

    }

    /// <summary>
    /// Parks at <c>input-required</c>, then finishes when a continuation message arrives for the same task.
    /// </summary>
    private sealed class ContinuableAgentHandler(string question, string answerText) : IAgentHandler, IDisposable
    {

        private readonly TaskCompletionSource _cancelObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool WasCancelled { get; private set; }

        public string? ObservedFollowUp { get; private set; }

        public async Task ExecuteAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken)
        {

            TaskUpdater updater = new(eventQueue, context.TaskId, context.ContextId);

            if (context.IsContinuation)
            {

                ObservedFollowUp = context.UserText?.Trim();

                // The SDK opens this request's response stream on the first Submit; a continuation that
                // skips it emits nothing the caller can observe.
                await updater.SubmitAsync(cancellationToken).ConfigureAwait(false);

                await updater.StartWorkAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

                await updater.AddArtifactAsync([Part.FromText(answerText)], cancellationToken: cancellationToken).ConfigureAwait(false);

                await updater.CompleteAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

                return;

            }

            await updater.SubmitAsync(cancellationToken).ConfigureAwait(false);

            await updater.StartWorkAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

            await updater.RequireInputAsync(
                new Message
                {
                    Role = Role.Agent,
                    MessageId = Guid.NewGuid().ToString("N"),
                    Parts = [Part.FromText(question)],
                },
                cancellationToken).ConfigureAwait(false);

        }

        public Task CancelAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken)
        {

            WasCancelled = true;

            _cancelObserved.TrySetResult();

            return Task.CompletedTask;

        }

        public async Task<bool> WaitForCancelAsync(TimeSpan timeout)
        {

            Task completed = await Task.WhenAny(_cancelObserved.Task, Task.Delay(timeout)).ConfigureAwait(false);

            return ReferenceEquals(completed, _cancelObserved.Task);

        }

        public void Dispose() => _cancelObserved.TrySetCanceled();

    }

}
