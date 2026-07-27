using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed class WorkspaceCheckCapabilityReporterTests
{
    [Fact]
    public async Task Capability_probe_is_async_and_reused_while_fresh()
    {
        int calls = 0;
        WorkspaceCheckCapabilityReporter reporter = CreateReporter(
            generation: () => "generation-a",
            probe: (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(
                    new WorkspaceCheckCapabilityStatus(
                        true,
                        false,
                        "available"));
            });

        WorkspaceCheckCapabilityStatus initial =
            reporter.GetStatus("/workspace");
        WorkspaceCheckCapabilityStatus refreshed =
            await reporter.GetStatusAsync(
                "/workspace",
                CancellationToken.None);
        WorkspaceCheckCapabilityStatus cached =
            await reporter.GetStatusAsync(
                "/workspace",
                CancellationToken.None);

        Assert.False(initial.IsAvailable);
        Assert.Contains(
            "refresh",
            initial.Reason,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(refreshed.IsAvailable, refreshed.Reason);
        Assert.True(cached.IsAvailable, cached.Reason);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Executable_or_settings_generation_change_fails_closed_until_refreshed()
    {
        string generation = "generation-a";
        int calls = 0;
        WorkspaceCheckCapabilityReporter reporter = CreateReporter(
            generation: () => generation,
            probe: (_, _) =>
            {
                int call = Interlocked.Increment(ref calls);
                return Task.FromResult(
                    new WorkspaceCheckCapabilityStatus(
                        true,
                        false,
                        $"available-{call}"));
            });

        WorkspaceCheckCapabilityStatus first =
            await reporter.GetStatusAsync(
                "/workspace",
                CancellationToken.None);
        generation = "generation-b";

        WorkspaceCheckCapabilityStatus stale =
            reporter.GetStatus("/workspace");
        WorkspaceCheckCapabilityStatus second =
            await reporter.GetStatusAsync(
                "/workspace",
                CancellationToken.None);

        Assert.True(first.IsAvailable, first.Reason);
        Assert.False(stale.IsAvailable);
        Assert.Contains(
            "generation",
            stale.Reason,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(second.IsAvailable, second.Reason);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Slow_probe_returns_bounded_refreshing_status()
    {
        TaskCompletionSource<WorkspaceCheckCapabilityStatus> pending =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        WorkspaceCheckCapabilityReporter reporter = CreateReporter(
            generation: () => "generation-a",
            probe: (_, _) => pending.Task,
            asyncWait: TimeSpan.FromMilliseconds(20),
            probeTimeout: TimeSpan.FromMilliseconds(50));

        WorkspaceCheckCapabilityStatus status =
            await reporter.GetStatusAsync(
                "/workspace",
                CancellationToken.None);

        Assert.False(status.IsAvailable);
        Assert.True(status.IsHealthDegraded);
        Assert.Contains(
            "refresh",
            status.Reason,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Generation_capture_failure_returns_degraded_status_instead_of_throwing()
    {
        WorkspaceCheckCapabilityReporter reporter = CreateReporter(
            generation: () => throw new IOException("identity unavailable"),
            probe: (_, _) => throw new InvalidOperationException(
                "A failed generation capture must not start a probe."));

        WorkspaceCheckCapabilityStatus status =
            await reporter.GetStatusAsync(
                "/workspace",
                CancellationToken.None);

        Assert.False(status.IsAvailable);
        Assert.True(status.IsHealthDegraded);
        Assert.Contains(
            "generation",
            status.Reason,
            StringComparison.OrdinalIgnoreCase);
    }

    private static WorkspaceCheckCapabilityReporter CreateReporter(
        Func<string> generation,
        Func<string?, CancellationToken, Task<WorkspaceCheckCapabilityStatus>>
            probe,
        TimeSpan? asyncWait = null,
        TimeSpan? probeTimeout = null) =>
        new(
            new TestOptionsMonitor<ArcanumSettings>(
                new ArcanumSettings()),
            generationProvider: _ => generation(),
            probe,
            TimeProvider.System,
            freshFor: TimeSpan.FromMinutes(1),
            asyncWait ?? TimeSpan.FromSeconds(1),
            probeTimeout ?? TimeSpan.FromSeconds(1));
}
