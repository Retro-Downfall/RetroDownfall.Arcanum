using System.Collections.Concurrent;
using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Tests.Hosting;

public sealed class UnseenServantServiceTests
{
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
}
