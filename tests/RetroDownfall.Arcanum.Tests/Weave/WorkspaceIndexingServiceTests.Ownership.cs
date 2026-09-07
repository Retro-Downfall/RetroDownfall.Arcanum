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

        Assert.Empty(gate.RequestedWorkKinds);

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

        gate.BeforeWorkLeaseDisposalAsync = () =>
        {
            leaseEntered.TrySetResult();

            return new ValueTask(releaseLease.Task);
        };

        WorkspaceIndexingService service = CreateService(new FakeWeaveService(), out _, scopeFactory: scopes, workAdmission: gate);

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
