using RetroDownfall.Arcanum.Core.Daemons;

using RetroDownfall.Arcanum.Infrastructure.Daemons;

using RetroDownfall.Arcanum.Infrastructure.Logging;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Daemons;

public sealed class DaemonExecutionRetentionTests
{
    [Fact]

    public async Task TryDeleteTerminalBeforeAsync_UsesCompletionBoundaryInsideAtomicDelete()
    {
        FakeTimeProvider time = new();

        time.SetUtcNow(DateTimeOffset.Parse("2026-01-02T00:00:00Z"));

        InMemoryDaemonExecutionRepository repository = new(
            new InMemoryLogRingBuffer(),
            time);

        string executionId = await repository.StartAsync(
            "bounded",
            "Bounded",
            CancellationToken.None);

        _ = await repository.CompleteAsync(executionId, CancellationToken.None);

        Assert.False(
            await repository.TryDeleteTerminalBeforeAsync(
                executionId,
                DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                CancellationToken.None));

        Assert.NotNull(
            await repository.GetAsync(
                executionId,
                CancellationToken.None));

        Assert.True(
            await repository.TryDeleteTerminalBeforeAsync(
                executionId,
                DateTimeOffset.Parse("2026-01-03T00:00:00Z"),
                CancellationToken.None));
    }

    [Fact]

    public async Task TryDeleteTerminalAsync_DeletesOnlyTerminalExecutions()
    {
        InMemoryDaemonExecutionRepository repository = new(
            new InMemoryLogRingBuffer());

        string terminal = await repository.StartAsync(
            "terminal",
            "Terminal",
            CancellationToken.None);

        _ = await repository.CompleteAsync(terminal, CancellationToken.None);

        string running = await repository.StartAsync(
            "running",
            "Running",
            CancellationToken.None);

        Assert.True(
            await repository.TryDeleteTerminalAsync(
                terminal,
                CancellationToken.None));

        Assert.False(
            await repository.TryDeleteTerminalAsync(
                running,
                CancellationToken.None));

        Assert.False(
            await repository.TryDeleteTerminalAsync(
                "missing",
                CancellationToken.None));

        DaemonExecutionSummary[] remaining = await repository.GetHistoryAsync(
            null,
            CancellationToken.None);

        DaemonExecutionSummary only = Assert.Single(remaining);

        Assert.Equal(running, only.Id);

        Assert.Equal(DaemonJobStatus.Running, only.Status);
    }

    [Fact]

    public async Task TryDeleteTerminalBeforeAsync_refuses_a_cancelled_execution_whose_job_has_not_drained()
    {
        FakeTimeProvider time = new();

        time.SetUtcNow(DateTimeOffset.Parse("2026-01-02T00:00:00Z"));

        InMemoryDaemonExecutionRepository repository = new(
            new InMemoryLogRingBuffer(),
            time);

        string executionId = await repository.StartAsync(
            "draining",
            "Draining",
            CancellationToken.None);

        _ = await repository.CancelAsync(executionId, CancellationToken.None);

        Assert.True(repository.HasRunningExecution("draining"));

        Assert.False(
            await repository.TryDeleteTerminalBeforeAsync(
                executionId,
                DateTimeOffset.MaxValue,
                CancellationToken.None));

        Assert.False(
            await repository.TryDeleteTerminalAsync(
                executionId,
                CancellationToken.None));

        Assert.NotNull(
            await repository.GetAsync(
                executionId,
                CancellationToken.None));

        // The record must survive until the runner's drain report, or the single-flight slot is stranded.
        await repository.ReportDrainedAsync(executionId, CancellationToken.None);

        Assert.False(repository.HasRunningExecution("draining"));

        Assert.True(
            await repository.TryDeleteTerminalBeforeAsync(
                executionId,
                DateTimeOffset.MaxValue,
                CancellationToken.None));
    }

    [Fact]

    public async Task A_cancelled_execution_is_awaiting_drain_until_the_drain_is_reported()
    {
        InMemoryDaemonExecutionRepository repository = new(
            new InMemoryLogRingBuffer());

        string executionId = await repository.StartAsync(
            "draining-flag",
            "Draining Flag",
            CancellationToken.None);

        Assert.False(repository.IsAwaitingDrain(executionId));

        _ = await repository.CancelAsync(executionId, CancellationToken.None);

        Assert.True(repository.IsAwaitingDrain(executionId));

        await repository.ReportDrainedAsync(executionId, CancellationToken.None);

        Assert.False(repository.IsAwaitingDrain(executionId));

        Assert.False(repository.IsAwaitingDrain("missing"));
    }
}
