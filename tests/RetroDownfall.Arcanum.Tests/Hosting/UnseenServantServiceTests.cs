using System.Collections.Concurrent;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Tests.Hosting;

public sealed class UnseenServantServiceTests
{
    [Fact]
    public async Task TrackJobTask_binds_the_published_handle_before_dispatch_returns()
    {
        ConcurrentExclusiveSchedulerPair scheduler = new(
            TaskScheduler.Default,
            maxConcurrencyLevel: 1);

        Task probe = Task.Factory.StartNew(
            () =>
            {
                ConcurrentDictionary<Guid, Task> activeJobTasks = new();

                Guid taskId = Guid.NewGuid();

                Task? published = null;

                Task jobTask = UnseenServantService.TrackJobTask(
                    activeJobTasks,
                    taskId,
                    () =>
                    {
                        published = activeJobTasks[taskId];

                        return Task.CompletedTask;
                    });

                Assert.True(jobTask.IsCompletedSuccessfully);

                Assert.NotNull(published);

                Assert.True(published.IsCompletedSuccessfully);
            },
            CancellationToken.None,
            TaskCreationOptions.None,
            scheduler.ExclusiveScheduler);

        try
        {
            await probe.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            scheduler.Complete();

            await scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public void TrackJobTask_does_not_resurrect_an_entry_the_job_body_already_removed()
    {
        ConcurrentDictionary<Guid, Task> activeJobTasks = new();

        Guid taskId = Guid.NewGuid();

        _ = UnseenServantService.TrackJobTask(
            activeJobTasks,
            taskId,
            () =>
            {
                _ = activeJobTasks.TryRemove(taskId, out _);

                return Task.CompletedTask;
            });

        Assert.False(activeJobTasks.ContainsKey(taskId));
    }

    [Fact]
    public async Task TrackJobTask_registers_an_incomplete_handle_before_the_job_body_runs()
    {
        ConcurrentDictionary<Guid, Task> activeJobTasks = new();

        Guid taskId = Guid.NewGuid();

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task? observedAtDispatch = null;

        Task jobTask = UnseenServantService.TrackJobTask(
            activeJobTasks,
            taskId,
            () =>
            {
                _ = activeJobTasks.TryGetValue(taskId, out observedAtDispatch);

                return release.Task;
            });

        Assert.NotNull(observedAtDispatch);

        Assert.False(observedAtDispatch.IsCompleted);

        release.SetResult();

        await jobTask;

        await observedAtDispatch;
    }

    [Fact]
    public async Task TrackJobTask_completes_and_removes_the_published_handle_when_start_throws()
    {
        ConcurrentDictionary<Guid, Task> activeJobTasks = new();

        Guid taskId = Guid.NewGuid();

        Task? published = null;

        InvalidOperationException expected = new("dispatch failed");

        InvalidOperationException actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => UnseenServantService.TrackJobTask(
                activeJobTasks,
                taskId,
                () =>
                {
                    published = activeJobTasks[taskId];

                    throw expected;
                }));

        Assert.Same(expected, actual);

        Assert.NotNull(published);

        await published.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(published.IsCompleted);

        Assert.False(activeJobTasks.ContainsKey(taskId));
    }

    [Fact]
    public async Task TrackJobTask_does_not_remove_a_same_key_replacement_when_start_throws()
    {
        ConcurrentDictionary<Guid, Task> activeJobTasks = new();

        Guid taskId = Guid.NewGuid();

        Task replacement = Task.CompletedTask;

        Task? published = null;

        InvalidOperationException expected = new("dispatch failed");

        InvalidOperationException actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => UnseenServantService.TrackJobTask(
                activeJobTasks,
                taskId,
                () =>
                {
                    published = activeJobTasks[taskId];

                    activeJobTasks[taskId] = replacement;

                    throw expected;
                }));

        Assert.Same(expected, actual);

        Assert.NotNull(published);

        await published.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(published.IsCompleted);

        Assert.Same(replacement, activeJobTasks[taskId]);
    }

    /// <summary>
    /// The save outlives shutdown on purpose (R-182), so it must be bounded: a store that never answers cannot hold
    /// host shutdown, and the job's own result stands without the watermark.
    /// </summary>
    [Fact]
    public async Task A_watermark_save_that_never_finishes_is_abandoned_at_its_bound_and_logged()
    {
        await using UnseenServantAdmissionHarness harness = new();

        _ = await harness.ConfigureDueJobAsync();

        harness.Service.WatermarkSaveTimeout = TimeSpan.FromMilliseconds(100);

        harness.OnStep = async (step, token) =>
        {
            if (step == "watermark")
            {
                // A store that never answers; only the bound's cancellation ends this.
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
        };

        harness.Dispatch(CancellationToken.None);

        await Task.WhenAll(harness.ActiveTasks).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Empty(harness.Store.Rows);

        Assert.Contains(
            harness.Logger.Entries,
            static entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Warning
                && entry.Message.Contains("Failed to persist Unseen Servant watermark", StringComparison.Ordinal));
    }

    [Fact]
    public void The_production_watermark_save_bound_is_five_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), UnseenServantService.DefaultWatermarkSaveTimeout);
    }

    [Fact]
    public async Task Watermark_is_saved_when_shutdown_is_requested_immediately_after_the_job_completes()
    {
        await using UnseenServantAdmissionHarness harness = new();

        UnseenServantJob job = await harness.ConfigureDueJobAsync();

        using CancellationTokenSource shutdown = new();

        harness.OnStep = (step, token) =>
        {
            if (step.StartsWith("runner:", StringComparison.Ordinal))
            {
                // The job body has done its external work; the host stops one instruction later.
                shutdown.Cancel();
            }

            if (step == "watermark")
            {
                // A real store observes the token it is given.
                token.ThrowIfCancellationRequested();
            }

            return Task.CompletedTask;
        };

        harness.Dispatch(shutdown.Token);

        await Task.WhenAll(harness.ActiveTasks).WaitAsync(TimeSpan.FromSeconds(10));

        UnseenServantWatermark saved = Assert.Single(harness.Store.Rows);

        Assert.Equal(UnseenServantJobTracker.JobTrackingKey(job), saved.JobKey);

        Assert.Equal(harness.Clock.GetUtcNow(), saved.LastRunAt);
    }
}
