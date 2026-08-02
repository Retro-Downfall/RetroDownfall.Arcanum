using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Daemons;
using RetroDownfall.Arcanum.Core.Logging;
using RetroDownfall.Arcanum.Infrastructure.Daemons;
using RetroDownfall.Arcanum.Infrastructure.Logging;

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

        InMemoryDaemonExecutionRepository repository = new(logBuffer);

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

    // W3.3 Fix 4 (id-matched removal): overlapping StartAsync for the same daemon is no
    // longer allowed (single-flight). Completing a finished execution must not clear a
    // different daemon's in-flight slot — covered by TryStart + RemoveInFlightIfMatch.
    // This test verifies two distinct daemons can both be Running concurrently.
    [Fact]
    public async Task StartAsync_DistinctDaemons_RunConcurrently()
    {

        InMemoryDaemonExecutionRepository repository = CreateRepository();

        Task<string> firstStart = repository.StartAsync(
            "daemon-a",
            "Daemon A",
            CancellationToken.None);

        Task<string> secondStart = repository.StartAsync(
            "daemon-b",
            "Daemon B",
            CancellationToken.None);

        string[] executionIds = await Task.WhenAll(
            firstStart,
            secondStart);

        string firstId = executionIds[0];

        string secondId = executionIds[1];

        Assert.True(repository.HasRunningExecution("daemon-a"));

        Assert.True(repository.HasRunningExecution("daemon-b"));

        await repository.CompleteAsync(firstId, CancellationToken.None);

        Assert.False(repository.HasRunningExecution("daemon-a"));

        Assert.True(repository.HasRunningExecution("daemon-b"));

        await repository.CompleteAsync(secondId, CancellationToken.None);

        Assert.False(repository.HasRunningExecution("daemon-b"));

    }

    [Fact]

    public async Task StartAsync_WaitsForExclusiveMutationLease_ThenStarts()
    {

        InMemoryDaemonExecutionRepository repository = CreateRepository();

        IDaemonExecutionMutationGate gate = repository;

        IAsyncDisposable lease = await gate.AcquireExclusiveAsync(
            CancellationToken.None);

        Task<string> start = repository.StartAsync(
            "daemon-gated",
            "Daemon Gated",
            CancellationToken.None);

        await Task.Yield();

        Assert.False(start.IsCompleted);

        await lease.DisposeAsync();

        string executionId = await start.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(repository.HasRunningExecution("daemon-gated"));

        DaemonExecutionDetail? detail = await repository.GetAsync(
            executionId,
            CancellationToken.None);

        Assert.NotNull(detail);

        Assert.Equal(DaemonJobStatus.Running, detail!.Status);

    }

    [Fact]

    public async Task TryStartAsync_WaitsForExclusiveMutationLease_ThenStarts()
    {

        InMemoryDaemonExecutionRepository repository = CreateRepository();

        IDaemonExecutionMutationGate gate = repository;

        IAsyncDisposable lease = await gate.AcquireExclusiveAsync(
            CancellationToken.None);

        Task<bool> start = repository.TryStartAsync(
            "daemon-gated-try",
            "Daemon Gated Try",
            "gated-execution",
            CancellationToken.None);

        await Task.Yield();

        Assert.False(start.IsCompleted);

        await lease.DisposeAsync();

        Assert.True(await start.WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.True(repository.HasRunningExecution("daemon-gated-try"));

    }

    [Fact]
    public async Task StartAsync_SameDaemonWhileRunning_Throws()
    {

        InMemoryDaemonExecutionRepository repository = CreateRepository();

        _ = await repository.StartAsync("daemon-overlap", "Daemon Overlap", CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.StartAsync("daemon-overlap", "Daemon Overlap", CancellationToken.None));

        Assert.True(repository.HasRunningExecution("daemon-overlap"));

    }

    [Fact]
    public async Task TrimHistory_NeverRemovesRunningExecutions()
    {
        InMemoryDaemonExecutionRepository repository = CreateRepository();
        int historyLimit = ArcanumSettingClamps.DaemonExecutionHistoryLimit(
            ArcanumRuntimeDefaults.DaemonExecutionHistoryLimit);

        for (int i = 0; i < historyLimit; i++)
        {
            string completed = await repository.StartAsync("daemon-trim", "Daemon Trim", CancellationToken.None);
            await repository.CompleteAsync(completed, CancellationToken.None);
        }

        string running = await repository.StartAsync("daemon-trim", "Daemon Trim", CancellationToken.None);

        // Crossing the code-owned history limit trims a completed record, never the live one.
        DaemonExecutionDetail? detail = await repository.GetAsync(running, CancellationToken.None);

        Assert.NotNull(detail);

        Assert.Equal(DaemonJobStatus.Running, detail!.Status);

        Assert.True(repository.HasRunningExecution("daemon-trim"));

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

    [Fact]
    public async Task Cancel_Then_Complete_RemainsCancelled()
    {
        InMemoryDaemonExecutionRepository repository = CreateRepository();
        string executionId = await repository.StartAsync("daemon-cas", "Daemon CAS", CancellationToken.None);

        DaemonExecutionSummary cancelled = await repository.CancelAsync(executionId, CancellationToken.None);
        Assert.Equal(DaemonJobStatus.Cancelled, cancelled.Status);

        DaemonExecutionSummary afterComplete = await repository.CompleteAsync(executionId, CancellationToken.None);
        Assert.Equal(DaemonJobStatus.Cancelled, afterComplete.Status);
    }

    [Fact]
    public async Task Complete_Then_Cancel_ThrowsNotRunning()
    {
        InMemoryDaemonExecutionRepository repository = CreateRepository();
        string executionId = await repository.StartAsync("daemon-cas-2", "Daemon CAS 2", CancellationToken.None);

        DaemonExecutionSummary completed = await repository.CompleteAsync(executionId, CancellationToken.None);
        Assert.Equal(DaemonJobStatus.Completed, completed.Status);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.CancelAsync(executionId, CancellationToken.None));
        Assert.Contains("not running", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Fail_Racing_Cancel_FirstTerminalWins()
    {
        InMemoryDaemonExecutionRepository repository = CreateRepository();
        string executionId = await repository.StartAsync("daemon-race-term", "Daemon Race Term", CancellationToken.None);

        using Barrier barrier = new(2);
        Task<DaemonExecutionSummary> failTask = Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await repository.FailAsync(executionId, "boom", CancellationToken.None);
        });
        Task<DaemonExecutionSummary> cancelTask = Task.Run(async () =>
        {
            barrier.SignalAndWait();
            try
            {
                return await repository.CancelAsync(executionId, CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
                DaemonExecutionDetail? detail = await repository.GetAsync(executionId, CancellationToken.None);
                Assert.NotNull(detail);
                return new DaemonExecutionSummary(
                    detail!.Id,
                    detail.DaemonId,
                    detail.DaemonName,
                    detail.Status,
                    detail.StartedAt,
                    detail.CompletedAt,
                    detail.ErrorMessage);
            }
        });

        DaemonExecutionSummary[] results = await Task.WhenAll(failTask, cancelTask);
        Assert.All(
            results,
            r => Assert.True(
                r.Status is DaemonJobStatus.Failed or DaemonJobStatus.Cancelled,
                $"unexpected status {r.Status}"));
        Assert.Equal(results[0].Status, results[1].Status);

        DaemonExecutionDetail? final = await repository.GetAsync(executionId, CancellationToken.None);
        Assert.NotNull(final);
        Assert.True(final!.Status is DaemonJobStatus.Failed or DaemonJobStatus.Cancelled);
    }

    private static InMemoryDaemonExecutionRepository CreateRepository()
    {
        return new InMemoryDaemonExecutionRepository(CreateLogBuffer());
    }

    private static InMemoryLogRingBuffer CreateLogBuffer() => new();

}
