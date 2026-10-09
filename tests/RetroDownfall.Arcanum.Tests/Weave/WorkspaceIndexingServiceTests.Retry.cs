using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Weave;

/// <summary>
/// The retry ladders of the scheduler: a failed unit outside a sweep, the re-creation of a watcher, and the
/// moments a deadline can be missed. Every deadline here is stretched or shortened through
/// <c>RetryBackoffBaseDelayOverrideForTests</c>, so each assertion rests on an ordering the test controls
/// and not on how long a wall-clock wait happened to take.
/// </summary>
public sealed partial class WorkspaceIndexingServiceTests
{
    /// <summary>
    /// A full unit that fails outside a scheduled sweep (a manual request, or the follow-up of a budget-stopped
    /// one) belongs to no sweep, so nothing recorded its failure and nothing retried it: the files it did not
    /// reach waited for the next reconciliation interval, an hour at the default cadence.
    /// </summary>
    [SkippableFact]
    public async Task A_full_unit_that_fails_outside_a_sweep_is_retried_on_the_backoff_ladder()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        FakeWeaveService weave = new();

        WorkspaceIndexingService service = CreateService(weave, out _);

        service.RetryBackoffBaseDelayOverrideForTests = TimeSpan.FromMilliseconds(50);

        service.MaxFilesToIndexOverrideForTests = 1;

        service.RegisterWorkspace(_workspace.Root);

        await service.StartAsync(CancellationToken.None);

        try
        {
            // The scheduled sweep over the still-empty workspace settles first, so what follows is not part of it.
            await WaitForWorkspaceConditionAsync(
                () => service.GetScheduledSweepSnapshot() is { Outstanding: 0 } snapshot && snapshot.NextReconciliation > DateTimeOffset.UtcNow.AddMinutes(30));

            _workspace.WriteFile("one.txt", "one");

            _workspace.WriteFile("two.txt", "two");

            _workspace.WriteFile("three.txt", "three");

            weave.FailForContentContaining = "two";

            Assert.True(service.QueueIndexNow(_workspace.Root).IsSuccess);

            await DrainWorkspaceSchedulerAsync(service);

            // The provider refused one file: the others were indexed and that one was not.
            Assert.DoesNotContain("two.txt", await GetIndexedRelativePathsAsync());

            weave.FailForContentContaining = null;

            Assert.True(
                await BecomesTrueAsync(
                    async () => (await GetIndexedRelativePathsAsync()).Contains("two.txt"),
                    TimeSpan.FromSeconds(10)),
                "The failed unit was not retried before the next reconciliation interval.");

            Assert.Equal(
                ["one.txt", "three.txt", "two.txt"],
                (await GetIndexedRelativePathsAsync()).OrderBy(static path => path, StringComparer.Ordinal).ToArray());
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// A watcher that is finally created after a failure gap may have missed what changed while it was down, and
    /// the next scheduled reconciliation can be an hour away, so its creation asks for one. It asks for a plain
    /// one: a creation failure delivered no events, so nothing is forced.
    /// </summary>
    [SkippableFact]
    public async Task A_watcher_created_after_a_creation_failure_requests_a_reconciliation_that_forces_nothing()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _workspace.WriteFile("one.cs", "class One {}");

        int attempts = 0;

        FakeWorkspaceFileWatcherFactory watchers = new()
        {
            BeforeReturn = () =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    throw new InvalidOperationException("The inotify watch budget is exhausted.");
                }
            },
        };

        RecordingGrimoireWorkAdmissionGate gate = new(new GrimoireConnectionAdmissionGate(TimeProvider.System));

        ObservingScopeFactory scopes = new(BuildScopeFactory());

        WorkspaceIndexingService service = CreateService(new FakeWeaveService(), out EmbeddingSettings embeddings, watcherFactory: watchers, scopeFactory: scopes, workAdmission: gate);

        Assert.True(await service.IndexWorkspaceAsync(_workspace.Root, embeddings, CancellationToken.None));

        int scopesBefore = scopes.ScopeCount;

        int effectGroupsBefore = gate.EffectGroupAttempts;

        // The first registration meets the failing factory; the second finds its one immediate retry due.
        service.RegisterWorkspace(_workspace.Root);

        Assert.Equal(0, service.ActiveWatcherCount);

        service.RegisterWorkspace(_workspace.Root);

        Assert.Equal(1, service.ActiveWatcherCount);

        await DrainWorkspaceSchedulerAsync(service);

        // One reconciliation ran, and it re-read nothing: a forced one would have visited the unchanged file.
        Assert.Equal(scopesBefore + 1, scopes.ScopeCount);

        Assert.Equal(effectGroupsBefore, gate.EffectGroupAttempts);

        await service.DisposeAsync();
    }

    /// <summary>
    /// A watcher that errors right after it was created has not shown that it is healthy, so an event it
    /// delivered in between does not wipe the failure ladder: it would otherwise be re-created at once, forever,
    /// forcing a reconciliation each time.
    /// </summary>
    [SkippableFact]
    public async Task A_watcher_that_fails_right_after_each_event_keeps_climbing_the_ladder()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string file = _workspace.WriteFile("one.cs", "class One {}");

        HeldWatcherCreation replacement = new();

        int attempts = 0;

        FakeWorkspaceFileWatcherFactory watchers = new()
        {
            BeforeReturn = () =>
            {
                if (Interlocked.Increment(ref attempts) == 2)
                {
                    replacement.Hold();
                }
            },
        };

        WorkspaceIndexingService service = CreateService(new FakeWeaveService(), out _, watcherFactory: watchers);

        service.RetryBackoffBaseDelayOverrideForTests = TimeSpan.FromHours(1);

        service.WatcherStablePeriodOverrideForTests = TimeSpan.FromHours(1);

        service.RegisterWorkspace(_workspace.Root);

        await service.StartAsync(CancellationToken.None);

        try
        {
            watchers.Created[0].TriggerChanged(file);

            watchers.Created[0].TriggerError(new IOException("The first watcher failed."));

            // The first failure is retried at once.
            await replacement.WaitUntilEnteredAsync();

            Task ready = WaitForWorkspaceConditionAsync(() => watchers.Created.Count == 2 && service.GetRuntimeStatus(_workspace.Root).Watching);

            Assert.False(ready.IsCompleted, "the unpublished replacement completed readiness");

            replacement.Release();

            await ready.WaitAsync(TimeSpan.FromSeconds(30));

            watchers.Created[1].TriggerChanged(file);

            watchers.Created[1].TriggerError(new IOException("The replacement failed straight away."));

            // The second failure backs off by an hour, so no third watcher appears.
            await Task.Delay(TimeSpan.FromMilliseconds(700));

            Assert.Equal(2, watchers.Created.Count);

            Assert.Equal(0, service.ActiveWatcherCount);
        }
        finally
        {
            replacement.Release();

            await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    /// <summary>
    /// The other half of the rule: a watcher that has run for the stable period and then delivers an event is
    /// healthy, so its next failure is retried at once like a first one.
    /// </summary>
    [SkippableFact]
    public async Task A_watcher_that_ran_stably_starts_the_ladder_over_on_its_next_event()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string file = _workspace.WriteFile("one.cs", "class One {}");

        HeldWatcherCreation second = new();

        HeldWatcherCreation third = new();

        int attempts = 0;

        FakeWorkspaceFileWatcherFactory watchers = new()
        {
            BeforeReturn = () =>
            {
                int attempt = Interlocked.Increment(ref attempts);

                if (attempt == 2)
                {
                    second.Hold();
                }
                else if (attempt == 3)
                {
                    third.Hold();
                }
            },
        };

        WorkspaceIndexingService service = CreateService(new FakeWeaveService(), out _, watcherFactory: watchers);

        service.RetryBackoffBaseDelayOverrideForTests = TimeSpan.FromHours(1);

        service.WatcherStablePeriodOverrideForTests = TimeSpan.Zero;

        service.RegisterWorkspace(_workspace.Root);

        await service.StartAsync(CancellationToken.None);

        try
        {
            watchers.Created[0].TriggerChanged(file);

            watchers.Created[0].TriggerError(new IOException("The first watcher failed."));

            await second.WaitUntilEnteredAsync();

            Task secondReady = WaitForWorkspaceConditionAsync(() => watchers.Created.Count == 2 && service.GetRuntimeStatus(_workspace.Root).Watching);

            Assert.False(secondReady.IsCompleted, "the unpublished second watcher completed readiness");

            second.Release();

            await secondReady.WaitAsync(TimeSpan.FromSeconds(30));

            watchers.Created[1].TriggerChanged(file);

            watchers.Created[1].TriggerError(new IOException("The second watcher failed after a healthy event."));

            // Healthy in between, so this is a first failure again: retried at once, not after an hour.
            await third.WaitUntilEnteredAsync();

            Task thirdReady = WaitForWorkspaceConditionAsync(() => watchers.Created.Count == 3 && service.GetRuntimeStatus(_workspace.Root).Watching);

            Assert.False(thirdReady.IsCompleted, "the unpublished third watcher completed readiness");

            third.Release();

            await thirdReady.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(1, service.ActiveWatcherCount);
        }
        finally
        {
            second.Release();

            third.Release();

            await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    /// <summary>
    /// A watcher retry that comes due while the scheduler is between checking watchers and computing how long to
    /// sleep must still be a deadline: left out as "already past", the loop sleeps until the next reconciliation,
    /// an hour away, and the watcher is not re-created until something else happens to wake it.
    /// </summary>
    [SkippableFact]
    public async Task A_watcher_retry_that_comes_due_during_a_scheduler_pass_is_not_slept_through()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        int attempts = 0;

        HeldWatcherCreation immediateRetry = new();

        FakeWorkspaceFileWatcherFactory watchers = new()
        {
            BeforeReturn = () =>
            {
                int attempt = Interlocked.Increment(ref attempts);

                if (attempt == 2)
                {
                    immediateRetry.Hold();
                }

                if (attempt <= 2)
                {
                    throw new InvalidOperationException("The inotify watch budget is exhausted.");
                }
            },
        };

        WorkspaceIndexingService service = CreateService(new FakeWeaveService(), out _, watcherFactory: watchers);

        service.RetryBackoffBaseDelayOverrideForTests = TimeSpan.FromMilliseconds(200);

        int passes = 0;

        SemaphoreSlim signal = (SemaphoreSlim)typeof(WorkspaceIndexingService)
            .GetField("_watcherSignal", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(service)!;

        // The first pass finds the retry still in the future; this holds it past the deadline before the
        // loop works out how long to sleep. The wake-up the settled sweep leaves behind is consumed too,
        // because it would otherwise end the sleep early and hide the deadline that was missed.
        service.AfterWatcherPassForTests = () =>
        {
            if (Interlocked.Increment(ref passes) == 1)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(500));

                while (signal.Wait(0))
                {
                }
            }
        };

        service.RegisterWorkspace(_workspace.Root);

        await service.StartAsync(CancellationToken.None);

        try
        {
            await immediateRetry.WaitUntilEnteredAsync();

            Task<bool> ready = BecomesTrueAsync(() => Task.FromResult(service.GetRuntimeStatus(_workspace.Root).Watching), TimeSpan.FromSeconds(10));

            Assert.False(ready.IsCompleted, "the doomed immediate retry completed readiness");

            immediateRetry.Release();

            Assert.True(
                await ready.WaitAsync(TimeSpan.FromSeconds(30)),
                "The watcher was not re-created: the loop slept through its retry deadline.");

            Assert.Equal(3, watchers.Created.Count);

            Assert.Equal(1, service.ActiveWatcherCount);

            Assert.True(service.GetRuntimeStatus(_workspace.Root).Watching);
        }
        finally
        {
            immediateRetry.Release();

            await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    /// <summary>
    /// A retry that finds Grimoire admission closed (maintenance owns it) is not a deadline that passed: it is
    /// owed, and attempted again shortly, so the watcher comes back after the window without another event.
    /// </summary>
    [SkippableFact]
    public async Task A_watcher_retry_refused_by_maintenance_is_attempted_again_after_it_reopens()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        int attempts = 0;

        int holdReopenedRetry = 0;

        HeldWatcherCreation reopenedRetry = new();

        FakeWorkspaceFileWatcherFactory watchers = new()
        {
            BeforeReturn = () =>
            {
                int attempt = Interlocked.Increment(ref attempts);

                if (attempt <= 2)
                {
                    throw new InvalidOperationException("The inotify watch budget is exhausted.");
                }

                if (attempt == 3 && Volatile.Read(ref holdReopenedRetry) == 1)
                {
                    reopenedRetry.Hold();
                }
            },
        };

        GrimoireConnectionAdmissionGate inner = new(TimeProvider.System);

        WorkspaceIndexingService service = CreateService(new FakeWeaveService(), out _, watcherFactory: watchers, workAdmission: inner);

        service.RetryBackoffBaseDelayOverrideForTests = TimeSpan.FromMilliseconds(500);

        service.RegisterWorkspace(_workspace.Root);

        await service.StartAsync(CancellationToken.None);

        try
        {
            // The immediate retry failed too, so the third attempt is a deadline half a second away.
            await WaitForWorkspaceConditionAsync(() => Volatile.Read(ref attempts) == 2);

            await using IGrimoireClosingOwner closing = BeginWorkspaceClosing(inner);

            await using IGrimoireExclusiveClosedLease closed = await CloseWorkspaceGateAsync(inner, closing);

            // Long enough for the deadline to arrive, and be refused, more than once.
            await Task.Delay(TimeSpan.FromMilliseconds(1_400));

            Assert.Equal(0, service.ActiveWatcherCount);

            Volatile.Write(ref holdReopenedRetry, 1);

            Assert.True((await closed.CompleteAsync(CovenantExclusiveLeaseDisposition.RollbackAndReopen, CancellationToken.None)).IsSuccess);

            await reopenedRetry.WaitUntilEnteredAsync();

            Task<bool> ready = BecomesTrueAsync(() => Task.FromResult(service.GetRuntimeStatus(_workspace.Root).Watching), TimeSpan.FromSeconds(10));

            Assert.False(ready.IsCompleted, "the unpublished post-maintenance watcher completed readiness");

            reopenedRetry.Release();

            Assert.True(
                await ready.WaitAsync(TimeSpan.FromSeconds(30)),
                "The watcher was not re-created after maintenance reopened Grimoire admission.");

            Assert.Equal(3, watchers.Created.Count);

            Assert.Equal(1, service.ActiveWatcherCount);

            Assert.True(service.GetRuntimeStatus(_workspace.Root).Watching);
        }
        finally
        {
            reopenedRetry.Release();

            await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    private static async Task<bool> BecomesTrueAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        using CancellationTokenSource deadline = new(timeout);

        try
        {
            while (!await condition())
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), deadline.Token);
            }

            return true;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>Holds a factory callback before its watcher can be published to runtime status.</summary>
    private sealed class HeldWatcherCreation
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task WaitUntilEnteredAsync() => _entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        internal void Hold()
        {
            _ = _entered.TrySetResult();

            _release.Task.GetAwaiter().GetResult();
        }

        internal void Release()
        {
            _ = _release.TrySetResult();
        }
    }
}
