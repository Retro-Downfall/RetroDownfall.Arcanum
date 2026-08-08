using System.Text.Json;
using System.Threading.Channels;

using A2A;

using TaskStatus = A2A.TaskStatus;
using JsonRpcRequest = RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol.JsonRpcRequest;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.CommLink;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.A2A;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.A2A;

/// <summary>
/// Multi-hop A2A delegation cycle detection (issue #59).
/// </summary>
/// <remarks>
/// Direct loops (A &#8594; B &#8594; A) were already caught because B's inbound chain contains A. Three-hop loops
/// were not: the in-process MCP server is workspace-scoped, so an Apprentice spawned by an inbound
/// Sending restarted the chain from empty on its own <c>dispatch_sending</c>, and by the time the work
/// arrived back at A the chain no longer mentioned A. These tests walk the middle hop with production
/// code — real <see cref="ArcanumA2AAgentHandler"/>, real <see cref="ConclaveArchmage"/> checkpoint
/// persistence, real internal MCP tool server — because that is exactly the leg that used to drop it.
/// </remarks>
public sealed class A2ADelegationCycleTests
{

    private const string OriginNode = "origin-node-a";

    private const string DownstreamNode = "downstream-node-c";

    private static ArcanumSettings EnabledSettings() => new()
    {
        Features = new FeatureSettings
        {
            Conclave = true,
            A2AServer = true,
            A2AClient = true,
        },
        Security = new SecuritySettings { CampaignRoots = [Directory.GetCurrentDirectory()] },
    };

    [Fact]
    public async Task ThreeHopCycle_IsRefusedAtTheRepeatedNode()
    {

        ArcanumSettings settings = EnabledSettings();

        // ── Hop 2 (this instance, the middle hop). An upstream agent delegates to us. ──────────────
        RecordingApprenticeRepository repository = new();

        Apprentice spawned = await AcceptInboundSendingAsync(settings, repository, [OriginNode]);

        IReadOnlyList<string> inherited =
            ApprenticeRepository.DeserializeCheckpoint(spawned.CheckpointData)?.DelegationChain ?? [];

        Assert.Equal([OriginNode, ConclaveDelegationChain.NodeId], inherited);

        // ── Hop 3. The spawned Apprentice delegates onward through dispatch_sending. Before #59 this
        // leg emitted a chain of just [this node], and the origin hop vanished. ───────────────────
        IReadOnlyList<string>? forwarded = await DispatchFromApprenticeAsync(spawned.Id, inherited);

        Assert.Equal([OriginNode, ConclaveDelegationChain.NodeId], forwarded);

        // A foreign peer appends its own node exactly as ConclaveDelegationChain.Extend does here.
        string[] chainSeenByOrigin = [.. forwarded!, DownstreamNode];

        // ── Hop 4. The work loops back to a node already in the chain. ────────────────────────────
        (Apprentice? cycled, TaskState? finalState) = await TryAcceptInboundSendingAsync(
            settings,
            new RecordingApprenticeRepository(),
            chainSeenByOrigin);

        Assert.Null(cycled);

        Assert.Equal(TaskState.Rejected, finalState);

    }

    [Fact]
    public async Task LongChainWithoutARepeat_IsAcceptedRatherThanCappedByHopCount()
    {

        // Issue #55 forbids arbitrary depth ceilings: only a repeat is evidence of a loop, so a long
        // but acyclic chain must still be accepted.
        string[] longChain = [.. Enumerable.Range(0, 64).Select(static i => $"node-{i}")];

        RecordingApprenticeRepository repository = new();

        Apprentice spawned = await AcceptInboundSendingAsync(EnabledSettings(), repository, longChain);

        IReadOnlyList<string> inherited =
            ApprenticeRepository.DeserializeCheckpoint(spawned.CheckpointData)?.DelegationChain ?? [];

        Assert.Equal([.. longChain, ConclaveDelegationChain.NodeId], inherited);

    }

    /// <summary>Runs a real inbound Sending through the real handler and returns the Apprentice it minted.</summary>
    private static async Task<Apprentice> AcceptInboundSendingAsync(
        ArcanumSettings settings,
        RecordingApprenticeRepository repository,
        IReadOnlyList<string> inboundChain)
    {

        (Apprentice? spawned, _) = await TryAcceptInboundSendingAsync(settings, repository, inboundChain);

        Assert.NotNull(spawned);

        return spawned!;

    }

    private static async Task<(Apprentice? Spawned, TaskState? FinalState)> TryAcceptInboundSendingAsync(
        ArcanumSettings settings,
        RecordingApprenticeRepository repository,
        IReadOnlyList<string> inboundChain)
    {

        Channel<ApprenticeEvent> chronicle = Channel.CreateUnbounded<ApprenticeEvent>();

        StubApprenticeRuntime runtime = new(chronicle.Reader);

        ServiceCollection services = new();

        services.AddSingleton<IApprenticeRepository>(repository);

        services.AddSingleton<IConclaveArchmage>(
            new ConclaveArchmage(repository, new TestOptionsMonitor<ArcanumSettings>(settings)));

        services.AddSingleton<IApprenticeRuntime>(runtime);

        services.AddSingleton<ISessionRepository>(new StubSessionRepository());

        IServiceScopeFactory scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        ArcanumA2AAgentHandler handler = new(
            scopeFactory,
            new TestOptionsMonitor<ArcanumSettings>(settings),
            NullLogger<ArcanumA2AAgentHandler>.Instance);

        RequestContext context = new()
        {
            Message = new Message
            {
                Role = Role.User,
                MessageId = Guid.NewGuid().ToString("N"),
                Parts = [Part.FromText("delegate this onward")],
            },
            TaskId = Guid.NewGuid().ToString("N"),
            ContextId = Guid.NewGuid().ToString("N"),
            StreamingResponse = false,
        };

        ConclaveDelegationChain.Write(context.Message!, inboundChain);

        // A terminal event is queued up front so the relay loop settles instead of blocking the test.
        chronicle.Writer.TryWrite(new ApprenticeEvent { Type = ApprenticeEventType.ApprenticeCompleted });

        chronicle.Writer.Complete();

        AgentEventQueue queue = new();

        await handler.ExecuteAsync(context, queue, CancellationToken.None);

        queue.Complete();

        TaskState? finalState = null;

        await foreach (StreamResponse response in queue)
        {

            TaskStatus? status = response.PayloadCase switch
            {
                StreamResponseCase.Task => response.Task?.Status,
                StreamResponseCase.StatusUpdate => response.StatusUpdate?.Status,
                _ => null,
            };

            if (status is not null)
            {

                finalState = status.State;

            }

        }

        return (repository.Added.LastOrDefault(), finalState);

    }

    /// <summary>
    /// Calls <c>dispatch_sending</c> over the real in-process MCP transport with the Apprentice context
    /// bound the way <c>ApprenticeService</c> binds it, and returns the chain the tool forwarded.
    /// </summary>
    private static async Task<IReadOnlyList<string>?> DispatchFromApprenticeAsync(
        Guid apprenticeId,
        IReadOnlyList<string> inheritedChain)
    {

        ChainRecordingA2AClient a2aClient = new();

        ServiceCollection services = new();

        services.AddSingleton<IA2AClientService>(a2aClient);

        IServiceScopeFactory scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        (InProcessMcpTransport transport, ArcanumInternalToolServer server) = InProcessMcpTransport.CreatePair(
            new HumanPromptRegistry(),
            scopeFactory,
            new StubPacer(),
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
                        new DispatchSendingParams { Goal = "delegate onward", AgentUrl = "https://peer.example.test/" },
                        McpJsonSerializerContext.Default.DispatchSendingParams),
                },
                McpJsonSerializerContext.Default.McpToolsCallParams);

            JsonRpcRequest request = new()
            {
                Method = "tools/call",
                Params = callParams,
                Id = JsonSerializer.SerializeToElement(1, McpJsonSerializerContext.Default.Int32),
            };

            using (ApprenticeToolInvocationAmbient.Begin(
                new ApprenticeToolInvocationContext(apprenticeId, inheritedChain)))
            {

                await transport.WriteRequestAsync(request);

            }

            McpInboundEnvelope envelope = await transport.InboundReader.ReadAsync();

            Assert.Equal(McpInboundKind.Response, envelope.Kind);

            Assert.Null(envelope.Response!.Error);

            return a2aClient.ObservedChain;

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

    private sealed class ChainRecordingA2AClient : IA2AClientService
    {

        public IReadOnlyList<string>? ObservedChain { get; private set; }

        public Task<Result<A2ADispatchResult>> DispatchSendingAsync(
            string goal,
            string? name,
            string agentUrl,
            IReadOnlyList<string>? delegationChain = null,
            CancellationToken cancellationToken = default,
            IProgress<A2ASendingProgress>? progress = null,
            A2ADispatchMode mode = A2ADispatchMode.Blocking)
        {

            ObservedChain = delegationChain;

            return Task.FromResult(
                Result<A2ADispatchResult>.Success(new A2ADispatchResult("remote-task", "ok")));

        }

        public Task<Result<A2ADispatchResult>> ContinueSendingAsync(
            string agentUrl,
            string taskId,
            string message,
            IReadOnlyList<string>? delegationChain = null,
            CancellationToken cancellationToken = default,
            IProgress<A2ASendingProgress>? progress = null,
            A2ADispatchMode mode = A2ADispatchMode.Blocking) =>
            throw new NotSupportedException();

        public Task<Result> CancelRemoteTaskAsync(
            string agentUrl,
            string taskId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());


    }

    private sealed class RecordingApprenticeRepository : IApprenticeRepository
    {

        public List<Apprentice> Added { get; } = [];

        public Task<Apprentice> AddAsync(Apprentice apprentice, CancellationToken cancellationToken = default)
        {

            Added.Add(apprentice);

            return Task.FromResult(apprentice);

        }

        public Task<Apprentice?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Added.FirstOrDefault(a => a.Id == id));

        public Task<ListPageResult<Apprentice>> ListAsync(
            Guid? campaignId,
            string? status,
            int? limit = null,
            DateTimeOffset? beforeUpdatedAt = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Apprentice> UpdateAsync(Apprentice apprentice, CancellationToken cancellationToken = default) =>
            Task.FromResult(apprentice);

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Apprentice>> GetResumableAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Apprentice>>([]);

        public Task<IReadOnlyList<Apprentice>> GetInterruptedPlanningAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Apprentice>>([]);

    }

    private sealed class StubApprenticeRuntime(ChannelReader<ApprenticeEvent> chronicle) : IApprenticeRuntime
    {

        public Task<Result<string>> StartAsync(Guid apprenticeId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<string>.Success("started"));

        public Task<Result<string>> PauseAsync(Guid apprenticeId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<string>.Success(string.Empty));

        public Task<Result<string>> ResumeAsync(Guid apprenticeId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<string>.Success(string.Empty));

        public Task<Result<string>> CancelAsync(Guid apprenticeId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<string>.Success(string.Empty));

        public Task<Result<ApprenticeDetailDto>> ReweaveAsync(
            Guid apprenticeId,
            IReadOnlyList<PlanStep> steps,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Result<string>> InterveneAsync(
            Guid apprenticeId,
            string guidance,
            bool resume,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ApprenticeEvent> SubscribeChronicleAsync(
            Guid apprenticeId,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {

            await foreach (ApprenticeEvent @event in chronicle.ReadAllAsync(cancellationToken))
            {

                yield return @event;

            }

        }

    }

    private sealed class StubSessionRepository : ISessionRepository
    {

        public Task<Session> CreateAsync(Guid? campaignId, string? title, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<Session?> GetByIdAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();

        public Task<SessionQueryResult> QueryAsync(SessionQueryRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<SessionAnalytics> GetAnalyticsAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<Result<SessionExportResult>> ExportAsync(Guid id, SessionExportFormat format, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<Result<Entry>> AddEntryAsync(Guid sessionId, Entry entry, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<Result<Session>> ForkAsync(Guid sourceId, ForkSessionRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<List<Entry>> GetEntriesAscendingAsync(Guid sessionId, int takeLast, CancellationToken ct = default) =>
            Task.FromResult(new List<Entry>());

        public Task<List<Entry>> GetEntriesAfterAsync(Guid sessionId, long afterSequence, int limit, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Entry?> GetEntryAsync(Guid sessionId, Guid entryId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<List<Entry>> GetEntriesAsync(
            Guid sessionId,
            int offset = 0,
            int limit = 100,
            DateTimeOffset? beforeCreatedAt = null,
            Guid? beforeId = null,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<int> GetEntryCountAsync(Guid sessionId, CancellationToken ct) => Task.FromResult(0);

        public Task UpdateSessionAsync(Session session, CancellationToken ct) => Task.CompletedTask;

        public Task ArchiveAsync(Guid id, CancellationToken ct) => Task.CompletedTask;

    }

    private sealed class StubPacer : IUnseenServantPacer
    {

        public bool SetDynamicInterval(string jobName, int intervalMinutes) => true;

        public int GetEffectiveInterval(UnseenServantJob job) => job.IntervalMinutes;

        public Task HydrateAsync(
            IReadOnlyList<UnseenServantWatermark> watermarks,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

    }

}
