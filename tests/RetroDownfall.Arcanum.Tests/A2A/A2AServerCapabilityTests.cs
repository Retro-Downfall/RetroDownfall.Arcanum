using System.Runtime.CompilerServices;
using System.Threading.Channels;

using A2A;

using TaskStatus = A2A.TaskStatus;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Conclave;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.A2A;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.A2A;

/// <summary>
/// Inbound A2A behavior added by issues #62 (durable task correspondence), #63 (declared capabilities),
/// and #64 (continuing an escalated Sending).
/// </summary>
public sealed class A2AServerCapabilityTests
{

    private static ArcanumSettings EnabledSettings() => new()
    {
        Features = new FeatureSettings { Conclave = true, A2AServer = true },
        Security = new SecuritySettings { CampaignRoots = [Directory.GetCurrentDirectory()] },
    };

    // ── #63 capability negotiation ─────────────────────────────────────────────────────────────────

    [Fact]
    public void DefaultCard_AdvertisesExactlyTheHistoricalSkillAndModes()
    {

        ConclaveA2ASettings a2a = EnabledSettings().ResolveA2A();

        AgentSkill[] skills = A2AAgentCardPolicy.ResolveSkills(a2a);

        // An operator who declares nothing must serve the card Arcanum served before #63, or every
        // existing peer's expectations break for no reason.
        AgentSkill only = Assert.Single(skills);

        Assert.Equal("apprentice-goal-execution", only.Id);

        Assert.Equal("Autonomous goal execution", only.Name);

        Assert.Equal(["text/plain"], only.InputModes);

        Assert.Equal(["text/plain"], only.OutputModes);

        Assert.Equal(["text/plain"], A2AAgentCardPolicy.ResolveInputModes(a2a));

        Assert.Equal(["text/plain"], A2AAgentCardPolicy.ResolveOutputModes(a2a));

    }

    [Fact]
    public void DeclaredSkills_ReplaceTheDefaultAndInheritCardModesWhenUnset()
    {

        ArcanumSettings settings = EnabledSettings();

        settings.Integrations!.A2A!.OutputModes = ["text/plain", "text/markdown"];

        settings.Integrations.A2A.Skills =
        [
            new A2ASkillSettings { Id = "code-review", Name = "Code review", Description = "Reviews a diff." },
            new A2ASkillSettings { Id = "render", OutputModes = ["image/png"] },
        ];

        AgentSkill[] skills = A2AAgentCardPolicy.ResolveSkills(settings.ResolveA2A());

        Assert.Equal(2, skills.Length);

        Assert.Equal("code-review", skills[0].Id);

        Assert.Equal(["text/plain", "text/markdown"], skills[0].OutputModes);

        // A skill with no name falls back to its id rather than advertising a blank.
        Assert.Equal("render", skills[1].Name);

        Assert.Equal(["image/png"], skills[1].OutputModes);

    }

    [Fact]
    public void DeclaredSkillsWithoutAnId_AreIgnoredRatherThanAdvertisedBlank()
    {

        ArcanumSettings settings = EnabledSettings();

        settings.Integrations!.A2A!.Skills = [new A2ASkillSettings { Name = "nameless" }];

        AgentSkill only = Assert.Single(A2AAgentCardPolicy.ResolveSkills(settings.ResolveA2A()));

        Assert.Equal("apprentice-goal-execution", only.Id);

    }

    public static TheoryData<string[]?> AcceptableRequestedModes => new()
    {
        { null },
        { Array.Empty<string>() },
        { new[] { "text/plain" } },

        // A peer that lists several acceptable modes only needs one of them to match.
        { new[] { "audio/wav", "text/plain" } },
    };

    [Theory]
    [MemberData(nameof(AcceptableRequestedModes))]
    public void RequestedModesArcanumSupports_AreAccepted(string[]? requested)
    {

        Result<string> outcome = A2AAgentCardPolicy.ValidateRequestedModes(EnabledSettings(), requested);

        Assert.True(outcome.IsSuccess);

    }

    [Fact]
    public void RequestedModeArcanumDoesNotAdvertise_IsRejectedByNameRatherThanDowngraded()
    {

        Result<string> outcome = A2AAgentCardPolicy.ValidateRequestedModes(EnabledSettings(), ["audio/wav"]);

        Assert.True(outcome.IsFailure);

        // A peer that asked for audio and silently received prose has no way to notice the downgrade.
        Assert.Contains("audio/wav", outcome.Error.Message, StringComparison.Ordinal);

        Assert.Contains("text/plain", outcome.Error.Message, StringComparison.Ordinal);

    }

    [Fact]
    public async Task InboundSendingRequestingAnUnsupportedMode_IsRejectedWithoutSpawningAnApprentice()
    {

        Harness harness = new();

        RequestContext context = harness.Request(
            "do the thing",
            configuration: new SendMessageConfiguration { AcceptedOutputModes = ["audio/wav"] });

        (TaskState? state, _) = await harness.RunAsync(context);

        Assert.Equal(TaskState.Rejected, state);

        Assert.Empty(harness.Archmage.Requests);

    }

    // ── #64 inbound continuation ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EscalatedInboundSending_ParksTheTaskAtInputRequired()
    {

        Harness harness = new();

        harness.QueueEscalation("which environment?");

        (TaskState? state, _) = await harness.RunAsync(harness.Request("deploy the thing"));

        Assert.Equal(TaskState.InputRequired, state);

    }

    [Fact]
    public async Task ContinuingAnEscalatedInboundSending_ResumesTheOriginalApprentice()
    {

        Harness harness = new();

        harness.QueueEscalation("which environment?");

        RequestContext first = harness.Request("deploy the thing", taskId: "task-1");

        await harness.RunAsync(first);

        Guid original = Assert.Single(harness.Archmage.Created).Id;

        harness.QueueCompletion();

        // The handler recognises a continuation by task id, which is what a peer actually resends; the
        // SDK's own IsContinuation is derived and cannot be set from a test.
        RequestContext continuation = harness.Request("staging", taskId: "task-1");

        (TaskState? state, _) = await harness.RunAsync(continuation);

        Assert.Equal(TaskState.Completed, state);

        // Minting a second Apprentice throws away the first one's plan, session, and progress — which is
        // not a continuation at all (issue #64).
        Assert.Single(harness.Archmage.Created);

        Assert.Equal([original], harness.Runtime.IntervenedApprenticeIds);

        Assert.Equal(["staging"], harness.Runtime.InterventionGuidance);

    }

    [Fact]
    public async Task CancellingATaskParkedAwaitingContinuation_CancelsTheApprentice()
    {

        Harness harness = new();

        harness.QueueEscalation("which environment?");

        RequestContext request = harness.Request("deploy the thing", taskId: "task-1");

        await harness.RunAsync(request);

        Guid parked = Assert.Single(harness.Archmage.Created).Id;

        AgentEventQueue cancelQueue = new();

        await harness.Handler.CancelAsync(request, cancelQueue, CancellationToken.None);

        Assert.Equal([parked], harness.Runtime.CancelledApprenticeIds);

        // ExecuteAsync already returned when the task parked, so no relay survives to drive the terminal
        // transition. Cancelling the Apprentice without it leaves the peer's task at input-required forever.
        Assert.Equal(TaskState.Canceled, await DrainStateAsync(cancelQueue));

    }

    // ── #62 durable task correspondence ────────────────────────────────────────────────────────────

    [Fact]
    public async Task InboundSending_RecordsADurableTaskToApprenticeCorrespondence()
    {

        Harness harness = new();

        harness.QueueCompletion();

        await harness.RunAsync(harness.Request("do the thing", taskId: "task-42"));

        Guid apprenticeId = Assert.Single(harness.Archmage.Created).Id;

        Assert.Equal(apprenticeId, harness.Ledger.RegisteredInbound["task-42"]);

    }

    [Fact]
    public async Task SettledInboundSending_ClosesItsDurableRecord()
    {

        Harness harness = new();

        harness.QueueCompletion();

        await harness.RunAsync(harness.Request("do the thing", taskId: "task-43"));

        // A settled Sending must not be reconciled after the next restart.
        Assert.Single(harness.Ledger.Released);

    }

    [Fact]
    public async Task InboundSendingParkedAwaitingContinuation_KeepsItsDurableRecordOpenUntilItSettles()
    {

        Harness harness = new();

        harness.QueueEscalation("which environment?");

        await harness.RunAsync(harness.Request("deploy the thing", taskId: "task-1"));

        // Still in flight: the peer can answer it, so reconciliation must still see it.
        Assert.Empty(harness.Ledger.Released);

        harness.QueueCompletion();

        await harness.RunAsync(harness.Request("staging", taskId: "task-1"));

        Assert.Single(harness.Ledger.Released);

    }

    [Fact]
    public async Task PeerCancelAfterARestart_CancelsTheRealApprenticeInsteadOfAnsweringIntoTheVoid()
    {

        Harness harness = new();

        Guid resumed = Guid.NewGuid();

        // A restart empties the in-memory index. Only the durable record still knows who is serving this
        // task, and before #62 the peer got a polite "Canceled" while the work kept running.
        harness.Ledger.Recovered["task-77"] = resumed;

        AgentEventQueue queue = new();

        await harness.Handler.CancelAsync(harness.Request("ignored", taskId: "task-77"), queue, CancellationToken.None);

        Assert.Equal([resumed], harness.Runtime.CancelledApprenticeIds);

        Assert.Equal(TaskState.Canceled, await DrainStateAsync(queue));

    }

    [Fact]
    public async Task PeerCancelForAnUnknownTask_StillAnswersTerminallyWithoutCancellingAnything()
    {

        Harness harness = new();

        AgentEventQueue queue = new();

        await harness.Handler.CancelAsync(harness.Request("ignored", taskId: "unknown"), queue, CancellationToken.None);

        Assert.Empty(harness.Runtime.CancelledApprenticeIds);

        Assert.Equal(TaskState.Canceled, await DrainStateAsync(queue));

    }

    [Fact]
    public async Task OutboundReconciliation_CancelsTheOrphanedRemoteTaskAndRecordsThat()
    {

        RecordingCancelClient client = new(succeed: true);

        A2AOutboundSendingRecoveryHandler handler = new(
            client,
            NullLogger<A2AOutboundSendingRecoveryHandler>.Instance);

        LongRunningOperationRecoveryResult outcome = await handler.RecoverAsync(
            OutboundOperation("remote-1", "https://peer.example.test/"),
            CancellationToken.None);

        Assert.Equal(LongRunningOperationState.Completed, outcome.State);

        Assert.Equal([("https://peer.example.test/", "remote-1")], client.Cancelled);

    }

    [Fact]
    public async Task OutboundReconciliation_RecordsAbandonmentWhenTheRemoteCannotBeReached()
    {

        RecordingCancelClient client = new(succeed: false);

        A2AOutboundSendingRecoveryHandler handler = new(
            client,
            NullLogger<A2AOutboundSendingRecoveryHandler>.Instance);

        LongRunningOperationRecoveryResult outcome = await handler.RecoverAsync(
            OutboundOperation("remote-2", "https://peer.example.test/"),
            CancellationToken.None);

        // Which of the two happened has to be recorded: one costs nothing, the other may still be billing.
        Assert.Equal(LongRunningOperationState.Abandoned, outcome.State);

        Assert.Equal(A2ASendingRecoveryOutcomes.OutboundRemoteAbandoned, outcome.ErrorCode);

    }

    [Fact]
    public async Task InboundReconciliation_ExplicitlyAbandonsWithAReasonRatherThanLeavingTheTaskWorking()
    {

        Guid apprenticeId = Guid.NewGuid();

        FakeApprenticeStore apprentices = new()
        {
            Item = new Apprentice { Id = apprenticeId, Status = ApprenticeStatus.Paused.ToString() },
        };

        A2AInboundSendingRecoveryHandler handler = new(
            apprentices,
            NullLogger<A2AInboundSendingRecoveryHandler>.Instance);

        LongRunningOperationRecoveryResult outcome = await handler.RecoverAsync(
            InboundOperation("task-9", apprenticeId),
            CancellationToken.None);

        Assert.Equal(LongRunningOperationState.Abandoned, outcome.State);

        Assert.Equal(A2ASendingRecoveryOutcomes.InboundRelayAbandoned, outcome.ErrorCode);

    }

    [Fact]
    public async Task InboundReconciliation_NamesAMissingApprenticeSeparatelyFromALostRelay()
    {

        A2AInboundSendingRecoveryHandler handler = new(
            new FakeApprenticeStore(),
            NullLogger<A2AInboundSendingRecoveryHandler>.Instance);

        LongRunningOperationRecoveryResult outcome = await handler.RecoverAsync(
            InboundOperation("task-9", Guid.NewGuid()),
            CancellationToken.None);

        Assert.Equal(A2ASendingRecoveryOutcomes.InboundApprenticeMissing, outcome.ErrorCode);

    }

    // ── harness ────────────────────────────────────────────────────────────────────────────────────

    private static LongRunningOperation InboundOperation(
        string taskId,
        Guid apprenticeId) =>
        Operation(
            LongRunningOperationKinds.A2AInboundSending,
            new A2ASendingRecord
            {
                Direction = A2ASendingRecordDirection.Inbound,
                TaskId = taskId,
                ApprenticeId = apprenticeId,
            });

    private static LongRunningOperation OutboundOperation(
        string taskId,
        string agentUrl) =>
        Operation(
            LongRunningOperationKinds.A2AOutboundSending,
            new A2ASendingRecord
            {
                Direction = A2ASendingRecordDirection.Outbound,
                TaskId = taskId,
                AgentUrl = agentUrl,
            });

    private static LongRunningOperation Operation(
        string kind,
        A2ASendingRecord record) =>
        new(
            Guid.NewGuid(),
            kind,
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
                record,
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

            TaskStatus? status = response.PayloadCase switch
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

        public Harness()
        {

            Runtime = new RecordingRuntime(_chronicle.Reader);

            ServiceCollection services = new();

            services.AddSingleton<IConclaveArchmage>(Archmage);

            services.AddSingleton<IApprenticeRuntime>(Runtime);

            services.AddSingleton<IApprenticeRepository>(new FakeApprenticeStore());

            services.AddSingleton<ISessionRepository>(new StubSessions());

            services.AddSingleton<IA2ASendingLedger>(Ledger);

            IServiceScopeFactory scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

            Handler = new ArcanumA2AAgentHandler(
                scopeFactory,
                new TestOptionsMonitor<ArcanumSettings>(EnabledSettings()),
                NullLogger<ArcanumA2AAgentHandler>.Instance);

        }

        public RecordingArchmage Archmage { get; } = new();

        public RecordingRuntime Runtime { get; }

        public RecordingLedger Ledger { get; } = new();

        public ArcanumA2AAgentHandler Handler { get; }

        public RequestContext Request(
            string text,
            string taskId = "task-1",
            SendMessageConfiguration? configuration = null) => new()
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
            Configuration = configuration,
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

    private sealed class RecordingArchmage : IConclaveArchmage
    {

        public List<ConclaveCastRequest> Requests { get; } = [];

        public List<Apprentice> Created { get; } = [];

        public Task<Result<Apprentice>> CastAsync(ConclaveCastRequest request, CancellationToken cancellationToken = default)
        {

            Requests.Add(request);

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

    private sealed class RecordingLedger : IA2ASendingLedger
    {

        public Dictionary<string, Guid> RegisteredInbound { get; } = [];

        public Dictionary<string, Guid> Recovered { get; } = [];

        public List<A2ASendingLedgerEntry> Released { get; } = [];

        public Task<A2ASendingLedgerEntry> RegisterInboundAsync(
            string taskId,
            Guid apprenticeId,
            CancellationToken cancellationToken = default)
        {

            RegisteredInbound[taskId] = apprenticeId;

            return Task.FromResult(new A2ASendingLedgerEntry(Guid.NewGuid(), "test"));

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

        public Task<A2ASendingLedgerEntry> FindOpenOutboundAsync(
            string remoteTaskId,
            CancellationToken cancellationToken = default) => Task.FromResult<A2ASendingLedgerEntry>(default);

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

            Parked.Add((entry, contextId));

            return Task.CompletedTask;

        }

        public List<(A2ASendingLedgerEntry Entry, string? ContextId)> Parked { get; } = [];

        public Task<A2AParkedSending?> FindParkedInboundAsync(
            string taskId,
            bool takeLease = true,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<A2AParkedSending?>(null);

        public Task<Guid?> FindInboundApprenticeAsync(string taskId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Recovered.TryGetValue(taskId, out Guid id) ? id : (Guid?)null);

    }

    private sealed class RecordingCancelClient(bool succeed) : IA2AClientService
    {

        public List<(string AgentUrl, string TaskId)> Cancelled { get; } = [];

        public Task<Result<A2ADispatchResult>> DispatchSendingAsync(
            string goal,
            string? name,
            string agentUrl,
            IReadOnlyList<string>? delegationChain = null,
            CancellationToken cancellationToken = default,
            IProgress<A2ASendingProgress>? progress = null,
            A2ADispatchMode mode = A2ADispatchMode.Blocking,
            A2ASendingOptions? options = null) => throw new NotSupportedException();

        public Task<Result<A2ADispatchResult>> ContinueSendingAsync(
            string agentUrl,
            string taskId,
            string message,
            IReadOnlyList<string>? delegationChain = null,
            CancellationToken cancellationToken = default,
            IProgress<A2ASendingProgress>? progress = null,
            A2ADispatchMode mode = A2ADispatchMode.Blocking,
            A2ASendingOptions? options = null) => throw new NotSupportedException();

        public Task<Result> CancelRemoteTaskAsync(
            string agentUrl,
            string taskId,
            CancellationToken cancellationToken = default)
        {

            Cancelled.Add((agentUrl, taskId));

            return Task.FromResult(succeed
                ? Result.Success()
                : Result.Failure(new Error(ErrorCodes.Sending.AgentUnreachable, "unreachable")));

        }

    }

    private sealed class FakeApprenticeStore : IApprenticeRepository
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

    private sealed class StubSessions : ISessionRepository
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
