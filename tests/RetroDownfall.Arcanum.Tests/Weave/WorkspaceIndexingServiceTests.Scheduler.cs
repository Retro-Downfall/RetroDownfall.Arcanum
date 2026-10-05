using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Weave;

public sealed partial class WorkspaceIndexingServiceTests
{
    [SkippableFact]
    public async Task Manual_burst_coalesces_without_overflow_or_per_request_runtime_state()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _workspace.WriteFile("one.cs", "class One {}");

        GatedWeaveService weave = new();

        WorkspaceIndexingService service = CreateService(weave, out _);

        Assert.True(service.QueueIndexNow(_workspace.Root).IsSuccess);

        await weave.EmbedEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        for (int request = 0; request < 1_000; request++)
        {
            Assert.Equal(WorkspaceIndexQueueDisposition.Coalesced, service.QueueIndexNow(_workspace.Root).Value);
        }

        Assert.Equal(new WorkspaceIndexingService.WorkspaceSchedulerSnapshot(1, 0, false), service.GetSchedulerSnapshot());

        weave.Release();

        await DrainWorkspaceSchedulerAsync(service);

        Assert.Equal(1, weave.EmbedBatchCallCount);
    }

    [SkippableFact]
    public async Task Queued_workspace_root_replacement_is_rejected_before_provider_or_scope_mutation()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _workspace.WriteFile("first/one.cs", "class One {}");

        _workspace.WriteFile("second/two.cs", "class Two {}");

        _workspace.WriteFile("third/three.cs", "class Original {}");

        GatedWeaveService weave = new();

        ObservingScopeFactory scopes = new(BuildScopeFactory());

        WorkspaceIndexingService service = CreateService(weave, out _, scopeFactory: scopes);

        Assert.True(service.QueueIndexNow(Path.Combine(_workspace.Root, "first")).IsSuccess);

        Assert.True(service.QueueIndexNow(Path.Combine(_workspace.Root, "second")).IsSuccess);

        await weave.SecondEmbedEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        string third = Path.Combine(_workspace.Root, "third");

        Assert.True(service.QueueIndexNow(third).IsSuccess);

        Directory.Move(third, Path.Combine(_workspace.Root, "retired-third"));

        Directory.CreateDirectory(third);

        File.WriteAllText(Path.Combine(third, "replacement.cs"), "class Replacement {}");

        weave.Release();

        await DrainWorkspaceSchedulerAsync(service);

        Assert.Equal(2, weave.EmbedBatchCallCount);

        Assert.Equal(2, scopes.ScopeCount);

        Assert.True(service.GetRuntimeStatus(third).Degraded);
    }

    [SkippableFact]
    public async Task Pending_successor_does_not_block_a_free_slot_for_another_workspace()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _workspace.WriteFile("first/one.cs", "class One {}");

        _workspace.WriteFile("second/two.cs", "class Two {}");

        string firstPath = Path.Combine(_workspace.Root, "first");

        GatedWeaveService weave = new();

        WorkspaceIndexingService service = CreateService(weave, out _);

        Assert.True(service.QueueIndexNow(firstPath).IsSuccess);

        await weave.EmbedEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        service.UnregisterWorkspace(firstPath);

        Assert.True(service.QueueIndexNow(firstPath).IsSuccess);

        Assert.True(service.QueueIndexNow(Path.Combine(_workspace.Root, "second")).IsSuccess);

        try
        {
            Assert.Equal(new WorkspaceIndexingService.WorkspaceSchedulerSnapshot(2, 0, false), service.GetSchedulerSnapshot());
        }
        finally
        {
            weave.Release();

            await DrainWorkspaceSchedulerAsync(service);
        }

        Assert.Equal(new WorkspaceIndexingService.WorkspaceSchedulerSnapshot(0, 0, false), service.GetSchedulerSnapshot());
    }

    [SkippableFact]
    public async Task Stop_joins_the_owned_manual_handle_until_provider_cleanup_finishes()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _workspace.WriteFile("one.cs", "class One {}");

        GatedWeaveService weave = new();

        WorkspaceIndexingService service = CreateService(weave, out _);

        Assert.True(service.QueueIndexNow(_workspace.Root).IsSuccess);

        await weave.EmbedEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Task stop = service.StopAsync(CancellationToken.None);

        try
        {
            Assert.False(stop.IsCompleted);
        }
        finally
        {
            weave.Release();

            await stop.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    [SkippableFact]
    public async Task Retired_watcher_error_cannot_dispose_a_new_registration_at_the_same_path()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        FakeWorkspaceFileWatcherFactory watchers = new();

        WorkspaceIndexingService service = CreateService(new FakeWeaveService(), out _, watcherFactory: watchers);

        service.RegisterWorkspace(_workspace.Root);

        FakeWorkspaceFileWatcher retired = watchers.Single;

        service.UnregisterWorkspace(_workspace.Root);

        service.RegisterWorkspace(_workspace.Root);

        FakeWorkspaceFileWatcher current = watchers.Created[1];

        retired.TriggerError(new InternalBufferOverflowException("late callback"));

        Assert.False(current.IsDisposed);

        Assert.Equal(1, service.ActiveWatcherCount);

        await service.StopAsync(CancellationToken.None);
    }

    [SkippableFact]
    public async Task Third_workspace_demand_remains_data_until_a_capacity_two_slot_finishes()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _workspace.WriteFile("first/one.cs", "class First {}");

        _workspace.WriteFile("second/two.cs", "class Second {}");

        _workspace.WriteFile("third/three.cs", "class Third {}");

        RecordingGrimoireWorkAdmissionGate gate = new(new GrimoireConnectionAdmissionGate(TimeProvider.System));

        GatedWeaveService weave = new();

        WorkspaceIndexingService service = CreateService(weave, out _, workAdmission: gate);

        Assert.Equal(WorkspaceIndexQueueDisposition.Accepted, service.QueueIndexNow(Path.Combine(_workspace.Root, "first")).Value);

        Assert.Equal(WorkspaceIndexQueueDisposition.Accepted, service.QueueIndexNow(Path.Combine(_workspace.Root, "second")).Value);

        await weave.SecondEmbedEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(new WorkspaceIndexingService.WorkspaceSchedulerSnapshot(2, 0, false), service.GetSchedulerSnapshot());

        Assert.Equal(WorkspaceIndexQueueDisposition.Accepted, service.QueueIndexNow(Path.Combine(_workspace.Root, "third")).Value);

        try
        {
            // Three watcher constructions and the two active indexing slots own work leases.
            // The queued third workspace must not acquire a sixth execution lease yet.
            Assert.Equal(5, gate.RequestedWorkKinds.Count);

            Assert.Equal(new WorkspaceIndexingService.WorkspaceSchedulerSnapshot(2, 1, true), service.GetSchedulerSnapshot());
        }
        finally
        {
            weave.Release();

            await DrainWorkspaceSchedulerAsync(service);

            await service.StopAsync(CancellationToken.None);
        }

        Assert.Equal(3, weave.EmbedBatchCallCount);

        Assert.Equal(new WorkspaceIndexingService.WorkspaceSchedulerSnapshot(0, 0, false), service.GetSchedulerSnapshot());
    }

    /// <summary>
    /// A Full unit that stops on the per-checkpoint file budget with changed files remaining must schedule
    /// the remainder itself. Nothing else does: the run reported Completed and reconciled, so initial
    /// indexing of a large repository would otherwise take one reconciliation interval per budget's worth.
    /// </summary>
    [SkippableFact]
    public async Task Budget_exhausted_full_tick_requeues_remaining_changed_files()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _workspace.WriteFile("one.txt", "one");

        _workspace.WriteFile("two.txt", "two");

        _workspace.WriteFile("three.txt", "three");

        FakeWeaveService weave = new();

        WorkspaceIndexingService service = CreateService(weave, out _);

        service.MaxFilesToIndexOverride = 1;

        Assert.True(service.QueueIndexNow(_workspace.Root).IsSuccess);

        // The follow-up unit is published under the lock that retires the finished one, so the
        // scheduler reads idle only once no changed file is left.
        await DrainWorkspaceSchedulerAsync(service);

        Assert.Equal(
            ["one.txt", "three.txt", "two.txt"],
            (await GetIndexedRelativePathsAsync()).OrderBy(static path => path, StringComparer.Ordinal).ToArray());

        // Settled: a drained scheduler does not keep re-walking once nothing is left to index.
        int embeddingCalls = weave.EmbedBatchCallCount;

        await Task.Delay(TimeSpan.FromMilliseconds(500));

        Assert.Equal(embeddingCalls, weave.EmbedBatchCallCount);

        Assert.Equal(new WorkspaceIndexingService.WorkspaceSchedulerSnapshot(0, 0, false), service.GetSchedulerSnapshot());

        await service.DisposeAsync();
    }

    /// <summary>
    /// The continuation of a forced reconciliation stays forced and remembers what it already re-embedded.
    /// Losing the force would leave the unvisited files unrefreshed; losing the memory would re-embed the
    /// first file in every follow-up and never reach the rest.
    /// </summary>
    [SkippableFact]
    public async Task Budget_exhausted_forced_reconciliation_continues_forced_without_repeating_files()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _workspace.WriteFile("one.txt", "one");

        _workspace.WriteFile("two.txt", "two");

        _workspace.WriteFile("three.txt", "three");

        FakeWorkspaceFileWatcherFactory watchers = new();

        RecordingGrimoireWorkAdmissionGate gate = new(new GrimoireConnectionAdmissionGate(TimeProvider.System));

        WorkspaceIndexingService service = CreateService(new FakeWeaveService(), out _, watcherFactory: watchers, workAdmission: gate);

        service.MaxFilesToIndexOverride = 1;

        service.RegisterWorkspace(_workspace.Root);

        Assert.True(service.QueueIndexNow(_workspace.Root).IsSuccess);

        await DrainWorkspaceSchedulerAsync(service);

        // Each file indexed owns one effect group: the first reconciliation indexes all three.
        Assert.Equal(3, gate.EffectGroupAttempts);

        // Lost watcher hints force the whole inventory to be re-read even though nothing changed; an
        // unchanged file is re-read without being re-embedded, so the effect groups count the visits.
        watchers.Single.TriggerError(new IOException("Watcher lost events."));

        await ProcessPendingWatcherEventsAsync(service, _workspace.Root, CancellationToken.None);

        Assert.Equal(6, gate.EffectGroupAttempts);

        await service.DisposeAsync();
    }

    /// <summary>
    /// A build writing thousands of files under <c>obj/</c> must not fill the 4,096-path coalescer: queued,
    /// those events cost a delete statement apiece and overflow into a forced full re-walk of the whole
    /// workspace.
    /// </summary>
    [SkippableFact]
    public async Task WatcherEvents_UnderIgnoredDirectories_NeverEnterPendingDemand()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        FakeWorkspaceFileWatcherFactory watchers = new();

        FakeWeaveService weave = new();

        WorkspaceIndexingService service = CreateService(weave, out _, watcherFactory: watchers);

        service.RegisterWorkspace(_workspace.Root);

        FakeWorkspaceFileWatcher watcher = watchers.Single;

        string generated = Path.Combine(_workspace.Root, "obj", "Debug");

        for (int index = 0; index < 5_000; index++)
        {
            watcher.TriggerCreated(Path.Combine(generated, $"generated-{index}.json"));
        }

        watcher.TriggerChanged(Path.Combine(_workspace.Root, "node_modules", "pkg", "index.js"));

        watcher.TriggerDeleted(Path.Combine(_workspace.Root, ".git", "index"));

        watcher.TriggerRenamed(
            Path.Combine(_workspace.Root, "build", "old.json"),
            Path.Combine(_workspace.Root, "dist", "new.json"));

        WorkspaceIndexRuntimeStatus afterEvents = service.GetRuntimeStatus(_workspace.Root);

        Assert.NotNull(afterEvents.LastEventAt);

        Assert.False(afterEvents.Overflowed);

        Assert.False(afterEvents.Degraded);

        await ProcessPendingWatcherEventsAsync(service, _workspace.Root, CancellationToken.None);

        WorkspaceIndexRuntimeStatus afterDrain = service.GetRuntimeStatus(_workspace.Root);

        // No demand ever existed, so no handle ran: nothing was reconciled, indexed or embedded.
        Assert.Null(afterDrain.LastSuccessfulIndexAt);

        Assert.False(afterDrain.Overflowed);

        Assert.Equal(0, weave.EmbedBatchCallCount);

        await service.DisposeAsync();
    }

    private static async Task ProcessPendingWatcherEventsAsync(WorkspaceIndexingService service, string path, CancellationToken cancellationToken)
    {
        service.QueuePendingWatcherEvents(path, cancellationToken);

        await DrainWorkspaceSchedulerAsync(service);
    }

    private static async Task DrainWorkspaceSchedulerAsync(WorkspaceIndexingService service)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));

        while (service.GetSchedulerSnapshot() is not { ActiveCount: 0, QueuedCount: 0 })
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), deadline.Token);
        }
    }
}
