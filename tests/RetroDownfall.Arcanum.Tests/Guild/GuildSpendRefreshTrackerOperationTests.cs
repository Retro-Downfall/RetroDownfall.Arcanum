using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildSpendRefreshTrackerOperationTests
{
    [Fact]
    public async Task Cancel_operation_signals_only_bound_attempt_and_allows_callback_reentry()
    {
        GuildSpendRefreshTracker tracker = new();

        Guid root = Guid.NewGuid();

        Guid operation = Guid.NewGuid();

        Assert.True(tracker.TryEnter(root, out var selected));

        Assert.True(tracker.TryEnter(root, out var other));

        Assert.NotNull(selected);

        Assert.NotNull(other);

        using (selected)
        using (other)
        {
            selected.BindOperation(operation);

            other.BindOperation(Guid.NewGuid());

            using CancellationTokenRegistration callback = selected.Stop.Register(() =>
            {
                Task reentry = Task.Run(() =>
                {
                    Assert.True(tracker.TryEnter(Guid.NewGuid(), out var nested));

                    nested?.Dispose();
                });

                Assert.True(reentry.Wait(TimeSpan.FromSeconds(2)));
            });

            await tracker.CancelOperationAsync(operation).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(selected.Stop.IsCancellationRequested);

            Assert.False(other.Stop.IsCancellationRequested);
        }
    }
    [Fact]
    public async Task Binding_is_idempotent_but_cannot_change_identity_or_bypass_root_close()
    {
        GuildSpendRefreshTracker tracker = new();

        Guid root = Guid.NewGuid();

        Guid operation = Guid.NewGuid();

        Assert.True(tracker.TryEnter(root, out var entry));

        Assert.NotNull(entry);

        entry.BindOperation(operation);

        entry.BindOperation(operation);

        Assert.Throws<InvalidOperationException>(() => entry.BindOperation(Guid.NewGuid()));

        Assert.Throws<ArgumentOutOfRangeException>(() => entry.BindOperation(Guid.Empty));

        Task closing = tracker.CloseRootAsync(root, default);

        Assert.True(entry.Stop.IsCancellationRequested);

        Assert.False(closing.IsCompleted);

        entry.Dispose();

        await closing.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(tracker.TryEnter(root, out _));
    }

    [Fact]
    public async Task Operation_cancellation_does_not_close_other_roots_or_replace_host_join()
    {
        GuildSpendRefreshTracker tracker = new();

        Guid operation = Guid.NewGuid();

        Assert.True(tracker.TryEnter(Guid.NewGuid(), out var selected));

        Assert.True(tracker.TryEnter(Guid.NewGuid(), out var other));

        Assert.NotNull(selected);

        Assert.NotNull(other);

        selected.BindOperation(operation);

        other.BindOperation(Guid.NewGuid());

        await tracker.CancelOperationAsync(operation);

        Assert.True(selected.Stop.IsCancellationRequested);

        Assert.False(other.Stop.IsCancellationRequested);

        Task closing = tracker.CloseAllAsync(default);

        Assert.True(other.Stop.IsCancellationRequested);

        Assert.False(closing.IsCompleted);

        selected.Dispose();

        other.Dispose();

        await closing.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(tracker.TryEnter(Guid.NewGuid(), out _));
    }

}
