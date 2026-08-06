using System.Text.Json;
using System.Threading.Channels;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.A2A;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

namespace RetroDownfall.Arcanum.Tests.A2A;

/// <summary>
/// The Chronicle side of a Sending: live <c>sendingProgress</c> frames while it runs (issue #61) and
/// terminal frames that carry distinct instants and an explicit cost outcome (issue #60).
/// </summary>
public sealed class A2ASendingChronicleTests
{

    private static readonly Guid ApprenticeId = Guid.NewGuid();

    // ── #61 live progress on the caller's Chronicle ────────────────────────────────────────────────

    [Fact]
    public async Task DispatchSending_PublishesRemoteStateChangesOntoTheCallingApprenticesChronicle()
    {

        ChronicleHub hub = new();

        List<ApprenticeEvent> observed = [];

        using CancellationTokenSource subscription = new();

        Task collector = CollectAsync(hub, observed, subscription.Token);

        ProgressReportingA2AClient client = new(
        [
            new A2ASendingProgress("https://peer.example.test/", "t-9", "submitted", A2ASendingDirection.Outbound, DateTimeOffset.UnixEpoch),
            new A2ASendingProgress("https://peer.example.test/", "t-9", "working", A2ASendingDirection.Outbound, DateTimeOffset.UnixEpoch),
        ]);

        await CallDispatchSendingAsync(client, hub);

        await WaitForAsync(observed, 2);

        await subscription.CancelAsync();

        await collector;

        Assert.Equal(2, observed.Count);

        Assert.All(observed, e => Assert.Equal(ApprenticeEventType.SendingProgress, e.Type));

        Assert.Equal(["submitted", "working"], observed.Select(static e => e.SendingState));

        Assert.All(observed, e => Assert.Equal("outbound", e.SendingDirection));

        Assert.All(observed, e => Assert.Equal("t-9", e.Summary));

    }

    [Fact]
    public async Task DispatchSendingWithoutAnApprenticeCaller_PublishesNoProgressFrames()
    {

        ChronicleHub hub = new();

        List<ApprenticeEvent> observed = [];

        using CancellationTokenSource subscription = new();

        Task collector = CollectAsync(hub, observed, subscription.Token);

        ProgressReportingA2AClient client = new(
        [
            new A2ASendingProgress("https://peer.example.test/", "t-9", "working", A2ASendingDirection.Outbound, DateTimeOffset.UnixEpoch),
        ]);

        // An operator-initiated Sending has no Apprentice Chronicle to publish onto.
        await CallDispatchSendingAsync(client, hub, bindApprentice: false);

        await Task.Delay(TimeSpan.FromMilliseconds(200));

        await subscription.CancelAsync();

        await collector;

        Assert.Empty(observed);

    }

    // ── #60 terminal frames ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CompletedSending_StampsDistinctInstantsAndDerivableRemoteDuration()
    {

        DateTimeOffset dispatched = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

        IReadOnlyList<ApprenticeEvent> frames = SendingChronicleFrames.Build(
            ApprenticeId,
            Payload(succeeded: true, dispatchedAt: dispatched, settledAt: dispatched.AddSeconds(90)),
            DateTimeOffset.UnixEpoch);

        Assert.Equal(2, frames.Count);

        Assert.Equal(ApprenticeEventType.SendingDispatched, frames[0].Type);

        Assert.Equal(ApprenticeEventType.SendingCompleted, frames[1].Type);

        // One shared timestamp made remote wall-clock underivable from the Chronicle (issue #60).
        Assert.Equal(dispatched, frames[0].Timestamp);

        Assert.Equal(dispatched.AddSeconds(90), frames[1].Timestamp);

        Assert.Equal(90_000, frames[1].DurationMs);

    }

    [Fact]
    public void SendingWithNoReportedCost_RecordsUnknownRatherThanZero()
    {

        IReadOnlyList<ApprenticeEvent> frames = SendingChronicleFrames.Build(
            ApprenticeId,
            Payload(succeeded: true),
            DateTimeOffset.UnixEpoch);

        Assert.False(frames[1].RemoteCostKnown);

        Assert.Null(frames[1].RemoteTotalTokens);

        Assert.Null(frames[1].RemoteCostUsd);

    }

    [Fact]
    public void SendingWithReportedCost_CarriesTheFiguresOnTheTerminalFrame()
    {

        IReadOnlyList<ApprenticeEvent> frames = SendingChronicleFrames.Build(
            ApprenticeId,
            Payload(succeeded: true, costKnown: true, tokens: 4321, costUsd: 0.0125m),
            DateTimeOffset.UnixEpoch);

        Assert.True(frames[1].RemoteCostKnown);

        Assert.Equal(4321, frames[1].RemoteTotalTokens);

        Assert.Equal(0.0125m, frames[1].RemoteCostUsd);

    }

    [Fact]
    public void FailedSending_CarriesTheReasonAndStillRecordsCostAsUnknown()
    {

        IReadOnlyList<ApprenticeEvent> frames = SendingChronicleFrames.Build(
            ApprenticeId,
            Payload(succeeded: false, error: "the remote refused"),
            DateTimeOffset.UnixEpoch);

        Assert.Equal(ApprenticeEventType.SendingFailed, frames[1].Type);

        Assert.Equal("the remote refused", frames[1].Error);

        Assert.False(frames[1].RemoteCostKnown);

    }

    [Fact]
    public void MalformedToolPayload_ProducesNoFramesRatherThanThrowing()
    {

        Assert.Empty(SendingChronicleFrames.Build(ApprenticeId, "not json", DateTimeOffset.UnixEpoch));

        Assert.Empty(SendingChronicleFrames.Build(ApprenticeId, "null", DateTimeOffset.UnixEpoch));

    }

    // ── harness ────────────────────────────────────────────────────────────────────────────────────

    private static string Payload(
        bool succeeded,
        DateTimeOffset? dispatchedAt = null,
        DateTimeOffset? settledAt = null,
        bool costKnown = false,
        long? tokens = null,
        decimal? costUsd = null,
        string? error = null) =>
        JsonSerializer.Serialize(
            new DispatchSendingResultWire
            {
                AgentUrl = "https://peer.example.test/",
                TaskId = "t-9",
                Succeeded = succeeded,
                Response = succeeded ? "done" : null,
                Error = error,
                CostKnown = costKnown,
                RemoteTotalTokens = tokens,
                RemoteCostUsd = costUsd,
                DispatchedAt = dispatchedAt,
                SettledAt = settledAt,
            },
            McpJsonSerializerContext.Default.DispatchSendingResultWire);

    private static async Task CollectAsync(ChronicleHub hub, List<ApprenticeEvent> sink, CancellationToken cancellationToken)
    {

        try
        {

            await foreach (ApprenticeEvent @event in hub.SubscribeAsync(ApprenticeId, cancellationToken))
            {

                sink.Add(@event);

            }

        }
        catch (OperationCanceledException)
        {
        }

    }

    private static async Task WaitForAsync(List<ApprenticeEvent> sink, int count)
    {

        for (int i = 0; i < 200 && sink.Count < count; i++)
        {

            await Task.Delay(TimeSpan.FromMilliseconds(10));

        }

    }

    /// <summary>Drives the real <c>dispatch_sending</c> tool over the real in-process MCP transport.</summary>
    private static async Task CallDispatchSendingAsync(
        IA2AClientService a2aClient,
        ChronicleHub hub,
        bool bindApprentice = true)
    {

        ServiceCollection services = new();

        services.AddSingleton(a2aClient);

        services.AddSingleton(hub);

        IServiceScopeFactory scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        (InProcessMcpTransport transport, ArcanumInternalToolServer server) = InProcessMcpTransport.CreatePair(
            new HumanPromptRegistry(),
            scopeFactory,
            new NoOpPacer(),
            workspaceRootNormalizedOrNull: null,
            listDirectoryMaxPaths: 8,
            intelligenceSettings: ArcanumRuntimeDefaults.Intelligence,
            maxFileReadSizeBytes: 1024,
            conclaveEnabled: true,
            sagaEnabled: false,
            a2aClientEnabled: true,
            attachmentsToolEnabled: false,
            maxJsonRpcLineBytes: 2_097_152,
            logger: NullLogger<ArcanumInternalToolServer>.Instance);

        using CancellationTokenSource lifetime = new();

        Task serverTask = server.RunAsync(lifetime.Token);

        await transport.StartAsync();

        try
        {

            JsonElement callParams = JsonSerializer.SerializeToElement(
                new McpToolsCallParams
                {
                    Name = "dispatch_sending",
                    Arguments = JsonSerializer.SerializeToElement(
                        new DispatchSendingParams { Goal = "do the thing", AgentUrl = "https://peer.example.test/" },
                        McpJsonSerializerContext.Default.DispatchSendingParams),
                },
                McpJsonSerializerContext.Default.McpToolsCallParams);

            JsonRpcRequest request = new()
            {
                Method = "tools/call",
                Params = callParams,
                Id = JsonSerializer.SerializeToElement(1, McpJsonSerializerContext.Default.Int32),
            };

            IDisposable? scope = bindApprentice
                ? ApprenticeToolInvocationAmbient.Begin(new ApprenticeToolInvocationContext(ApprenticeId, []))
                : null;

            try
            {

                await transport.WriteRequestAsync(request);

            }
            finally
            {

                scope?.Dispose();

            }

            McpInboundEnvelope envelope = await transport.InboundReader.ReadAsync();

            Assert.Equal(McpInboundKind.Response, envelope.Kind);

        }
        finally
        {

            await lifetime.CancelAsync();

            try
            {

                await serverTask;

            }
            catch (OperationCanceledException)
            {
            }

            await transport.DisposeAsync();

        }

    }

    private sealed class ProgressReportingA2AClient(IReadOnlyList<A2ASendingProgress> updates) : IA2AClientService
    {

        public Task<Result<A2ADispatchResult>> DispatchSendingAsync(
            string goal,
            string? name,
            string agentUrl,
            IReadOnlyList<string>? delegationChain = null,
            CancellationToken cancellationToken = default,
            IProgress<A2ASendingProgress>? progress = null,
            A2ADispatchMode mode = A2ADispatchMode.Blocking)
        {

            foreach (A2ASendingProgress update in updates)
            {

                progress?.Report(update);

            }

            return Task.FromResult(Result<A2ADispatchResult>.Success(
                new A2ADispatchResult("t-9", "done", A2ARemoteCost.Unknown, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)));

        }

        public Task<Result<A2ADispatchResult>> ContinueSendingAsync(
            string agentUrl,
            string taskId,
            string message,
            IReadOnlyList<string>? delegationChain = null,
            CancellationToken cancellationToken = default,
            IProgress<A2ASendingProgress>? progress = null,
            A2ADispatchMode mode = A2ADispatchMode.Blocking) => throw new NotSupportedException();

        public Task<Result> CancelRemoteTaskAsync(
            string agentUrl,
            string taskId,
            CancellationToken cancellationToken = default) => Task.FromResult(Result.Success());

    }

    private sealed class NoOpPacer : IUnseenServantPacer
    {

        public void SetDynamicInterval(string jobName, int intervalMinutes)
        {
        }

        public int GetEffectiveInterval(UnseenServantJob job) => job.IntervalMinutes;

        public Task HydrateAsync(
            IReadOnlyList<UnseenServantWatermark> watermarks,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

    }

}
