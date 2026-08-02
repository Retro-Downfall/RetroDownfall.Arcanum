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

}
