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

    // W3.3 Fix 4 (id-matched removal): StartAsync overwrites _inFlightByDaemon
    // blindly (scheduled path), so two executions of the same daemon can both be
    // recorded as Running with the second one's id holding the in-flight slot.
    // Completing the OLD execution must NOT remove the NEW execution's in-flight
    // mapping. The old code did a blind TryRemove(daemonId), which evicted the new
    // execution's slot and made HasRunningExecution report stale state. The fix
    // removes the slot only when the stored id matches the completing execution.
    [Fact]
    public async Task CompleteAsync_OverlappingExecution_DoesNotEvictLaterExecution()
    {

        InMemoryDaemonExecutionRepository repository = CreateRepository();

        string firstId = await repository.StartAsync("daemon-overlap", "Daemon Overlap", CancellationToken.None);

        string secondId = await repository.StartAsync("daemon-overlap", "Daemon Overlap", CancellationToken.None);

        Assert.True(repository.HasRunningExecution("daemon-overlap"));

        // Completing the older execution must leave the newer one's slot intact.
        await repository.CompleteAsync(firstId, CancellationToken.None);

        Assert.True(repository.HasRunningExecution("daemon-overlap"), "Later execution's in-flight slot was evicted by the older execution's completion.");

        // Completing the actual in-flight execution clears the slot.
        await repository.CompleteAsync(secondId, CancellationToken.None);

        Assert.False(repository.HasRunningExecution("daemon-overlap"));

    }

    // W3.3 Fix 4 (atomic TryStart): the on-demand path must reserve the in-flight
    // slot atomically with the not-already-running check. Two concurrent TryStart
    // calls for the same daemon must admit exactly one and reject the other.
    [Fact]
    public async Task TryStartAsync_ConcurrentCalls_ForSameDaemon_AdmitExactlyOne()
    {

        InMemoryDaemonExecutionRepository repository = CreateRepository();

        using Barrier barrier = new(2);

        Task<bool>[] attempts = Enumerable.Range(0, 2)
            .Select(i => Task.Run(() =>
            {

                barrier.SignalAndWait();

                return repository.TryStartAsync("daemon-race", "Daemon Race", $"exec-{i}", CancellationToken.None);

            }))
            .ToArray();

        bool[] results = await Task.WhenAll(attempts);

        Assert.Single(results, static r => r);

        Assert.Single(results, static r => !r);

        Assert.True(repository.HasRunningExecution("daemon-race"));

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
