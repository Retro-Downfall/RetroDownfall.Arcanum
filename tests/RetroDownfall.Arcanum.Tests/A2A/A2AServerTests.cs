using System.Runtime.CompilerServices;
using System.Threading.Channels;

using A2A;

using TaskStatus = A2A.TaskStatus;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Api.A2A;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Conclave;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.A2A;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.A2A;

public sealed class A2AServerTests
{

    private static ArcanumSettings EnabledSettings() => new()
    {
        Features = new FeatureSettings
        {
            Conclave = true,
            A2AServer = true,
        },

        // Security.CampaignRoots is secure-by-default: empty denies every workspace, including the
        // Directory.GetCurrentDirectory() fallback ResolveWorkspace ultimately falls back to. Operators
        // enabling A2A must configure CampaignRoots (or A2A.DefaultWorkspace under an allowed root) for
        // any inbound task to have a usable workspace — see DESIGN.md §5.7.1.
        Security = new SecuritySettings { CampaignRoots = [Directory.GetCurrentDirectory()] },
    };

    private static RequestContext RequestWithText(string text, string taskId = "task-1", string contextId = "ctx-1") => new()
    {
        Message = new Message
        {
            Role = Role.User,
            MessageId = Guid.NewGuid().ToString("N"),
            Parts = [Part.FromText(text)],
        },
        TaskId = taskId,
        ContextId = contextId,
        StreamingResponse = false,
    };

    private static TaskState? LatestState(IReadOnlyList<StreamResponse> responses)
    {

        TaskState? latest = null;

        foreach (StreamResponse r in responses)
        {

            TaskStatus? status = r.PayloadCase switch
            {
                StreamResponseCase.Task => r.Task?.Status,
                StreamResponseCase.StatusUpdate => r.StatusUpdate?.Status,
                _ => null,
            };

            if (status is not null)
            {

                latest = status.State;

            }

        }

        return latest;

    }

    private static IEnumerable<string> ArtifactTexts(IReadOnlyList<StreamResponse> responses) =>
        responses
            .Where(static r => r.PayloadCase == StreamResponseCase.ArtifactUpdate && r.ArtifactUpdate?.Artifact?.Parts is not null)
            .SelectMany(static r => r.ArtifactUpdate!.Artifact!.Parts)
            .Where(static p => p.ContentCase == PartContentCase.Text)
            .Select(static p => p.Text!);

    private static async Task<List<StreamResponse>> DrainAsync(AgentEventQueue queue)
    {

        queue.Complete();

        List<StreamResponse> items = [];

        await foreach (StreamResponse item in queue)
        {

            items.Add(item);

        }

        return items;

    }

    [Fact]
    public async Task ExecuteAsync_ServerDisabled_RejectsWithoutCreatingApprentice()
    {

        FakeConclaveArchmage archmage = new();

        FakeApprenticeRuntime runtime = new(Channel.CreateUnbounded<ApprenticeEvent>().Reader);

        ArcanumA2AAgentHandler handler = CreateHandler(new ArcanumSettings(), archmage, runtime, new FakeApprenticeRepository(), new FakeSessionRepository());

        AgentEventQueue queue = new();

        await handler.ExecuteAsync(RequestWithText("do the thing"), queue, CancellationToken.None);

        List<StreamResponse> responses = await DrainAsync(queue);

        Assert.Null(archmage.LastRequest);

        Assert.Empty(runtime.StartedApprenticeIds);

        Assert.Equal(TaskState.Rejected, LatestState(responses));

    }

    [Fact]
    public async Task ExecuteAsync_EmptyGoal_RejectsWithoutCreatingApprentice()
    {

        FakeConclaveArchmage archmage = new();

        FakeApprenticeRuntime runtime = new(Channel.CreateUnbounded<ApprenticeEvent>().Reader);

        ArcanumA2AAgentHandler handler = CreateHandler(EnabledSettings(), archmage, runtime, new FakeApprenticeRepository(), new FakeSessionRepository());

        AgentEventQueue queue = new();

        await handler.ExecuteAsync(RequestWithText("   "), queue, CancellationToken.None);

        List<StreamResponse> responses = await DrainAsync(queue);

        Assert.Null(archmage.LastRequest);

        Assert.Equal(TaskState.Rejected, LatestState(responses));

    }

    [Fact]
    public async Task ExecuteAsync_HappyPath_CreatesStartsAndCompletesWithArtifact()
    {

        Guid apprenticeId = Guid.NewGuid();

        Guid sessionId = Guid.NewGuid();

        Apprentice apprentice = new()
        {
            Id = apprenticeId,
            SessionId = sessionId,
            Name = "Sending abc123",
            Goal = "do the thing",
            Status = ApprenticeStatus.Idle.ToString(),
            WorkspacePath = Directory.GetCurrentDirectory(),
        };

        FakeConclaveArchmage archmage = new() { OnCast = _ => Result<Apprentice>.Success(apprentice) };

        Channel<ApprenticeEvent> chronicle = Channel.CreateUnbounded<ApprenticeEvent>();

        FakeApprenticeRuntime runtime = new(chronicle.Reader);

        FakeApprenticeRepository repo = new() { Item = apprentice };

        FakeSessionRepository sessionRepo = new()
        {
            Entries =
            [
                new Entry { Id = Guid.NewGuid(), SessionId = sessionId, Role = MessageRole.User, Content = "do the thing", Sequence = 1 },
                new Entry { Id = Guid.NewGuid(), SessionId = sessionId, Role = MessageRole.Assistant, Content = "All done!", Sequence = 2 },
            ],
        };

        ArcanumA2AAgentHandler handler = CreateHandler(EnabledSettings(), archmage, runtime, repo, sessionRepo);

        AgentEventQueue queue = new();

        chronicle.Writer.TryWrite(new ApprenticeEvent { Type = ApprenticeEventType.ApprenticeCompleted, ApprenticeId = apprenticeId });

        chronicle.Writer.Complete();

        await handler.ExecuteAsync(RequestWithText("do the thing"), queue, CancellationToken.None);

        List<StreamResponse> responses = await DrainAsync(queue);

        Assert.NotNull(archmage.LastRequest);

        Assert.Equal("do the thing", archmage.LastRequest!.Goal);

        Assert.Equal([apprenticeId], runtime.StartedApprenticeIds);

        Assert.Equal(TaskState.Completed, LatestState(responses));

        Assert.Contains("All done!", ArtifactTexts(responses));

    }

    [Fact]
    public async Task ExecuteAsync_ApprenticeFails_EmitsFailedStatus()
    {

        Guid apprenticeId = Guid.NewGuid();

        Apprentice apprentice = new() { Id = apprenticeId, Status = ApprenticeStatus.Idle.ToString() };

        FakeConclaveArchmage archmage = new() { OnCast = _ => Result<Apprentice>.Success(apprentice) };

        Channel<ApprenticeEvent> chronicle = Channel.CreateUnbounded<ApprenticeEvent>();

        FakeApprenticeRuntime runtime = new(chronicle.Reader);

        ArcanumA2AAgentHandler handler = CreateHandler(EnabledSettings(), archmage, runtime, new FakeApprenticeRepository { Item = apprentice }, new FakeSessionRepository());

        AgentEventQueue queue = new();

        chronicle.Writer.TryWrite(new ApprenticeEvent { Type = ApprenticeEventType.ApprenticeFailed, ApprenticeId = apprenticeId, Error = "boom" });

        chronicle.Writer.Complete();

        await handler.ExecuteAsync(RequestWithText("do the thing"), queue, CancellationToken.None);

        List<StreamResponse> responses = await DrainAsync(queue);

        Assert.Equal(TaskState.Failed, LatestState(responses));

    }

    [Fact]
    public async Task ExecuteAsync_ApprenticeEscalates_EmitsInputRequiredStatus()
    {

        Guid apprenticeId = Guid.NewGuid();

        Apprentice apprentice = new() { Id = apprenticeId, Status = ApprenticeStatus.Idle.ToString() };

        FakeConclaveArchmage archmage = new() { OnCast = _ => Result<Apprentice>.Success(apprentice) };

        Channel<ApprenticeEvent> chronicle = Channel.CreateUnbounded<ApprenticeEvent>();

        FakeApprenticeRuntime runtime = new(chronicle.Reader);

        ArcanumA2AAgentHandler handler = CreateHandler(EnabledSettings(), archmage, runtime, new FakeApprenticeRepository { Item = apprentice }, new FakeSessionRepository());

        AgentEventQueue queue = new();

        chronicle.Writer.TryWrite(new ApprenticeEvent { Type = ApprenticeEventType.ApprenticeEscalated, ApprenticeId = apprenticeId, Error = "need guidance" });

        chronicle.Writer.Complete();

        await handler.ExecuteAsync(RequestWithText("do the thing"), queue, CancellationToken.None);

        List<StreamResponse> responses = await DrainAsync(queue);

        Assert.Equal(TaskState.InputRequired, LatestState(responses));

    }

    [Fact]
    public async Task CancelAsync_KnownTask_CancelsUnderlyingApprentice()
    {

        Guid apprenticeId = Guid.NewGuid();

        Apprentice apprentice = new() { Id = apprenticeId, Status = ApprenticeStatus.Idle.ToString() };

        FakeConclaveArchmage archmage = new() { OnCast = _ => Result<Apprentice>.Success(apprentice) };

        Channel<ApprenticeEvent> chronicle = Channel.CreateUnbounded<ApprenticeEvent>();

        FakeApprenticeRuntime runtime = new(chronicle.Reader);

        ArcanumA2AAgentHandler handler = CreateHandler(EnabledSettings(), archmage, runtime, new FakeApprenticeRepository { Item = apprentice }, new FakeSessionRepository());

        AgentEventQueue queue = new();

        RequestContext context = RequestWithText("do the thing");

        Task executeTask = handler.ExecuteAsync(context, queue, CancellationToken.None);

        // Give ExecuteAsync a chance to reach the (still-open) chronicle subscription before cancelling.
        for (int i = 0; i < 100 && runtime.StartedApprenticeIds.Count == 0; i++)
        {

            await Task.Delay(10);

        }

        await handler.CancelAsync(context, queue, CancellationToken.None);

        Assert.Equal([apprenticeId], runtime.CancelledApprenticeIds);

        chronicle.Writer.TryWrite(new ApprenticeEvent { Type = ApprenticeEventType.ApprenticeCancelled, ApprenticeId = apprenticeId });

        chronicle.Writer.Complete();

        await executeTask;

    }

    [Fact]
    public async Task CancelAsync_UnknownTask_CancelsNoApprenticeButStillAnswersThePeer()
    {

        FakeConclaveArchmage archmage = new();

        FakeApprenticeRuntime runtime = new(Channel.CreateUnbounded<ApprenticeEvent>().Reader);

        ArcanumA2AAgentHandler handler = CreateHandler(EnabledSettings(), archmage, runtime, new FakeApprenticeRepository(), new FakeSessionRepository());

        AgentEventQueue queue = new();

        await handler.CancelAsync(RequestWithText("irrelevant", taskId: "never-seen"), queue, CancellationToken.None);

        Assert.Empty(runtime.CancelledApprenticeIds);

        // Returning silently overrides the SDK's own Canceled transition, leaving the peer with a task that
        // never reaches a terminal state. Not knowing the task still has to produce an answer.
        List<StreamResponse> responses = await DrainAsync(queue);

        Assert.Equal(TaskState.Canceled, LatestState(responses));

    }

    [Fact]
    public async Task ExecuteAsync_TerminalEventRaisedWhileStarting_IsStillObserved()
    {

        Guid apprenticeId = Guid.NewGuid();

        Apprentice apprentice = new() { Id = apprenticeId, Status = ApprenticeStatus.Idle.ToString() };

        FakeConclaveArchmage archmage = new() { OnCast = _ => Result<Apprentice>.Success(apprentice) };

        // Models the production ChronicleHub: live-only, no replay, per-subscriber channel created at
        // subscribe time. The terminal event is raised *during* StartAsync, so a handler that subscribes
        // only after StartAsync returns loses it and leaves the A2A task in Working forever.
        LiveOnlyApprenticeRuntime runtime = new();

        runtime.OnStart = () =>
        {

            runtime.Publish(new ApprenticeEvent
            {
                Type = ApprenticeEventType.ApprenticeFailed,
                ApprenticeId = apprenticeId,
                Error = "failed immediately",
            });

            runtime.CompleteAll();

        };

        ArcanumA2AAgentHandler handler = CreateHandler(EnabledSettings(), archmage, runtime, new FakeApprenticeRepository { Item = apprentice }, new FakeSessionRepository());

        AgentEventQueue queue = new();

        await handler.ExecuteAsync(RequestWithText("do the thing"), queue, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30));

        List<StreamResponse> responses = await DrainAsync(queue);

        Assert.Equal(TaskState.Failed, LatestState(responses));

    }

    [Fact]
    public async Task ExecuteAsync_ChronicleEndsWithoutATerminalEvent_StillAnswersThePeer()
    {

        Guid apprenticeId = Guid.NewGuid();

        Apprentice apprentice = new() { Id = apprenticeId, Status = ApprenticeStatus.Idle.ToString() };

        FakeConclaveArchmage archmage = new() { OnCast = _ => Result<Apprentice>.Success(apprentice) };

        LiveOnlyApprenticeRuntime runtime = new();

        // Peer disconnect / host teardown: the Chronicle simply ends. Leaving the task in Working is not an
        // answer, so the handler must produce a terminal transition of its own.
        runtime.OnStart = runtime.CompleteAll;

        ArcanumA2AAgentHandler handler = CreateHandler(EnabledSettings(), archmage, runtime, new FakeApprenticeRepository { Item = apprentice }, new FakeSessionRepository());

        AgentEventQueue queue = new();

        await handler.ExecuteAsync(RequestWithText("do the thing"), queue, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30));

        List<StreamResponse> responses = await DrainAsync(queue);

        Assert.Equal(TaskState.Failed, LatestState(responses));

    }

    [Fact]
    public async Task ExecuteAsync_ChainAlreadyContainsThisNode_RejectsWithoutCreatingApprentice()
    {

        FakeConclaveArchmage archmage = new();

        FakeApprenticeRuntime runtime = new(Channel.CreateUnbounded<ApprenticeEvent>().Reader);

        ArcanumA2AAgentHandler handler = CreateHandler(EnabledSettings(), archmage, runtime, new FakeApprenticeRepository(), new FakeSessionRepository());

        AgentEventQueue queue = new();

        RequestContext context = RequestWithText("do the thing");

        ConclaveDelegationChain.Write(context.Message!, ["upstream-node", ConclaveDelegationChain.NodeId]);

        await handler.ExecuteAsync(context, queue, CancellationToken.None);

        List<StreamResponse> responses = await DrainAsync(queue);

        // The Sending looped back to this instance. Spawning an Apprentice here is exactly what makes the
        // loop infinite, so the cycle must break before any work is minted (issue #12).
        Assert.Null(archmage.LastRequest);

        Assert.Empty(runtime.StartedApprenticeIds);

        Assert.Equal(TaskState.Rejected, LatestState(responses));

    }

    [Fact]
    public async Task ExecuteAsync_ChainFromAnotherNode_IsAcceptedAndCarriedOntoTheApprentice()
    {

        Guid apprenticeId = Guid.NewGuid();

        Apprentice apprentice = new() { Id = apprenticeId, Status = ApprenticeStatus.Idle.ToString() };

        FakeConclaveArchmage archmage = new() { OnCast = _ => Result<Apprentice>.Success(apprentice) };

        Channel<ApprenticeEvent> chronicle = Channel.CreateUnbounded<ApprenticeEvent>();

        FakeApprenticeRuntime runtime = new(chronicle.Reader);

        ArcanumA2AAgentHandler handler = CreateHandler(EnabledSettings(), archmage, runtime, new FakeApprenticeRepository { Item = apprentice }, new FakeSessionRepository());

        AgentEventQueue queue = new();

        RequestContext context = RequestWithText("do the thing");

        ConclaveDelegationChain.Write(context.Message!, ["upstream-node"]);

        chronicle.Writer.TryWrite(new ApprenticeEvent { Type = ApprenticeEventType.ApprenticeCompleted, ApprenticeId = apprenticeId });

        chronicle.Writer.Complete();

        await handler.ExecuteAsync(context, queue, CancellationToken.None);

        Assert.NotNull(archmage.LastRequest);

        // The upstream hops plus this node travel with the spawned Apprentice so its own delegation stays
        // loop-aware instead of restarting the chain from empty.
        Assert.Equal(["upstream-node", ConclaveDelegationChain.NodeId], archmage.LastRequest!.DelegationChain);

    }

    [Fact]
    public void BuildAgentCard_UsesConfiguredNameAndDescription_AndDerivesInterfaceUrlFromRequest()
    {

        ArcanumSettings settings = new()
        {
            Integrations = new IntegrationSettings
            {
                A2A = new A2AIntegrationSettings
                {
                    AgentCardName = "Test Arcanum",
                    AgentCardDescription = "A test description.",
                    ServerPath = "/api/conclave/a2a",
                },
            },
        };

        DefaultHttpContext httpContext = new();

        httpContext.Request.Scheme = "https";

        httpContext.Request.Host = new HostString("arcanum.example.com", 5001);

        AgentCard card = A2AServerEndpoints.BuildAgentCard(settings, httpContext, "/api/conclave/a2a");

        Assert.Equal("Test Arcanum", card.Name);

        Assert.Equal("A test description.", card.Description);

        Assert.True(card.Capabilities?.Streaming);

        Assert.False(card.Capabilities?.PushNotifications);

        AgentInterface supported = Assert.Single(card.SupportedInterfaces!);

        Assert.Equal("https://arcanum.example.com:5001/api/conclave/a2a", supported.Url);

        Assert.Equal("JSONRPC", supported.ProtocolBinding);

        Assert.Contains("arcanumApiKey", card.SecuritySchemes!.Keys);

    }

    [Fact]
    public void BuildAgentCard_FallsBackToDefaultNameAndDescription_WhenNotConfigured()
    {

        DefaultHttpContext httpContext = new();

        httpContext.Request.Scheme = "http";

        httpContext.Request.Host = new HostString("localhost", 5001);

        AgentCard card = A2AServerEndpoints.BuildAgentCard(new ArcanumSettings(), httpContext, "/api/conclave/a2a");

        Assert.Equal("Arcanum", card.Name);

        Assert.False(string.IsNullOrWhiteSpace(card.Description));

    }

    private static ArcanumA2AAgentHandler CreateHandler(
        ArcanumSettings settings,
        FakeConclaveArchmage archmage,
        IApprenticeRuntime runtime,
        FakeApprenticeRepository repo,
        FakeSessionRepository sessionRepo)
    {

        ServiceCollection services = new();

        services.AddSingleton<IConclaveArchmage>(archmage);

        services.AddSingleton<IApprenticeRuntime>(runtime);

        services.AddSingleton<IApprenticeRepository>(repo);

        services.AddSingleton<ISessionRepository>(sessionRepo);

        IServiceScopeFactory scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        return new ArcanumA2AAgentHandler(
            scopeFactory,
            new TestOptionsMonitor<ArcanumSettings>(settings),
            NullLogger<ArcanumA2AAgentHandler>.Instance);

    }

    private sealed class FakeConclaveArchmage : IConclaveArchmage
    {

        public Func<ConclaveCastRequest, Result<Apprentice>>? OnCast { get; set; }

        public ConclaveCastRequest? LastRequest { get; private set; }

        public Task<Result<Apprentice>> CastAsync(ConclaveCastRequest request, CancellationToken cancellationToken = default)
        {

            LastRequest = request;

            Result<Apprentice> result = OnCast?.Invoke(request)
                ?? Result<Apprentice>.Failure(new Error("Test.NotConfigured", "FakeConclaveArchmage.OnCast was not configured."));

            return Task.FromResult(result);

        }

    }

    private sealed class FakeApprenticeRuntime(ChannelReader<ApprenticeEvent> chronicle) : IApprenticeRuntime
    {

        public List<Guid> StartedApprenticeIds { get; } = [];

        public List<Guid> CancelledApprenticeIds { get; } = [];

        public Result<string> StartResult { get; set; } = Result<string>.Success("started");

        /// <summary>Runs inside <see cref="StartAsync"/>, so a test can race the Chronicle subscription.</summary>
        public Action? OnStart { get; set; }

        public Task<Result<string>> StartAsync(Guid apprenticeId, CancellationToken cancellationToken = default)
        {

            StartedApprenticeIds.Add(apprenticeId);

            OnStart?.Invoke();

            return Task.FromResult(StartResult);

        }

        public Task<Result<string>> PauseAsync(Guid apprenticeId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<string>.Success(string.Empty));

        public Task<Result<string>> ResumeAsync(Guid apprenticeId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<string>.Success(string.Empty));

        public Task<Result<string>> CancelAsync(Guid apprenticeId, CancellationToken cancellationToken = default)
        {

            CancelledApprenticeIds.Add(apprenticeId);

            return Task.FromResult(Result<string>.Success(string.Empty));

        }

        public Task<Result<ApprenticeDetailDto>> ReweaveAsync(Guid apprenticeId, IReadOnlyList<PlanStep> steps, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<string>> InterveneAsync(Guid apprenticeId, string guidance, bool resume, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ApprenticeEvent> SubscribeChronicleAsync(
            Guid apprenticeId,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {

            await foreach (ApprenticeEvent evt in chronicle.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {

                yield return evt;

            }

        }

    }

    /// <summary>
    /// Live-only Chronicle, mirroring <c>ChronicleHub</c>: a subscriber receives only events published
    /// after its own subscription registered, and there is no replay. Anything buffered before the
    /// subscription is lost — which is exactly the production behavior a shared pre-filled channel hides.
    /// </summary>
    private sealed class LiveOnlyApprenticeRuntime : IApprenticeRuntime
    {

        private readonly Lock _gate = new();

        private readonly List<Channel<ApprenticeEvent>> _subscribers = [];

        public List<Guid> StartedApprenticeIds { get; } = [];

        public List<Guid> CancelledApprenticeIds { get; } = [];

        public Action? OnStart { get; set; }

        public void Publish(ApprenticeEvent @event)
        {

            lock (_gate)
            {

                foreach (Channel<ApprenticeEvent> subscriber in _subscribers)
                {

                    subscriber.Writer.TryWrite(@event);

                }

            }

        }

        public void CompleteAll()
        {

            lock (_gate)
            {

                foreach (Channel<ApprenticeEvent> subscriber in _subscribers)
                {

                    subscriber.Writer.TryComplete();

                }

            }

        }

        public Task<Result<string>> StartAsync(Guid apprenticeId, CancellationToken cancellationToken = default)
        {

            StartedApprenticeIds.Add(apprenticeId);

            OnStart?.Invoke();

            return Task.FromResult(Result<string>.Success("started"));

        }

        public Task<Result<string>> PauseAsync(Guid apprenticeId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<string>.Success(string.Empty));

        public Task<Result<string>> ResumeAsync(Guid apprenticeId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<string>.Success(string.Empty));

        public Task<Result<string>> CancelAsync(Guid apprenticeId, CancellationToken cancellationToken = default)
        {

            CancelledApprenticeIds.Add(apprenticeId);

            return Task.FromResult(Result<string>.Success(string.Empty));

        }

        public Task<Result<ApprenticeDetailDto>> ReweaveAsync(Guid apprenticeId, IReadOnlyList<PlanStep> steps, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<string>> InterveneAsync(Guid apprenticeId, string guidance, bool resume, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ApprenticeEvent> SubscribeChronicleAsync(
            Guid apprenticeId,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {

            Channel<ApprenticeEvent> channel = Channel.CreateUnbounded<ApprenticeEvent>();

            lock (_gate)
            {

                _subscribers.Add(channel);

            }

            await foreach (ApprenticeEvent evt in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {

                yield return evt;

            }

        }

    }

    private sealed class FakeApprenticeRepository : IApprenticeRepository
    {

        public Apprentice? Item { get; set; }

        public Task<Apprentice?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Item is not null && Item.Id == id ? Item : null);

        public Task<ListPageResult<Apprentice>> ListAsync(
            Guid? campaignId,
            string? status,
            int? limit = null,
            DateTimeOffset? beforeUpdatedAt = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Apprentice> AddAsync(Apprentice apprentice, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Apprentice> UpdateAsync(Apprentice apprentice, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Apprentice>> GetResumableAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Apprentice>> GetInterruptedPlanningAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

    }

    private sealed class FakeSessionRepository : ISessionRepository
    {

        public List<Entry> Entries { get; set; } = [];

        public Task<Session> CreateAsync(Guid? campaignId, string? title, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<Session?> GetByIdAsync(Guid id, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<SessionQueryResult> QueryAsync(SessionQueryRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<SessionAnalytics> GetAnalyticsAsync(CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<Result<SessionExportResult>> ExportAsync(Guid id, SessionExportFormat format, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<Result<Entry>> AddEntryAsync(Guid sessionId, Entry entry, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<Result<Session>> ForkAsync(Guid sourceId, ForkSessionRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<List<Entry>> GetEntriesAscendingAsync(Guid sessionId, int takeLast, CancellationToken ct = default) =>
            Task.FromResult(Entries);

        public Task<List<Entry>> GetEntriesAfterAsync(
            Guid sessionId,
            long afterSequence,
            int limit,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<Entry?> GetEntryAsync(Guid sessionId, Guid entryId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<List<Entry>> GetEntriesAsync(
            Guid sessionId,
            int offset = 0,
            int limit = 100,
            DateTimeOffset? beforeCreatedAt = null,
            Guid? beforeId = null,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<int> GetEntryCountAsync(Guid sessionId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task UpdateSessionAsync(Session session, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task ArchiveAsync(Guid id, CancellationToken ct) =>
            throw new NotSupportedException();

    }

}
