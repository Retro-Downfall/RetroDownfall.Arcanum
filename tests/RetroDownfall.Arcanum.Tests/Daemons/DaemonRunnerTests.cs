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

    private static DaemonRunner CreateRunner(
        IEnumerable<IDaemonJob> jobs,
        CapturingEventBus? bus,
        out InMemoryDaemonExecutionRepository repository)
    {

        ArcanumSettings settings = new()
        {
            Logs = new LogSettings { RingBufferCapacity = 16 },
            EventBus = new EventBusSettings { ChannelCapacity = 8 },
        };

        InMemoryLogRingBuffer logBuffer = new(new TestOptionsMonitor<ArcanumSettings>(settings));

        repository = new(
            new TestOptionsMonitor<ArcanumSettings>(settings),
            logBuffer);

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

        public Task StartedTask => _started.Task;

        public void SignalCompletion() => _completionSignal.TrySetResult();

        public async Task RunAsync(CancellationToken ct)
        {

            _started.TrySetResult();

            if (RunUntilSignal)
            {

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
