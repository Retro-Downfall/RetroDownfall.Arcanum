using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Daemons;
using RetroDownfall.Arcanum.Core.Events;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Daemons;
using RetroDownfall.Arcanum.Infrastructure.Logging;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Daemons;

public sealed class DaemonRunnerTests
{
    [Fact]
    public async Task RunAsync_returns_not_found_for_missing_job()
    {
        DaemonRunner runner = CreateRunner([]);

        Result<DaemonExecutionSummary> result = await runner.RunAsync("missing", force: false, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Daemon.NotFound", result.Error.Code);
    }

    [Fact]
    public async Task RunAsync_rejects_on_demand_when_disabled()
    {
        FakeDaemonJob job = new("job-a", "Job A", canRunOnDemand: false);

        DaemonRunner runner = CreateRunner([job]);

        Result<DaemonExecutionSummary> result = await runner.RunAsync("job-a", force: false, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Daemon.Disabled", result.Error.Code);
    }

    [Fact]
    public async Task RunAsync_completes_successful_job_and_publishes_events()
    {
        FakeDaemonJob job = new("job-b", "Job B", canRunOnDemand: true);

        CapturingEventBus bus = new();

        DaemonRunner runner = CreateRunner([job], bus);

        Result<DaemonExecutionSummary> result = await runner.RunAsync("job-b", force: false, CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(DaemonJobStatus.Completed, result.Value.Status);

        Assert.Equal(2, bus.Events.Count);

        Assert.Equal(DaemonEventType.Started, bus.Events[0].EventType);

        Assert.Equal(DaemonEventType.Completed, bus.Events[1].EventType);
    }

    [Fact]
    public async Task RunAsync_OverlappingOnDemandStarts_StartExactlyOne()
    {
        FakeDaemonJob job = new("job-c", "Job C", canRunOnDemand: true)
        {
            RunUntilSignal = true,
        };

        DaemonRunner runner = CreateRunner([job]);

        Task<Result<DaemonExecutionSummary>> first = runner.RunAsync("job-c", force: false, CancellationToken.None);

        await job.StartedTask;

        Result<DaemonExecutionSummary> second;

        try
        {
            second = await runner.RunAsync("job-c", force: false, CancellationToken.None);
        }
        finally
        {
            job.SignalCompletion();
        }

        Assert.True(second.IsFailure);
        Assert.Equal("Daemon.AlreadyRunning", second.Error.Code);
        Result<DaemonExecutionSummary> winner = await first;
        Assert.True(winner.IsSuccess);
    }

    [Fact]
    public async Task RunScheduledAsync_while_on_demand_running_returns_AlreadyRunning()
    {
        FakeDaemonJob job = new("job-sched", "Job Sched", canRunOnDemand: true)
        {
            RunUntilSignal = true,
        };

        DaemonRunner runner = CreateRunner([job]);

        Task<Result<DaemonExecutionSummary>> onDemand = runner.RunAsync("job-sched", force: false, CancellationToken.None);

        await job.StartedTask;

        Result<DaemonExecutionSummary> scheduled = await runner.RunScheduledAsync("job-sched", CancellationToken.None);

        Assert.True(scheduled.IsFailure);

        Assert.Equal("Daemon.AlreadyRunning", scheduled.Error.Code);

        job.SignalCompletion();

        _ = await onDemand;
    }

    [Fact]
    public async Task RunScheduledAsync_skips_CanRunOnDemand_gate()
    {
        FakeDaemonJob job = new("job-sched-only", "Job Sched Only", canRunOnDemand: false);

        DaemonRunner runner = CreateRunner([job]);

        Result<DaemonExecutionSummary> onDemand = await runner.RunAsync("job-sched-only", force: false, CancellationToken.None);

        Assert.True(onDemand.IsFailure);

        Assert.Equal("Daemon.Disabled", onDemand.Error.Code);

        Result<DaemonExecutionSummary> scheduled = await runner.RunScheduledAsync("job-sched-only", CancellationToken.None);

        Assert.True(scheduled.IsSuccess);

        Assert.Equal(DaemonJobStatus.Completed, scheduled.Value.Status);
    }

    [Fact]
    public async Task RunAsync_caller_cancel_still_reaches_terminal_Failed_status()
    {
        FakeDaemonJob job = new("job-cancel", "Job Cancel", canRunOnDemand: true)
        {
            RunUntilSignal = true,
        };

        CapturingEventBus bus = new();

        DaemonRunner runner = CreateRunner([job], bus, out InMemoryDaemonExecutionRepository repository);

        using CancellationTokenSource cts = new();

        Task<Result<DaemonExecutionSummary>> run = runner.RunAsync("job-cancel", force: false, cts.Token);

        await job.StartedTask;

        await cts.CancelAsync();

        Result<DaemonExecutionSummary> result = await run;

        Assert.True(result.IsFailure);

        Assert.Equal("Daemon.Cancelled", result.Error.Code);

        Assert.False(repository.HasRunningExecution("job-cancel"));
    }

    /// <summary>
    /// An out-of-band cancel (POST /api/executions/{id}/cancel) records the execution terminal while the
    /// job body is still unwinding — an agentic tool call or MCP round-trip inside ExecutePromptAsync can
    /// ignore the token for a long time. The per-daemon single-flight reservation must therefore survive
    /// until the body actually returns, or the operator's natural cancel-then-rerun puts two headless
    /// inference turns against the same target spell and the same Lexicon daemon-state entity in flight.
    /// </summary>
    [Fact]
    public async Task Out_of_band_cancel_does_not_admit_a_second_run_while_the_job_body_drains()
    {
        FakeDaemonJob job = new("job-drain", "Job Drain", canRunOnDemand: true)
        {
            RunUntilSignal = true,
            IgnoresCancellation = true,
        };

        DaemonRunner runner = CreateRunner([job], null, out InMemoryDaemonExecutionRepository repository);

        Task<Result<DaemonExecutionSummary>> run = runner.RunAsync("job-drain", force: false, CancellationToken.None);

        await job.StartedTask;

        DaemonExecutionSummary[] history = await repository.GetHistoryAsync("job-drain", CancellationToken.None);

        string executionId = Assert.Single(history).Id;

        DaemonExecutionSummary cancelled = await repository.CancelAsync(executionId, CancellationToken.None);

        Assert.Equal(DaemonJobStatus.Cancelled, cancelled.Status);

        Assert.True(repository.HasRunningExecution("job-drain"));

        Result<DaemonExecutionSummary> second = await runner.RunAsync("job-drain", force: true, CancellationToken.None);

        Assert.True(second.IsFailure);

        Assert.Equal("Daemon.AlreadyRunning", second.Error.Code);

        job.SignalCompletion();

        _ = await run;

        Assert.False(repository.HasRunningExecution("job-drain"));

        Result<DaemonExecutionSummary> afterDrain = await runner.RunAsync("job-drain", force: true, CancellationToken.None);

        Assert.True(afterDrain.IsSuccess);
    }

    /// <summary>
    /// The runner reserves the daemon's single-flight slot before it looks up the execution's token source,
    /// so a missing source must release the reservation: nothing else ever will for the life of the host.
    /// </summary>
    [Fact]
    public async Task RunAsync_releases_the_slot_when_the_execution_token_source_is_missing()
    {
        FakeDaemonJob job = new("job-no-cts", "Job No Cts", canRunOnDemand: true);

        InMemoryDaemonExecutionRepository inner = new(new InMemoryLogRingBuffer());

        NoTokenSourceRepository repository = new(inner);

        DaemonRunner runner = new(
            new DaemonJobRegistry([job]),
            repository,
            new CapturingEventBus(),
            new FakeDaemonLogAttacher());

        Result<DaemonExecutionSummary> result = await runner.RunAsync("job-no-cts", force: false, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Daemon.NotFound", result.Error.Code);

        Assert.False(inner.HasRunningExecution("job-no-cts"));

        DaemonExecutionSummary only = Assert.Single(await inner.GetHistoryAsync("job-no-cts", CancellationToken.None));

        Assert.Equal(DaemonJobStatus.Failed, only.Status);

        Assert.False(job.StartedTask.IsCompleted);

        // The slot is free again, so the next run is admitted instead of reporting AlreadyRunning.
        Result<DaemonExecutionSummary> next = await runner.RunAsync("job-no-cts", force: false, CancellationToken.None);

        Assert.NotEqual("Daemon.AlreadyRunning", next.Error.Code);
    }

    private static DaemonRunner CreateRunner(
        IEnumerable<IDaemonJob> jobs,
        CapturingEventBus? bus,
        out InMemoryDaemonExecutionRepository repository)
    {
        InMemoryLogRingBuffer logBuffer = new();

        repository = new(logBuffer);

        DaemonJobRegistry registry = new(jobs);

        CapturingEventBus eventBus = bus ?? new CapturingEventBus();

        FakeDaemonLogAttacher logAttacher = new();

        return new DaemonRunner(registry, repository, eventBus, logAttacher);
    }

    private static DaemonRunner CreateRunner(
        IEnumerable<IDaemonJob> jobs,
        CapturingEventBus? bus = null)
    {
        return CreateRunner(jobs, bus, out _);
    }

    private sealed class NoTokenSourceRepository(InMemoryDaemonExecutionRepository inner) : IDaemonExecutionRepository
    {
        public Task<DaemonExecutionSummary[]> GetHistoryAsync(string? daemonId, CancellationToken ct) =>
            inner.GetHistoryAsync(daemonId, ct);

        public Task<DaemonExecutionDetail?> GetAsync(string executionId, CancellationToken ct) =>
            inner.GetAsync(executionId, ct);

        public Task<string> StartAsync(string daemonId, string daemonName, CancellationToken ct) =>
            inner.StartAsync(daemonId, daemonName, ct);

        public Task<bool> TryStartAsync(string daemonId, string daemonName, string executionId, CancellationToken ct) =>
            inner.TryStartAsync(daemonId, daemonName, executionId, ct);

        public Task<DaemonExecutionSummary> CompleteAsync(string executionId, CancellationToken ct) =>
            inner.CompleteAsync(executionId, ct);

        public Task<DaemonExecutionSummary> FailAsync(string executionId, string errorMessage, CancellationToken ct) =>
            inner.FailAsync(executionId, errorMessage, ct);

        public Task<DaemonExecutionSummary> CancelAsync(string executionId, CancellationToken ct) =>
            inner.CancelAsync(executionId, ct);

        public Task ReportDrainedAsync(string executionId, CancellationToken ct) =>
            inner.ReportDrainedAsync(executionId, ct);

        public Task<bool> TryDeleteTerminalAsync(string executionId, CancellationToken ct) =>
            inner.TryDeleteTerminalAsync(executionId, ct);

        public Task<bool> TryDeleteTerminalBeforeAsync(string executionId, DateTimeOffset completedAtCutoff, CancellationToken ct) =>
            inner.TryDeleteTerminalBeforeAsync(executionId, completedAtCutoff, ct);

        public bool HasRunningExecution(string daemonId) =>
            inner.HasRunningExecution(daemonId);

        public bool IsAwaitingDrain(string executionId) =>
            inner.IsAwaitingDrain(executionId);

        public CancellationTokenSource? GetCancellationTokenSource(string executionId) => null;
    }

    private sealed class FakeDaemonJob(string id, string name, bool canRunOnDemand) : IDaemonJob
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _completionSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Id { get; } = id;

        public string Name { get; } = name;

        public string? Description => null;

        public bool CanRunOnDemand { get; } = canRunOnDemand;

        public string TargetSpell => "spell";

        public TimeSpan RunDelay { get; init; } = TimeSpan.Zero;

        public bool RunUntilSignal { get; init; }

        /// <summary>
        /// Stands in for the work that genuinely outlives a cancel — an agentic tool call or MCP stdio
        /// round-trip inside <c>ExecutePromptAsync</c> that never observes the token.
        /// </summary>
        public bool IgnoresCancellation { get; init; }

        public Task StartedTask => _started.Task;

        public void SignalCompletion() => _completionSignal.TrySetResult();

        public async Task RunAsync(CancellationToken ct)
        {
            _started.TrySetResult();

            if (RunUntilSignal)
            {
                if (IgnoresCancellation)
                {
                    await _completionSignal.Task.ConfigureAwait(false);

                    return;
                }

                await _completionSignal.Task.WaitAsync(ct).ConfigureAwait(false);

                return;
            }

            if (RunDelay > TimeSpan.Zero)
            {
                await Task.Delay(RunDelay, ct).ConfigureAwait(false);
            }
        }
    }

    private sealed class CapturingEventBus : IEventBus
    {
        public List<DaemonEvent> Events { get; } = [];

        public void Publish<T>(T @event) where T : notnull
        {
            if (@event is DaemonEvent daemonEvent)
            {
                Events.Add(daemonEvent);
            }
        }

        public IAsyncEnumerable<T> Subscribe<T>(CancellationToken cancellationToken) where T : notnull =>
            AsyncEnumerable.Empty<T>();
    }

    private sealed class FakeDaemonLogAttacher : IDaemonLogAttacher
    {
        public IDisposable BeginExecutionScope(string executionId) => new NoopDisposable();

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
