using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Weave;

public sealed partial class WorkspaceIndexingServiceTests
{
    [SkippableFact]
    public async Task Unpublished_watcher_disposal_failure_still_settles_registration_ownership()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        FakeWorkspaceFileWatcherFactory watchers = new();

        WorkspaceIndexingService service = CreateService(new FakeWeaveService(), out _, watcherFactory: watchers);

        watchers.BeforeReturn = () =>
        {
            watchers.Single.AfterDispose = static () => throw new IOException("Unpublished watcher disposal failed.");

            service.UnregisterWorkspace(_workspace.Root);
        };

        _services.Remove(service);

        Assert.Throws<IOException>(() => service.RegisterWorkspace(_workspace.Root));

        Assert.Equal(0, typeof(WorkspaceIndexingService).GetField("_watcherCreations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service));

        await service.DisposeAsync();
    }

    [SkippableFact]
    public void Watcher_error_disposal_failure_still_signals_pending_reconciliation()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        FakeWorkspaceFileWatcherFactory watchers = new();

        WorkspaceIndexingService service = CreateService(new FakeWeaveService(), out _, watcherFactory: watchers);

        service.RegisterWorkspace(_workspace.Root);

        watchers.Single.AfterDispose = static () => throw new IOException("Watcher disposal failed.");

        Assert.Throws<IOException>(() => watchers.Single.TriggerError(new IOException("Watcher failed.")));

        SemaphoreSlim signal = (SemaphoreSlim)typeof(WorkspaceIndexingService).GetField("_watcherSignal", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;

        Assert.Equal(1, signal.CurrentCount);

        Assert.True(service.GetRuntimeStatus(_workspace.Root).Degraded);
    }

    [SkippableFact]
    public async Task Watcher_disposal_failure_does_not_abandon_the_other_owned_watchers()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        FakeWorkspaceFileWatcherFactory watchers = new();

        WorkspaceIndexingService service = CreateService(new FakeWeaveService(), out _, watcherFactory: watchers);

        service.RegisterWorkspace(_workspace.Root);

        service.RegisterWorkspace(_workspace.CreateSubdir("second"));

        watchers.Created[0].AfterDispose = static () => throw new IOException("Watcher disposal failed.");

        _services.Remove(service);

        await Assert.ThrowsAnyAsync<Exception>(() => service.StopAsync(CancellationToken.None));

        await Assert.ThrowsAnyAsync<Exception>(() => service.DisposeAsync().AsTask());

        Assert.All(watchers.Created, watcher => Assert.True(watcher.IsDisposed));

        Assert.Equal(0, service.ActiveWatcherCount);
    }

    [SkippableFact]
    public async Task Watcher_disposal_failure_during_unregister_still_cancels_the_exact_active_handle()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _workspace.WriteFile("one.cs", "class One {}");

        FakeWorkspaceFileWatcherFactory watchers = new();

        ControlledWorkspaceWeave weave = new();

        WorkspaceIndexingService service = CreateService(weave, out _, watcherFactory: watchers);

        Assert.True(service.QueueIndexNow(_workspace.Root).IsSuccess);

        WorkspaceEmbeddingCall call = await weave.NextAsync();

        watchers.Single.AfterDispose = static () => throw new IOException("Watcher disposal failed.");

        try
        {
            Assert.Throws<IOException>(() => service.UnregisterWorkspace(_workspace.Root));

            Assert.True(call.CancellationToken.IsCancellationRequested);
        }
        finally
        {
            call.Release();

            await service.StopAsync(CancellationToken.None);
        }
    }

    [SkippableFact]
    public async Task Stop_cancellation_callback_failure_still_joins_scope_cleanup_and_disposes_resources()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _workspace.WriteFile("one.cs", "class One {}");

        ControlledWorkspaceWeave weave = new();

        WorkspaceIndexingService service = CreateService(weave, out _);

        Assert.True(service.QueueIndexNow(_workspace.Root).IsSuccess);

        WorkspaceEmbeddingCall call = await weave.NextAsync();

        using CancellationTokenRegistration callback = call.CancellationToken.Register(static () => throw new InvalidOperationException("Cancellation callback failed."));

        Task stop = service.StopAsync(CancellationToken.None);

        _services.Remove(service);

        try
        {
            Assert.False(stop.IsCompleted);
        }
        finally
        {
            call.Release();

            await Assert.ThrowsAnyAsync<Exception>(() => stop.WaitAsync(TimeSpan.FromSeconds(30)));

            await DrainWorkspaceSchedulerAsync(service);

            await Assert.ThrowsAnyAsync<Exception>(() => service.DisposeAsync().AsTask());
        }

        Assert.Equal(1, typeof(WorkspaceIndexingService).GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service));
    }

    [SkippableFact]
    public async Task Failed_scheduled_sweep_retries_with_existing_backoff_not_full_cadence()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        FailingScopeFactory scopes = new();

        WorkspaceIndexingService service = CreateService(new FakeWeaveService(), out _, scopeFactory: scopes);

        service.RegisterWorkspace(_workspace.Root);

        DateTimeOffset before = DateTimeOffset.UtcNow;

        await service.StartAsync(CancellationToken.None);

        await WaitForWorkspaceConditionAsync(() => scopes.ScopeCount == 1 && service.GetScheduledSweepSnapshot().Outstanding == 0);

        try
        {
            Assert.InRange(service.GetScheduledSweepSnapshot().NextReconciliation, before, DateTimeOffset.UtcNow.AddSeconds(2));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// One vanished workspace must not make the scheduler re-walk every healthy workspace each second: the
    /// retry belongs to the entry that failed, with its own backoff, and the healthy entries wait for the
    /// next scheduled reconciliation.
    /// </summary>
    [SkippableFact]
    public async Task Failed_sweep_does_not_repeat_full_walk_of_healthy_workspaces_each_second()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string healthy = Path.Combine(_workspace.Root, "healthy");

        string vanished = _workspace.CreateSubdir("vanished");

        _workspace.WriteFile("healthy/one.cs", "class One {}");

        ObservingScopeFactory scopes = new(BuildScopeFactory());

        WorkspaceIndexingService service = CreateService(new FakeWeaveService(), out _, scopeFactory: scopes);

        service.RegisterWorkspace(healthy);

        service.RegisterWorkspace(vanished);

        Directory.Delete(vanished);

        await service.StartAsync(CancellationToken.None);

        try
        {
            // The first sweep walks the healthy workspace once and fails the vanished one.
            await WaitForWorkspaceConditionAsync(() => scopes.ScopeCount == 1 && service.GetScheduledSweepSnapshot().Outstanding == 0);

            // Long enough for at least two one-second retries of the failed entry.
            await Task.Delay(TimeSpan.FromSeconds(3.5));

            Assert.Equal(1, scopes.ScopeCount);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 8)]
    [InlineData(9, 256)]
    [InlineData(10, 300)]
    [InlineData(31, 300)]
    [InlineData(500, 300)]
    public void Retry_delay_doubles_per_consecutive_failure_and_is_capped_at_the_ceiling(int failures, int expectedSeconds)
    {
        TimeSpan delay = WorkspaceIndexingService.NextRetryDelay(failures, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5));

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), delay);
    }

    [Fact]
    public void Retry_delay_requires_at_least_one_failure()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => WorkspaceIndexingService.NextRetryDelay(0, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5)));
    }

    /// <summary>
    /// Registration happens on every inference turn and nothing else removes an entry, so a deleted working
    /// directory is retried until the process exits unless its repeated failures evict it. Eviction keeps
    /// every healthy entry and lets a later registration of the same path start from the new root.
    /// </summary>
    [SkippableFact]
    public async Task Workspace_with_a_persistently_unavailable_root_is_evicted_and_registers_afresh()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string healthy = Path.Combine(_workspace.Root, "healthy");

        string vanished = _workspace.CreateSubdir("vanished");

        _workspace.WriteFile("healthy/one.cs", "class One {}");

        FakeWorkspaceFileWatcherFactory watchers = new();

        WorkspaceIndexingService service = CreateService(new FakeWeaveService(), out _, watcherFactory: watchers);

        service.RetryBackoffBaseDelayOverrideForTests = TimeSpan.FromMilliseconds(1);

        service.RegisterWorkspace(healthy);

        service.RegisterWorkspace(vanished);

        FakeWorkspaceFileWatcher vanishedWatcher = Assert.Single(watchers.Created, watcher => watcher.WorkspacePath == vanished);

        Directory.Delete(vanished);

        await service.StartAsync(CancellationToken.None);

        try
        {
            await WaitForWorkspaceConditionAsync(() => vanishedWatcher.IsDisposed);

            Assert.Equal(1, service.ActiveWatcherCount);

            Assert.True(service.GetRuntimeStatus(healthy).Watching);

            Assert.False(service.GetRuntimeStatus(vanished).Watching);

            Directory.CreateDirectory(vanished);

            service.RegisterWorkspace(vanished);

            Assert.Equal(2, service.ActiveWatcherCount);

            Assert.True(service.GetRuntimeStatus(vanished).Watching);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// A watcher factory that keeps failing (an exhausted inotify budget, say) delivered no events, so it
    /// must neither be retried on every scheduler cycle nor force a full re-read of the workspace each
    /// time: the periodic reconciliation already covers a workspace nothing watches.
    /// </summary>
    [SkippableFact]
    public async Task Persistent_watcher_creation_failure_backs_off_and_does_not_force_full_reindex()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _workspace.WriteFile("one.cs", "class One {}");

        FakeWorkspaceFileWatcherFactory watchers = new()
        {
            BeforeReturn = static () => throw new InvalidOperationException("The inotify watch budget is exhausted."),
        };

        ObservingScopeFactory scopes = new(BuildScopeFactory());

        WorkspaceIndexingService service = CreateService(new FakeWeaveService(), out _, watcherFactory: watchers, scopeFactory: scopes);

        // The ladder after the one immediate retry starts at half a minute, so however slow this run is,
        // exactly two attempts can fall inside the wait below; one per debounce cycle would be about five.
        service.RetryBackoffBaseDelayOverrideForTests = TimeSpan.FromSeconds(30);

        service.RegisterWorkspace(_workspace.Root);

        await service.StartAsync(CancellationToken.None);

        try
        {
            // The one scheduled reconciliation walks the workspace once and settles.
            await WaitForWorkspaceConditionAsync(() => scopes.ScopeCount == 1 && service.GetScheduledSweepSnapshot().Outstanding == 0);

            await Task.Delay(TimeSpan.FromSeconds(1.5));

            // The registration's attempt and its one immediate retry; the next is half a minute away.
            Assert.Equal(2, watchers.Created.Count);

            // No creation failure forced another reconciliation.
            Assert.Equal(1, scopes.ScopeCount);

            Assert.Equal(0, service.ActiveWatcherCount);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// A backed-off watcher is re-created when its deadline arrives even though nothing else wakes the
    /// scheduler: the loop otherwise sleeps until the next reconciliation, an hour away.
    /// </summary>
    [SkippableFact]
    public async Task Watcher_creation_is_retried_when_its_backoff_elapses_without_another_signal()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        int attempts = 0;

        FakeWorkspaceFileWatcherFactory watchers = new()
        {
            BeforeReturn = () =>
            {
                if (Interlocked.Increment(ref attempts) <= 2)
                {
                    throw new InvalidOperationException("The inotify watch budget is exhausted.");
                }
            },
        };

        WorkspaceIndexingService service = CreateService(new FakeWeaveService(), out _, watcherFactory: watchers);

        service.RetryBackoffBaseDelayOverrideForTests = TimeSpan.FromMilliseconds(700);

        service.RegisterWorkspace(_workspace.Root);

        await service.StartAsync(CancellationToken.None);

        try
        {
            await WaitForWorkspaceConditionAsync(() => service.ActiveWatcherCount == 1);

            Assert.Equal(3, watchers.Created.Count);

            Assert.True(service.GetRuntimeStatus(_workspace.Root).Watching);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// The watcher that ran and then failed may have lost events, so unlike a creation failure it still
    /// forces the reconciliation of what it covered.
    /// </summary>
    [SkippableFact]
    public async Task Runtime_watcher_error_still_forces_a_full_reconciliation()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _workspace.WriteFile("one.cs", "class One {}");

        FakeWorkspaceFileWatcherFactory watchers = new();

        RecordingGrimoireWorkAdmissionGate gate = new(new GrimoireConnectionAdmissionGate(TimeProvider.System));

        WorkspaceIndexingService service = CreateService(new FakeWeaveService(), out _, watcherFactory: watchers, workAdmission: gate);

        service.RegisterWorkspace(_workspace.Root);

        Assert.True(service.QueueIndexNow(_workspace.Root).IsSuccess);

        await DrainWorkspaceSchedulerAsync(service);

        Assert.Equal(1, gate.EffectGroupAttempts);

        watchers.Single.TriggerError(new IOException("Watcher lost events."));

        await ProcessPendingWatcherEventsAsync(service, _workspace.Root, CancellationToken.None);

        // The forced reconciliation re-reads the unchanged file, one effect group per visit.
        Assert.Equal(2, gate.EffectGroupAttempts);

        await service.DisposeAsync();
    }

    [SkippableFact]
    public async Task Dispatch_failure_returns_unavailable_and_settles_the_published_handle_without_admission()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        RecordingGrimoireWorkAdmissionGate gate = new(new GrimoireConnectionAdmissionGate(TimeProvider.System));

        WorkspaceIndexingService service = CreateService(new FakeWeaveService(), out _, workAdmission: gate);

        service.BeforeHandleDispatchForTests = static () => throw new TaskSchedulerException("Dispatch unavailable.");

        var result = service.QueueIndexNow(_workspace.Root);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Workspace.IndexingUnavailable, result.Error.Code);

        Assert.Equal(new WorkspaceIndexingService.WorkspaceSchedulerSnapshot(0, 0, false), service.GetSchedulerSnapshot());

        Assert.Equal([GrimoireWorkKind.WorkspaceIndexing], gate.RequestedWorkKinds);

        Assert.Equal(0, gate.EffectGroupAttempts);

        await service.StopAsync(CancellationToken.None);
    }

    [SkippableFact]
    public async Task Unexpected_watcher_factory_failure_retires_its_exact_reservation_and_can_retry()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        FakeWorkspaceFileWatcherFactory watchers = new() { BeforeReturn = static () => throw new InvalidOperationException("Factory failed.") };

        WorkspaceIndexingService service = CreateService(new FakeWeaveService(), out _, watcherFactory: watchers);

        Exception? failure = Record.Exception(() => service.RegisterWorkspace(_workspace.Root));

        Assert.Null(failure);

        Assert.Equal(0, service.ActiveWatcherCount);

        Assert.True(service.GetRuntimeStatus(_workspace.Root).Degraded);

        watchers.BeforeReturn = null;

        service.RegisterWorkspace(_workspace.Root);

        Assert.Equal(1, service.ActiveWatcherCount);

        await service.StopAsync(CancellationToken.None);
    }

    [SkippableFact]
    public async Task Stop_waits_for_inflight_watcher_registration_and_disposes_the_unpublished_instance()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        FakeWorkspaceFileWatcherFactory watchers = new()
        {
            BeforeReturn = () =>
            {
                entered.TrySetResult();

                release.Task.GetAwaiter().GetResult();
            },
        };

        WorkspaceIndexingService service = CreateService(new FakeWeaveService(), out _, watcherFactory: watchers);

        Task register = Task.Factory.StartNew(() => service.RegisterWorkspace(_workspace.Root), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Task stop = service.StopAsync(CancellationToken.None);

        try
        {
            Assert.False(stop.IsCompleted);
        }
        finally
        {
            release.TrySetResult();

            await Task.WhenAll(register, stop).WaitAsync(TimeSpan.FromSeconds(30));
        }

        Assert.True(watchers.Single.IsDisposed);

        Assert.Equal(0, service.ActiveWatcherCount);
    }

    [SkippableFact]
    public async Task Synchronous_registration_replacement_cannot_publish_or_mutate_the_retired_watcher()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        FakeWorkspaceFileWatcherFactory watchers = new();

        WorkspaceIndexingService service = CreateService(new FakeWeaveService(), out _, watcherFactory: watchers);

        watchers.BeforeReturn = () =>
        {
            watchers.BeforeReturn = null;

            service.UnregisterWorkspace(_workspace.Root);

            service.RegisterWorkspace(_workspace.Root);
        };

        service.RegisterWorkspace(_workspace.Root);

        Assert.True(watchers.Created[0].IsDisposed);

        Assert.False(watchers.Created[1].IsDisposed);

        watchers.Created[0].TriggerChanged(Path.Combine(_workspace.Root, "old.cs"));

        Assert.Null(service.GetRuntimeStatus(_workspace.Root).LastEventAt);

        Assert.Equal(1, service.ActiveWatcherCount);

        await service.StopAsync(CancellationToken.None);
    }

    [SkippableFact]
    public async Task Stop_and_maintenance_drain_join_async_scope_then_work_lease_disposal()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _workspace.WriteFile("one.cs", "class One {}");

        GrimoireConnectionAdmissionGate inner = new(TimeProvider.System);

        RecordingGrimoireWorkAdmissionGate gate = new(inner);

        AsyncDisposalScopeFactory scopes = new(BuildScopeFactory());

        TaskCompletionSource leaseEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource releaseLease = new(TaskCreationOptions.RunContinuationsAsynchronously);

        WorkspaceIndexingService service = CreateService(new FakeWeaveService(), out _, scopeFactory: scopes, workAdmission: gate);

        service.RegisterWorkspace(_workspace.Root);

        gate.BeforeWorkLeaseDisposalAsync = () =>
        {
            leaseEntered.TrySetResult();

            return new ValueTask(releaseLease.Task);
        };

        Assert.True(service.QueueIndexNow(_workspace.Root).IsSuccess);

        await scopes.DisposalEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        await using IGrimoireClosingOwner closing = BeginWorkspaceClosing(inner);

        Task<Result> drain = inner.DrainRequestAndWorkAsync(closing, CancellationToken.None).AsTask();

        Task stop = service.StopAsync(new CancellationToken(canceled: true));

        try
        {
            Assert.False(drain.IsCompleted);

            Assert.False(stop.IsCompleted);

            Assert.False(leaseEntered.Task.IsCompleted);

            scopes.Release.TrySetResult();

            await leaseEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.False(drain.IsCompleted);

            Assert.False(stop.IsCompleted);
        }
        finally
        {
            scopes.Release.TrySetResult();

            releaseLease.TrySetResult();

            await stop.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.True((await drain.WaitAsync(TimeSpan.FromSeconds(30))).IsSuccess);
        }

        Assert.Equal(new WorkspaceIndexingService.WorkspaceSchedulerSnapshot(0, 0, false), service.GetSchedulerSnapshot());
    }

    [SkippableFact]
    public async Task Queued_identity_has_no_execution_task_token_source_scope_or_waiter()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        GrimoireConnectionAdmissionGate inner = new(TimeProvider.System);

        RecordingGrimoireWorkAdmissionGate gate = new(inner);

        await using IGrimoireClosingOwner closing = BeginWorkspaceClosing(inner);

        WorkspaceIndexingService service = CreateService(new FakeWeaveService(), out _, workAdmission: gate);

        for (int index = 0; index < 3; index++)
        {
            string path = _workspace.CreateSubdir($"queued-{index}");

            Assert.True(service.QueueIndexNow(path).IsSuccess);
        }

        await WaitForWorkspaceConditionAsync(() => gate.ActiveGenerationWaiters == 2);

        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        object overflow = typeof(WorkspaceIndexingService).GetField("_overflow", fields)!.GetValue(service)!;

        object entry = overflow.GetType().GetProperty("Head", fields)!.GetValue(overflow)!;

        Assert.Null(entry.GetType().GetProperty("Handle", fields)!.GetValue(entry));

        object demand = entry.GetType().GetProperty("Pending", fields)!.GetValue(entry)!;

        foreach (object data in new[] { entry, demand })
        {
            Assert.DoesNotContain(data.GetType().GetFields(fields), static field =>
                typeof(Task).IsAssignableFrom(field.FieldType)
                || typeof(CancellationTokenSource).IsAssignableFrom(field.FieldType)
                || typeof(TaskCompletionSource).IsAssignableFrom(field.FieldType)
                || typeof(IServiceScope).IsAssignableFrom(field.FieldType)
                || field.FieldType == typeof(CancellationToken));
        }

        await service.StopAsync(CancellationToken.None);

        Assert.Equal(0, gate.ActiveGenerationWaiters);
    }

    [SkippableFact]
    public void Path_resolver_keeps_non_Windows_case_comparison_ordinal_and_unregistered_fallback_unchanged()
    {
        Skip.If(OperatingSystem.IsWindows(), "Non-Windows ordinal path policy.");

        WorkspaceIndexingService service = CreateService(new FakeWeaveService(), out _);

        service.RegisterWorkspace(_workspace.Root);

        string otherCase = _workspace.Root.ToUpperInvariant();

        Assert.NotEqual(_workspace.Root, otherCase);

        Assert.Equal(otherCase, service.ResolveIndexedWorkspacePath(otherCase));

        Assert.Equal(_workspace.Root, service.ResolveIndexedWorkspacePath(_workspace.Root + Path.DirectorySeparatorChar));

        string unregistered = Path.Combine(_workspace.Root, "unregistered") + Path.DirectorySeparatorChar;

        Assert.Equal(Path.GetFullPath(unregistered), service.ResolveIndexedWorkspacePath(unregistered));
    }

    private sealed class AsyncDisposalScopeFactory(IServiceScopeFactory inner) : IServiceScopeFactory
    {
        internal TaskCompletionSource DisposalEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IServiceScope CreateScope() => new AsyncDisposalScope(inner.CreateScope(), this);

        private sealed class AsyncDisposalScope(IServiceScope innerScope, AsyncDisposalScopeFactory owner) : IServiceScope, IAsyncDisposable
        {
            public IServiceProvider ServiceProvider => innerScope.ServiceProvider;

            public void Dispose() => throw new InvalidOperationException("Workspace scopes must be asynchronously disposed.");

            public async ValueTask DisposeAsync()
            {
                owner.DisposalEntered.TrySetResult();

                await owner.Release.Task;

                if (innerScope is IAsyncDisposable asynchronous)
                {
                    await asynchronous.DisposeAsync();
                }
                else
                {
                    innerScope.Dispose();
                }
            }
        }
    }
}
