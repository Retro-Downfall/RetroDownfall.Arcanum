using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildSpendRefreshTrackerCleanupScopeTests
{
    [Fact]
    public async Task Overlapping_root_scopes_join_attempt_and_reopen_only_after_both_dispose()
    {
        GuildSpendRefreshTracker tracker = new();

        Guid root = Guid.NewGuid();

        Guid unrelated = Guid.NewGuid();

        Assert.True(tracker.TryEnter(root, out var entry));

        Assert.NotNull(entry);

        Task<IDisposable> first = tracker.CloseRootsScopedAsync([root]);

        Task<IDisposable> second = tracker.CloseRootsScopedAsync([root]);

        Assert.True(entry.Stop.IsCancellationRequested);

        Assert.False(first.IsCompleted);

        Assert.False(second.IsCompleted);

        Assert.False(tracker.TryEnter(root, out _));

        Assert.True(tracker.TryEnter(unrelated, out var other));

        other!.Dispose();

        entry.Dispose();

        IDisposable firstScope = await first.WaitAsync(TimeSpan.FromSeconds(5));

        IDisposable secondScope = await second.WaitAsync(TimeSpan.FromSeconds(5));

        firstScope.Dispose();

        Assert.False(tracker.TryEnter(root, out _));

        secondScope.Dispose();

        Assert.True(tracker.TryEnter(root, out var reopened));

        reopened!.Dispose();
    }

    [Fact]
    public async Task Callback_failure_still_joins_attempt_and_releases_scope()
    {
        GuildSpendRefreshTracker tracker = new();

        Guid root = Guid.NewGuid();

        Assert.True(tracker.TryEnter(root, out var entry));

        Assert.NotNull(entry);

        using CancellationTokenRegistration callback = entry.Stop.Register(() => throw new InvalidOperationException("callback failed"));

        Task<IDisposable> closing = tracker.CloseRootsScopedAsync([root]);

        Assert.True(entry.Stop.IsCancellationRequested);

        Assert.False(closing.IsCompleted);

        Assert.False(tracker.TryEnter(root, out _));

        entry.Dispose();

        await Assert.ThrowsAsync<AggregateException>(async () => await closing.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.True(tracker.TryEnter(root, out var reopened));

        reopened!.Dispose();
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Permanent_root_or_host_close_joins_even_if_callback_throws(bool host)
    {
        GuildSpendRefreshTracker tracker = new();

        Guid root = Guid.NewGuid();

        Assert.True(tracker.TryEnter(root, out var entry));

        Assert.NotNull(entry);

        using GuildSpendRefreshTracker.Entry held = entry;

        using CancellationTokenRegistration callback = entry.Stop.Register(() => throw new InvalidOperationException("callback failed"));

        Task closing = host ? tracker.CloseAllAsync(default) : tracker.CloseRootAsync(root, default);

        Assert.True(entry.Stop.IsCancellationRequested);

        await Task.Yield();

        Assert.False(closing.IsCompleted);

        entry.Dispose();

        await Assert.ThrowsAsync<AggregateException>(async () => await closing.WaitAsync(TimeSpan.FromSeconds(5)));
    }

}
