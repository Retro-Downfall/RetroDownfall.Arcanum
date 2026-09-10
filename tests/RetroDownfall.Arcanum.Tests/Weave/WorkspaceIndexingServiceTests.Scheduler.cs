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
