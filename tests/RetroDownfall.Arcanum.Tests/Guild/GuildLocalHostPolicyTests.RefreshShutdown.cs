using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public async Task Real_host_readiness_stop_joins_refresh_tracker_even_when_stop_is_cancelled()
    {
        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using Fixture f = await Fixture.CreateAsync();

        GuildSpendRefreshTracker tracker = new();

        string path = Path.Combine(f.WorkspaceRoot, "host.lock");

        await using GuildLocalHostReadiness readiness = new(f.Settings, f.Connections, f.Policy, f.Authority,
            TimeProvider.System, path, f.Gate, tracker);

        Assert.True((await readiness.EnsureReadyAsync(default)).IsSuccess);

        Assert.True(tracker.TryEnter(Guid.NewGuid(), out GuildSpendRefreshTracker.Entry? entry));

        Assert.NotNull(entry);

        Task stop = readiness.StopAsync(new CancellationToken(canceled: true));

        Assert.False(stop.IsCompleted);

        Task secondStop = readiness.DisposeAsync().AsTask();

        Assert.False(secondStop.IsCompleted);

        Assert.True(GuildWorkspaceLeaseHostLock.TryAcquire(path).IsFailure);

        entry.Dispose();

        await Task.WhenAll(stop, secondStop);

        Assert.False(tracker.TryEnter(Guid.NewGuid(), out _));

        var successor = GuildWorkspaceLeaseHostLock.TryAcquire(path);

        Assert.True(successor.IsSuccess);

        successor.Value.Dispose();
    }
}
