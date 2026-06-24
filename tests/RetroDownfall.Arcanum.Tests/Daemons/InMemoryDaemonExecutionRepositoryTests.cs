using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Daemons;
using RetroDownfall.Arcanum.Core.Logging;
using RetroDownfall.Arcanum.Infrastructure.Daemons;
using RetroDownfall.Arcanum.Infrastructure.Logging;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Daemons;

public sealed class InMemoryDaemonExecutionRepositoryTests
{

    [Fact]
    public async Task Start_complete_and_history_track_execution_lifecycle()
    {

        InMemoryDaemonExecutionRepository repository = CreateRepository();

        string executionId = await repository.StartAsync("daemon-a", "Daemon A", CancellationToken.None);

        Assert.True(repository.HasRunningExecution("daemon-a"));

        DaemonExecutionSummary completed = await repository.CompleteAsync(executionId, CancellationToken.None);

        Assert.Equal(DaemonJobStatus.Completed, completed.Status);

        Assert.False(repository.HasRunningExecution("daemon-a"));

        DaemonExecutionSummary[] history = await repository.GetHistoryAsync("daemon-a", CancellationToken.None);

        Assert.Single(history);

        Assert.Equal(executionId, history[0].Id);

    }

    [Fact]
    public async Task CancelAsync_cancels_running_execution()
    {

        InMemoryDaemonExecutionRepository repository = CreateRepository();

        string executionId = await repository.StartAsync("daemon-b", "Daemon B", CancellationToken.None);

        CancellationTokenSource? linked = repository.GetCancellationTokenSource(executionId);

        Assert.NotNull(linked);

        DaemonExecutionSummary cancelled = await repository.CancelAsync(executionId, CancellationToken.None);

        Assert.Equal(DaemonJobStatus.Cancelled, cancelled.Status);

        Assert.True(linked!.IsCancellationRequested);

    }

    [Fact]
    public async Task GetAsync_includes_correlated_log_entries()
    {

        InMemoryLogRingBuffer logBuffer = CreateLogBuffer();

        InMemoryDaemonExecutionRepository repository = new(
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()),
            logBuffer);

        string executionId = await repository.StartAsync("daemon-c", "Daemon C", CancellationToken.None);

        logBuffer.Write(new LogEntry(
            0,
            DateTimeOffset.UtcNow,
            Core.Logging.LogLevel.Information,
            "daemon",
            "ran step",
            null,
            executionId,
            null,
            []));

        DaemonExecutionDetail? detail = await repository.GetAsync(executionId, CancellationToken.None);

        Assert.NotNull(detail);

        Assert.Single(detail!.Logs);

        Assert.Equal("ran step", detail.Logs[0].Message);

    }

    private static InMemoryDaemonExecutionRepository CreateRepository()
    {

        return new InMemoryDaemonExecutionRepository(
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()),
            CreateLogBuffer());

    }

    private static InMemoryLogRingBuffer CreateLogBuffer()
    {

        ArcanumSettings settings = new()
        {
            Logs = new LogSettings { RingBufferCapacity = 16 },
            EventBus = new EventBusSettings { ChannelCapacity = 8 },
        };

        return new InMemoryLogRingBuffer(new TestOptionsMonitor<ArcanumSettings>(settings));

    }

}
