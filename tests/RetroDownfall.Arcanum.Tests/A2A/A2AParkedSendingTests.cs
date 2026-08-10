using System.Runtime.CompilerServices;
using System.Threading.Channels;

using A2A;

using A2ATaskStatus = A2A.TaskStatus;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.A2A;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.A2A;

/// <summary>
/// Issue #68: an inbound Sending parked at <c>input-required</c> stays answerable across a restart. The
/// escalated Apprentice was always durable; what was missing was the record that a specific A2A task is
/// waiting on it — and, underneath that, any way for the peer's follow-up to reach the handler at all
/// once the SDK's process-local task store died with its process.
/// </summary>
public sealed class A2AParkedSendingTests
{

    private static ArcanumSettings EnabledSettings() => new()
    {
        Features = new FeatureSettings { Conclave = true, A2AServer = true },
        Security = new SecuritySettings { CampaignRoots = [Directory.GetCurrentDirectory()] },
    };

    // ── the parked fact becomes durable ────────────────────────────────────────────────────────────

    [Fact]
    public async Task EscalatedSending_RecordsTheParkedStateOnTheDurableRecord()
    {

        Harness harness = new();

        harness.QueueEscalation("which environment should I deploy to?");

        (TaskState? state, _) = await harness.RunAsync(harness.Request("deploy the service"));

        Assert.Equal(TaskState.InputRequired, state);

        Guid apprenticeId = Assert.Single(harness.Archmage.Created).Id;

        // The in-memory index dies with the process. Without this, a peer answering the question after a
        // restart gets a brand-new Apprentice that knows nothing of the first one's plan or progress.
        Assert.True(harness.Ledger.Parked.TryGetValue("task-1", out ParkedRow parked));

        Assert.Equal(apprenticeId, parked.ApprenticeId);

        Assert.Equal("ctx-1", parked.ContextId);

        // A parked Sending has not settled, so its record stays open.
        Assert.Empty(harness.Ledger.Released);

    }

    [Fact]
    public async Task ContinuationAfterARestart_ResumesTheOriginalApprenticeInsteadOfMintingASecond()
    {

        FakeParkedLedger ledger = new();

        Guid original = Guid.NewGuid();

        ledger.Parked["task-1"] = new ParkedRow(original, "ctx-1", new A2ASendingLedgerEntry(Guid.NewGuid(), "prior-process"));

        // A fresh handler is the restart: nothing carried over in memory, only the durable record.
        Harness restarted = new(ledger);

        restarted.QueueCompletion();

        (TaskState? state, _) = await restarted.RunAsync(restarted.Request("use staging", taskId: "task-1"));

        Assert.Equal(TaskState.Completed, state);

        Assert.Equal([original], restarted.Runtime.IntervenedApprenticeIds);

        Assert.Equal(["use staging"], restarted.Runtime.InterventionGuidance);

        // The failure #64 removed, displaced past a restart boundary: a second Apprentice with none of the
        // first one's plan, session, or progress.
        Assert.Empty(restarted.Archmage.Created);

    }

    [Fact]
    public async Task ContinuationWithNoDurableRecordAtAll_StillMintsAFreshApprentice()
    {

        Harness harness = new();

        harness.QueueCompletion();

        (TaskState? state, _) = await harness.RunAsync(harness.Request("do the thing", taskId: "never-seen"));

        Assert.Equal(TaskState.Completed, state);

        // "Nothing recorded" is a new Sending, not a silent failure.
        Assert.Single(harness.Archmage.Created);

        Assert.Empty(harness.Runtime.IntervenedApprenticeIds);

    }

    [Fact]
    public async Task CancelAfterARestart_CancelsTheParkedApprenticeAndAnswersThePeer()
    {

        FakeParkedLedger ledger = new();

        Guid original = Guid.NewGuid();

        ledger.Parked["task-1"] = new ParkedRow(original, "ctx-1", new A2ASendingLedgerEntry(Guid.NewGuid(), "prior-process"));

        ledger.Recovered["task-1"] = original;

        Harness restarted = new(ledger);

        AgentEventQueue queue = new();

        await restarted.Handler.CancelAsync(restarted.Request("", taskId: "task-1"), queue, CancellationToken.None);

        // A peer that gives up on the question must stop the Apprentice waiting for it, not merely be told
        // "Canceled" while the work keeps running.
        Assert.Equal([original], restarted.Runtime.CancelledApprenticeIds);

        Assert.Equal(TaskState.Canceled, await DrainStateAsync(queue));

    }

    // ── the peer's follow-up can reach the handler at all ──────────────────────────────────────────

    [Fact]
    public async Task TaskStore_RehydratesAParkedTaskRecordedByAPreviousProcess()
    {

        FakeParkedLedger ledger = new();

        ledger.Parked["task-1"] = new ParkedRow(Guid.NewGuid(), "ctx-restored", default);

        ArcanumA2ATaskStore store = new(ScopeFactoryFor(ledger), NullLogger<ArcanumA2ATaskStore>.Instance);

        AgentTask? rehydrated = await store.GetTaskAsync("task-1");

        // Without this the SDK answers the peer's continuation with "task not found" and the handler is
        // never invoked — the durable Apprentice mapping underneath would never get a chance to matter.
        Assert.NotNull(rehydrated);

        Assert.Equal("task-1", rehydrated!.Id);

        Assert.Equal("ctx-restored", rehydrated.ContextId);

        Assert.Equal(TaskState.InputRequired, rehydrated.Status.State);

    }

    [Fact]
    public async Task TaskStore_ReturnsNothingForATaskNobodyRecorded()
    {

        ArcanumA2ATaskStore store = new(ScopeFactoryFor(new FakeParkedLedger()), NullLogger<ArcanumA2ATaskStore>.Instance);

        Assert.Null(await store.GetTaskAsync("never-existed"));

    }

    [Fact]
    public async Task TaskStore_PrefersTheLiveTaskOverTheDurableRecord()
    {

        FakeParkedLedger ledger = new();

        ledger.Parked["task-1"] = new ParkedRow(Guid.NewGuid(), "ctx-restored", default);

        ArcanumA2ATaskStore store = new(ScopeFactoryFor(ledger), NullLogger<ArcanumA2ATaskStore>.Instance);

        await store.SaveTaskAsync(
            "task-1",
            new AgentTask
            {
                Id = "task-1",
                ContextId = "ctx-live",
                Status = new A2ATaskStatus { State = TaskState.Working },
            });

        AgentTask? live = await store.GetTaskAsync("task-1");

        // A live task is the authority while the process that owns it is running; the durable record is
        // only the fallback for one that outlived its process.
        Assert.Equal("ctx-live", live!.ContextId);

        Assert.Equal(TaskState.Working, live.Status.State);

    }

    [Fact]
    public async Task TaskStore_WithoutALedger_BehavesExactlyLikeTheInMemoryStore()
    {

        ArcanumA2ATaskStore store = new(scopeFactory: null, NullLogger<ArcanumA2ATaskStore>.Instance);

        Assert.Null(await store.GetTaskAsync("task-1"));

        await store.SaveTaskAsync(
            "task-1",
            new AgentTask { Id = "task-1", ContextId = "ctx", Status = new A2ATaskStatus { State = TaskState.Working } });

        Assert.NotNull(await store.GetTaskAsync("task-1"));

        await store.DeleteTaskAsync("task-1");

        Assert.Null(await store.GetTaskAsync("task-1"));

    }

    // ── end to end, through the protocol ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ThroughTheProtocol_AContinuationAfterARestartResumesTheOriginalApprentice()
    {

        FakeParkedLedger ledger = new();

        Guid original = Guid.NewGuid();

        ledger.Parked["task-1"] = new ParkedRow(original, "ctx-1", new A2ASendingLedgerEntry(Guid.NewGuid(), "prior-process"));

        Harness restarted = new(ledger);

        restarted.QueueCompletion();

        await using A2AServer server = restarted.BuildServer();

        SendMessageResponse response = await server.SendMessageAsync(new SendMessageRequest
        {
            Message = new Message
            {
                Role = Role.User,
                MessageId = Guid.NewGuid().ToString("N"),
                TaskId = "task-1",
                Parts = [Part.FromText("use staging")],
            },
            Configuration = new SendMessageConfiguration { ReturnImmediately = true },
        });

        Assert.Equal("task-1", response.Task?.Id);

        await WaitForAsync(() => restarted.Runtime.IntervenedApprenticeIds.Count > 0);

        // Before the durable task store, the SDK answered this with TaskNotFound and the handler was
        // never reached — so the durable Apprentice mapping underneath could never matter (issue #68).
        Assert.Equal([original], restarted.Runtime.IntervenedApprenticeIds);

        Assert.Empty(restarted.Archmage.Created);

    }

    [Fact]
    public async Task ThroughTheProtocol_ACancelAfterARestartCancelsTheParkedApprentice()
    {

        FakeParkedLedger ledger = new();

        Guid original = Guid.NewGuid();

        ledger.Parked["task-1"] = new ParkedRow(original, "ctx-1", new A2ASendingLedgerEntry(Guid.NewGuid(), "prior-process"));

        ledger.Recovered["task-1"] = original;

        Harness restarted = new(ledger);

        await using A2AServer server = restarted.BuildServer();

        AgentTask cancelled = await server.CancelTaskAsync(new CancelTaskRequest { Id = "task-1" });

        Assert.Equal(TaskState.Canceled, cancelled.Status.State);

        Assert.Equal([original], restarted.Runtime.CancelledApprenticeIds);

    }

    // ── reconciliation tells parked apart from abandoned ───────────────────────────────────────────

    [Fact]
    public async Task InboundReconciliation_ParkedAndAnswerable_IsNotAbandoned()
    {

        Guid apprenticeId = Guid.NewGuid();

        FakeApprentices apprentices = new()
        {
            Item = new Apprentice { Id = apprenticeId, Status = ApprenticeStatus.Escalated.ToString() },
        };

        A2AInboundSendingRecoveryHandler handler = new(
            apprentices,
            NullLogger<A2AInboundSendingRecoveryHandler>.Instance);

        LongRunningOperationRecoveryResult outcome = await handler.RecoverAsync(
            InboundOperation("task-1", apprenticeId, parked: true),
            CancellationToken.None);

        // Abandoning it here would close the only record that lets the peer's answer find its Apprentice —
        // the exact failure this change exists to remove.
        Assert.Equal(LongRunningOperationState.ReconciliationRequired, outcome.State);

        Assert.Equal(A2ASendingRecoveryOutcomes.InboundParkedAwaitingAnswer, outcome.ErrorCode);

    }

    [Fact]
    public async Task InboundReconciliation_ParkedButTheApprenticeIsGone_IsAbandoned()
    {

        A2AInboundSendingRecoveryHandler handler = new(
            new FakeApprentices(),
            NullLogger<A2AInboundSendingRecoveryHandler>.Instance);

        LongRunningOperationRecoveryResult outcome = await handler.RecoverAsync(
            InboundOperation("task-1", Guid.NewGuid(), parked: true),
            CancellationToken.None);

        Assert.Equal(LongRunningOperationState.Abandoned, outcome.State);

        Assert.Equal(A2ASendingRecoveryOutcomes.InboundApprenticeMissing, outcome.ErrorCode);

    }

    [Fact]
    public async Task InboundReconciliation_ParkedButTheApprenticeAlreadyFinished_IsAbandoned()
    {

        Guid apprenticeId = Guid.NewGuid();

        FakeApprentices apprentices = new()
        {
            Item = new Apprentice { Id = apprenticeId, Status = ApprenticeStatus.Completed.ToString() },
        };

        A2AInboundSendingRecoveryHandler handler = new(
            apprentices,
            NullLogger<A2AInboundSendingRecoveryHandler>.Instance);

        LongRunningOperationRecoveryResult outcome = await handler.RecoverAsync(
            InboundOperation("task-1", apprenticeId, parked: true),
            CancellationToken.None);

        // A park nobody can answer any more is stale, not answerable.
        Assert.Equal(LongRunningOperationState.Abandoned, outcome.State);

        Assert.Equal(A2ASendingRecoveryOutcomes.InboundRelayAbandoned, outcome.ErrorCode);

    }

    [Fact]
    public async Task InboundReconciliation_NotParked_IsStillAbandonedWithTheRelayReason()
    {

        Guid apprenticeId = Guid.NewGuid();

        FakeApprentices apprentices = new()
        {
            Item = new Apprentice { Id = apprenticeId, Status = ApprenticeStatus.Escalated.ToString() },
        };

        A2AInboundSendingRecoveryHandler handler = new(
            apprentices,
            NullLogger<A2AInboundSendingRecoveryHandler>.Instance);

        LongRunningOperationRecoveryResult outcome = await handler.RecoverAsync(
            InboundOperation("task-1", apprenticeId, parked: false),
            CancellationToken.None);

        Assert.Equal(LongRunningOperationState.Abandoned, outcome.State);

        Assert.Equal(A2ASendingRecoveryOutcomes.InboundRelayAbandoned, outcome.ErrorCode);

    }

    // ── harness ────────────────────────────────────────────────────────────────────────────────────

    internal readonly record struct ParkedRow(Guid ApprenticeId, string? ContextId, A2ASendingLedgerEntry Ledger);

    private static IServiceScopeFactory ScopeFactoryFor(IA2ASendingLedger ledger)
    {

        ServiceCollection services = new();

        services.AddSingleton(ledger);

        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

    }

    private static async Task WaitForAsync(Func<bool> condition)
    {

        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);

        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {

            await Task.Delay(25).ConfigureAwait(false);

        }

    }

    private static LongRunningOperation InboundOperation(string taskId, Guid apprenticeId, bool parked) =>
        new(
            Guid.NewGuid(),
            LongRunningOperationKinds.A2AInboundSending,
            LongRunningOperationState.Running,
            LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            DateTimeOffset.UnixEpoch,
            null,
            null,
            null,
            null,
            null,
            0,
            1,
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                new A2ASendingRecord
                {
                    Direction = A2ASendingRecordDirection.Inbound,
                    TaskId = taskId,
                    ApprenticeId = apprenticeId,
                    Parked = parked,
                    ContextId = "ctx-1",
                },
                new System.Text.Json.JsonSerializerOptions
                {
                    TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
                    Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
                }),
            null,
            "test",
            null,
            1);

    private static async Task<TaskState?> DrainStateAsync(AgentEventQueue queue)
    {

        queue.Complete();

        TaskState? latest = null;

        await foreach (StreamResponse response in queue)
        {

            A2ATaskStatus? status = response.PayloadCase switch
            {
                StreamResponseCase.Task => response.Task?.Status,
                StreamResponseCase.StatusUpdate => response.StatusUpdate?.Status,
                _ => null,
            };

            if (status is not null)
            {

                latest = status.State;

            }

        }

        return latest;

    }

    private sealed class Harness
    {

        private readonly Channel<ApprenticeEvent> _chronicle = Channel.CreateUnbounded<ApprenticeEvent>();

        public Harness(FakeParkedLedger? ledger = null)
        {

            Ledger = ledger ?? new FakeParkedLedger();

            Runtime = new RecordingRuntime(_chronicle.Reader);

            ServiceCollection services = new();

            services.AddSingleton<IConclaveArchmage>(Archmage);

            services.AddSingleton<IApprenticeRuntime>(Runtime);

            services.AddSingleton<IApprenticeRepository>(new FakeApprentices());

            services.AddSingleton<ISessionRepository>(new StubSessionRepository());

            services.AddSingleton<IA2ASendingLedger>(Ledger);

            _scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

            Handler = new ArcanumA2AAgentHandler(
                _scopeFactory,
                new TestOptionsMonitor<ArcanumSettings>(EnabledSettings()),
                NullLogger<ArcanumA2AAgentHandler>.Instance);

        }

        private readonly IServiceScopeFactory _scopeFactory;

        /// <summary>The real SDK server over the durable task store — the wiring a peer actually meets.</summary>
        public A2AServer BuildServer() => new(
            Handler,
            new ArcanumA2ATaskStore(_scopeFactory, NullLogger<ArcanumA2ATaskStore>.Instance),
            new ChannelEventNotifier(),
            NullLogger<A2AServer>.Instance,
            new A2AServerOptions { AutoAppendHistory = true });

        public RecordingArchmage Archmage { get; } = new();

        public RecordingRuntime Runtime { get; }

        public FakeParkedLedger Ledger { get; }

        public ArcanumA2AAgentHandler Handler { get; }

        public RequestContext Request(string text, string taskId = "task-1") => new()
        {
            Message = new Message
            {
                Role = Role.User,
                MessageId = Guid.NewGuid().ToString("N"),
                Parts = [Part.FromText(text)],
            },
            TaskId = taskId,
            ContextId = "ctx-1",
            StreamingResponse = false,
        };

        public void QueueEscalation(string reason) =>
            _chronicle.Writer.TryWrite(new ApprenticeEvent
            {
                Type = ApprenticeEventType.ApprenticeEscalated,
                Error = reason,
            });

        public void QueueCompletion() =>
            _chronicle.Writer.TryWrite(new ApprenticeEvent { Type = ApprenticeEventType.ApprenticeCompleted });

        public async Task<(TaskState? State, AgentEventQueue Queue)> RunAsync(RequestContext context)
        {

            AgentEventQueue queue = new();

            await Handler.ExecuteAsync(context, queue, CancellationToken.None);

            return (await DrainStateAsync(queue), queue);

        }

    }

    internal sealed class FakeParkedLedger : IA2ASendingLedger
    {

        public Dictionary<string, ParkedRow> Parked { get; } = [];

        public Dictionary<string, Guid> Recovered { get; } = [];

        public Dictionary<string, Guid> RegisteredInbound { get; } = [];

        public List<A2ASendingLedgerEntry> Released { get; } = [];

        private readonly Dictionary<Guid, string> _entryTaskIds = [];

        public Task<A2ASendingLedgerEntry> RegisterInboundAsync(
            string taskId,
            Guid apprenticeId,
            CancellationToken cancellationToken = default)
        {

            RegisteredInbound[taskId] = apprenticeId;

            A2ASendingLedgerEntry entry = new(Guid.NewGuid(), "test");

            _entryTaskIds[entry.OperationId] = taskId;

            return Task.FromResult(entry);

        }

        public Task<A2ASendingLedgerEntry> RegisterOutboundAsync(
            string remoteTaskId,
            string agentUrl,
            Guid? budgetReservationId = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new A2ASendingLedgerEntry(Guid.NewGuid(), "test"));

        public Task SettleOutboundAsync(
            A2ASendingLedgerEntry entry,
            A2ARemoteCost cost,
            CancellationToken cancellationToken = default)
        {

            Settled.Add((entry, cost));

            return Task.CompletedTask;

        }

        public List<(A2ASendingLedgerEntry Entry, A2ARemoteCost Cost)> Settled { get; } = [];

        public Task RecordOutboundCallbackAsync(
            A2ASendingLedgerEntry entry,
            string callbackConfigId,
            string callbackTokenHash,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<A2AOutboundCallback?> FindOutboundCallbackAsync(
            string callbackConfigId,
            CancellationToken cancellationToken = default) => Task.FromResult<A2AOutboundCallback?>(null);

        public Task ReleaseAsync(A2ASendingLedgerEntry entry, CancellationToken cancellationToken = default)
        {

            Released.Add(entry);

            return Task.CompletedTask;

        }

        public Task MarkParkedAsync(
            A2ASendingLedgerEntry entry,
            string? contextId,
            CancellationToken cancellationToken = default)
        {

            if (_entryTaskIds.TryGetValue(entry.OperationId, out string? taskId)
                && RegisteredInbound.TryGetValue(taskId, out Guid apprenticeId))
            {

                Parked[taskId] = new ParkedRow(apprenticeId, contextId, entry);

            }

            return Task.CompletedTask;

        }

        public Task<A2AParkedSending?> FindParkedInboundAsync(
            string taskId,
            bool takeLease = true,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Parked.TryGetValue(taskId, out ParkedRow row)
                ? new A2AParkedSending(row.ApprenticeId, row.ContextId, row.Ledger)
                : (A2AParkedSending?)null);

        public Task<Guid?> FindInboundApprenticeAsync(string taskId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Recovered.TryGetValue(taskId, out Guid id) ? id : (Guid?)null);

    }

    private sealed class RecordingArchmage : IConclaveArchmage
    {

        public List<Apprentice> Created { get; } = [];

        public Task<Result<Apprentice>> CastAsync(ConclaveCastRequest request, CancellationToken cancellationToken = default)
        {

            Apprentice apprentice = new()
            {
                Id = Guid.NewGuid(),
                Goal = request.Goal,
                Status = ApprenticeStatus.Idle.ToString(),
                WorkspacePath = request.WorkspacePath,
            };

            Created.Add(apprentice);

            return Task.FromResult(Result<Apprentice>.Success(apprentice));

        }

    }

    private sealed class RecordingRuntime(ChannelReader<ApprenticeEvent> chronicle) : IApprenticeRuntime
    {

        public List<Guid> CancelledApprenticeIds { get; } = [];

        public List<Guid> IntervenedApprenticeIds { get; } = [];

        public List<string> InterventionGuidance { get; } = [];

        public Task<Result<string>> StartAsync(Guid apprenticeId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<string>.Success("started"));

        public Task<Result<string>> PauseAsync(Guid apprenticeId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<string>.Success(string.Empty));

        public Task<Result<string>> ResumeAsync(Guid apprenticeId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<string>.Success(string.Empty));

        public Task<Result<string>> CancelAsync(Guid apprenticeId, CancellationToken cancellationToken = default)
        {

            CancelledApprenticeIds.Add(apprenticeId);

            return Task.FromResult(Result<string>.Success(string.Empty));

        }

        public Task<Result<ApprenticeDetailDto>> ReweaveAsync(
            Guid apprenticeId,
            IReadOnlyList<PlanStep> steps,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Result<string>> InterveneAsync(
            Guid apprenticeId,
            string guidance,
            bool resume,
            CancellationToken cancellationToken = default)
        {

            IntervenedApprenticeIds.Add(apprenticeId);

            InterventionGuidance.Add(guidance);

            return Task.FromResult(Result<string>.Success("resumed"));

        }

        public async IAsyncEnumerable<ApprenticeEvent> SubscribeChronicleAsync(
            Guid apprenticeId,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {

            while (await chronicle.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {

                while (chronicle.TryRead(out ApprenticeEvent? @event))
                {

                    yield return @event;

                }

            }

        }

    }

    private sealed class FakeApprentices : IApprenticeRepository
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
            Task.FromResult(apprentice);

        public Task<Apprentice> UpdateAsync(Apprentice apprentice, CancellationToken cancellationToken = default) =>
            Task.FromResult(apprentice);

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Apprentice>> GetResumableAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Apprentice>>([]);

        public Task<IReadOnlyList<Apprentice>> GetInterruptedPlanningAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Apprentice>>([]);

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

}
