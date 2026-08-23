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
using RetroDownfall.Arcanum.Core.Conclave;
using RetroDownfall.Arcanum.Core.Tower;
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
    public async Task ParkedContinuations_AreBounded_AndTheEvictedOneResolvesThroughItsDurableRecord()
    {

        Harness harness = new();

        // Only an answer or a cancel ever retires a park, so every peer that escalates and then does
        // neither leaves an entry behind for the life of the process. Both sibling A2A indices are capped
        // (ArcanumA2ATaskStore.RetainedTaskCap, the push-notification registration ceiling); this one has
        // to be too, on a key a peer chooses.
        for (int index = 0; index <= ArcanumA2AAgentHandler.MaxParkedContinuations; index++)
        {

            harness.QueueEscalation("which environment should I deploy to?");

            _ = await harness.RunAsync(harness.Request("deploy the service", taskId: $"task-{index}"));

        }

        Guid firstApprentice = harness.Archmage.Created[0].Id;

        harness.QueueCompletion();

        _ = await harness.RunAsync(harness.Request("use staging", taskId: "task-0"));

        // Every Sending's accept path already asks the ledger once, before anything is parked, so a
        // *second* lookup for the same task id is the eviction showing: the fast-path entry is gone and
        // the answer resolves the way one that outlived its process does. It still lands on the very same
        // Apprentice, which is why the ceiling costs the fast path rather than the work.
        Assert.Equal(2, harness.Ledger.ParkedLookups.Count(static id => string.Equals(id, "task-0", StringComparison.Ordinal)));

        Assert.Equal([firstApprentice], harness.Runtime.IntervenedApprenticeIds);

        string newest = $"task-{ArcanumA2AAgentHandler.MaxParkedContinuations}";

        harness.QueueCompletion();

        _ = await harness.RunAsync(harness.Request("use staging", taskId: newest));

        // The ceiling drops the least recently parked task, not the one the peer is most likely about to
        // answer: the newest park is still on the fast path and needs no durable lookup at all.
        Assert.Equal(1, harness.Ledger.ParkedLookups.Count(id => string.Equals(id, newest, StringComparison.Ordinal)));

        Assert.Empty(harness.Archmage.Created.Skip(ArcanumA2AAgentHandler.MaxParkedContinuations + 1));

    }

    [Fact]
    public async Task CancelRacingTheParkOfAnEscalation_StillDrivesTheTerminalTransitionItself()
    {

        Harness harness = new();

        harness.QueueEscalation("which environment should I deploy to?");

        AgentEventQueue cancelQueue = new();

        // The escalation publishes the park before ExecuteAsync's finally drops the live mapping, and the
        // durable park write sits inside that window. A tasks/cancel landing here is the one interleaving
        // where both indices hold the task at once — and the relay has already committed to returning, so
        // nothing else is left to drive this task's terminal transition.
        harness.Ledger.OnMarkParked = () => harness.Handler.CancelAsync(
            harness.Request("", taskId: "task-1"),
            cancelQueue,
            CancellationToken.None);

        _ = await harness.RunAsync(harness.Request("deploy the service"));

        Guid apprenticeId = Assert.Single(harness.Archmage.Created).Id;

        Assert.Equal([apprenticeId], harness.Runtime.CancelledApprenticeIds);

        // The peer asked for a cancel and its Apprentice really was cancelled; answering nothing leaves
        // the task with no terminal state at all while the work behind it is already stopped.
        Assert.Equal(TaskState.Canceled, await DrainStateAsync(cancelQueue));

        // The park is gone with the cancel, so the durable record settles with it instead of staying open
        // for reconciliation to keep re-examining.
        Assert.NotEmpty(harness.Ledger.Released);

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
    public async Task TaskStore_RehydratesAParkedTaskOnce_AndThenServesItFromMemory()
    {

        FakeParkedLedger ledger = new();

        ledger.Parked["task-1"] = new ParkedRow(Guid.NewGuid(), "ctx-restored", default);

        ArcanumA2ATaskStore store = new(ScopeFactoryFor(ledger), NullLogger<ArcanumA2ATaskStore>.Instance);

        AgentTask? first = await store.GetTaskAsync("task-1");

        AgentTask? second = await store.GetTaskAsync("task-1");

        Assert.Equal("ctx-restored", first!.ContextId);

        Assert.Equal(TaskState.InputRequired, second!.Status.State);

        // The SDK resolves the task on every request that names one, and this lookup has no indexed
        // answer — the A2A task id lives inside the checkpoint blob, so it is a paged scan of every open
        // Sending row in the ledger. What it recovers cannot change until the peer answers or cancels,
        // and both of those re-save this entry through the handler, so a peer polling a parked Sending
        // must not re-scan the whole ledger once per tasks/get.
        Assert.Equal(["task-1"], ledger.ParkedLookups);

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

    [Fact]
    public async Task TaskStore_RetainsOnlyABoundedNumberOfSettledTasks()
    {

        ArcanumA2ATaskStore store = new(scopeFactory: null, NullLogger<ArcanumA2ATaskStore>.Instance);

        await store.SaveTaskAsync("still-working", Retained("still-working", TaskState.Working));

        for (int i = 0; i < ArcanumA2ATaskStore.RetainedTaskCap + 50; i++)
        {

            await store.SaveTaskAsync($"settled-{i}", Retained($"settled-{i}", TaskState.Completed));

        }

        // The SDK documents that it never calls DeleteTaskAsync and leaves pruning to the store, and
        // nothing in Arcanum calls it either — so without a retention policy every inbound Sending's
        // whole relayed history and final artifact is pinned until the host restarts.
        Assert.Null(await store.GetTaskAsync("settled-0"));

        Assert.NotNull(await store.GetTaskAsync($"settled-{ArcanumA2ATaskStore.RetainedTaskCap + 49}"));

        // A task that has not settled is never a candidate: it is live state nothing can rebuild, and
        // the peer driving it is still entitled to a tasks/get.
        Assert.NotNull(await store.GetTaskAsync("still-working"));

    }

    [Fact]
    public async Task TaskStore_NeverEvictsAnUnsettledTaskToStayUnderTheCap()
    {

        ArcanumA2ATaskStore store = new(scopeFactory: null, NullLogger<ArcanumA2ATaskStore>.Instance);

        for (int i = 0; i < ArcanumA2ATaskStore.RetainedTaskCap + 50; i++)
        {

            await store.SaveTaskAsync($"working-{i}", Retained($"working-{i}", TaskState.Working));

        }

        // Genuinely that much in-flight work is real work, and dropping any of it would answer a live
        // peer TaskNotFound. The cap bounds what is kept for nothing, not what is being served.
        Assert.NotNull(await store.GetTaskAsync("working-0"));

        Assert.NotNull(await store.GetTaskAsync($"working-{ArcanumA2ATaskStore.RetainedTaskCap + 49}"));

    }

    private static AgentTask Retained(string taskId, TaskState state) => new()
    {
        Id = taskId,
        ContextId = "ctx-retention",
        Status = new A2ATaskStatus { State = state },
    };

    // ── tasks/list is a query, not a dump ──────────────────────────────────────────────────────────

    [Fact]
    public async Task TaskStore_ListTasks_HonoursTheContextAndStatusFiltersItIsGiven()
    {

        ArcanumA2ATaskStore store = new(scopeFactory: null, NullLogger<ArcanumA2ATaskStore>.Instance);

        await SeedListableAsync(store, "mine-1", "ctx-mine", TaskState.Completed, minute: 1);

        await SeedListableAsync(store, "mine-2", "ctx-mine", TaskState.Working, minute: 2);

        await SeedListableAsync(store, "theirs-1", "ctx-theirs", TaskState.Completed, minute: 3);

        ListTasksResponse scoped = await store.ListTasksAsync(new ListTasksRequest { ContextId = "ctx-mine" });

        // A peer that scopes tasks/list to its own context is asking a question; answering with every
        // task the process happens to be holding is not an answer to it.
        Assert.Equal(["mine-2", "mine-1"], scoped.Tasks.Select(static task => task.Id));

        Assert.Equal(2, scoped.TotalSize);

        ListTasksResponse working = await store.ListTasksAsync(new ListTasksRequest { Status = TaskState.Working });

        Assert.Equal(["mine-2"], working.Tasks.Select(static task => task.Id));

        ListTasksResponse recent = await store.ListTasksAsync(
            new ListTasksRequest { StatusTimestampAfter = ListBaseTime.AddMinutes(2) });

        Assert.Equal(["theirs-1"], recent.Tasks.Select(static task => task.Id));

    }

    [Fact]
    public async Task TaskStore_ListTasks_PagesAndReportsWhereTheNextPageStarts()
    {

        ArcanumA2ATaskStore store = new(scopeFactory: null, NullLogger<ArcanumA2ATaskStore>.Instance);

        for (int i = 0; i < 5; i++)
        {

            await SeedListableAsync(store, $"task-{i}", "ctx", TaskState.Completed, minute: i);

        }

        ListTasksResponse first = await store.ListTasksAsync(new ListTasksRequest { PageSize = 2 });

        Assert.Equal(["task-4", "task-3"], first.Tasks.Select(static task => task.Id));

        // All three are [JsonRequired] on the wire: leaving them at their defaults while handing back N
        // tasks makes a compliant client's pagination arithmetic wrong.
        Assert.Equal(2, first.PageSize);

        Assert.Equal(5, first.TotalSize);

        Assert.NotEqual(string.Empty, first.NextPageToken);

        ListTasksResponse second = await store.ListTasksAsync(
            new ListTasksRequest { PageSize = 2, PageToken = first.NextPageToken });

        Assert.Equal(["task-2", "task-1"], second.Tasks.Select(static task => task.Id));

        ListTasksResponse last = await store.ListTasksAsync(
            new ListTasksRequest { PageSize = 2, PageToken = second.NextPageToken });

        Assert.Equal(["task-0"], last.Tasks.Select(static task => task.Id));

        Assert.Equal(string.Empty, last.NextPageToken);

    }

    [Fact]
    public async Task TaskStore_ListTasks_TrimsHistoryAndOmitsArtifactsUnlessAsked()
    {

        ArcanumA2ATaskStore store = new(scopeFactory: null, NullLogger<ArcanumA2ATaskStore>.Instance);

        await SeedListableAsync(store, "task-1", "ctx", TaskState.Completed, minute: 1);

        ListTasksResponse defaulted = await store.ListTasksAsync(new ListTasksRequest());

        // The Apprentice's whole final answer rides in Artifacts; a listing is not the place to hand it
        // out unasked, and the SDK's own reference store does not.
        Assert.Null(Assert.Single(defaulted.Tasks).Artifacts);

        ListTasksResponse trimmed = await store.ListTasksAsync(
            new ListTasksRequest { HistoryLength = 1, IncludeArtifacts = true });

        AgentTask listed = Assert.Single(trimmed.Tasks);

        Assert.Equal(["m3"], listed.History!.Select(static message => message.MessageId));

        Assert.NotNull(listed.Artifacts);

        // Projecting a listing must not edit the task this process is still serving.
        AgentTask? stored = await store.GetTaskAsync("task-1");

        Assert.Equal(3, stored!.History!.Count);

        Assert.NotNull(stored.Artifacts);

    }

    private static readonly DateTimeOffset ListBaseTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static Task SeedListableAsync(
        ArcanumA2ATaskStore store,
        string taskId,
        string contextId,
        TaskState state,
        int minute) =>
        store.SaveTaskAsync(
            taskId,
            new AgentTask
            {
                Id = taskId,
                ContextId = contextId,
                Status = new A2ATaskStatus { State = state, Timestamp = ListBaseTime.AddMinutes(minute) },
                History =
                [
                    new Message { Role = Role.User, MessageId = "m1", Parts = [Part.FromText("goal")] },
                    new Message { Role = Role.Agent, MessageId = "m2", Parts = [Part.FromText("working")] },
                    new Message { Role = Role.Agent, MessageId = "m3", Parts = [Part.FromText("done")] },
                ],
                Artifacts = [new Artifact { ArtifactId = "a1", Parts = [Part.FromText("the answer")] }],
            });

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

        public Task<A2ASendingLedgerEntry> FindOpenOutboundAsync(
            string remoteTaskId,
            CancellationToken cancellationToken = default) => Task.FromResult<A2ASendingLedgerEntry>(default);

        public Task ReleaseAsync(A2ASendingLedgerEntry entry, CancellationToken cancellationToken = default)
        {

            Released.Add(entry);

            return Task.CompletedTask;

        }

        /// <summary>
        /// Runs inside <see cref="MarkParkedAsync"/>, so a test can land a peer request in the window
        /// between the park being published and the relay's live mapping being dropped.
        /// </summary>
        public Func<Task>? OnMarkParked { get; set; }

        public async Task MarkParkedAsync(
            A2ASendingLedgerEntry entry,
            string? contextId,
            CancellationToken cancellationToken = default)
        {

            if (_entryTaskIds.TryGetValue(entry.OperationId, out string? taskId)
                && RegisteredInbound.TryGetValue(taskId, out Guid apprenticeId))
            {

                Parked[taskId] = new ParkedRow(apprenticeId, contextId, entry);

            }

            if (OnMarkParked is { } hook)
            {

                await hook().ConfigureAwait(false);

            }

        }

        /// <summary>Every task id the durable park lookup was asked about, in order.</summary>
        public List<string> ParkedLookups { get; } = [];

        public Task<A2AParkedSending?> FindParkedInboundAsync(
            string taskId,
            bool takeLease = true,
            CancellationToken cancellationToken = default)
        {

            ParkedLookups.Add(taskId);

            return Task.FromResult(Parked.TryGetValue(taskId, out ParkedRow row)
                ? new A2AParkedSending(row.ApprenticeId, row.ContextId, row.Ledger)
                : (A2AParkedSending?)null);

        }

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
