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
    public async Task RunAsync_returns_failure_when_job_already_running()
    {

        FakeDaemonJob job = new("job-c", "Job C", canRunOnDemand: true)
        {
            RunDelay = TimeSpan.FromMilliseconds(200),
        };

        DaemonRunner runner = CreateRunner([job]);

        Task<Result<DaemonExecutionSummary>> first = runner.RunAsync("job-c", force: false, CancellationToken.None);

        await job.StartedTask;

        Result<DaemonExecutionSummary> second = await runner.RunAsync("job-c", force: false, CancellationToken.None);

        Assert.True(second.IsFailure);

        Assert.Equal("Daemon.AlreadyRunning", second.Error.Code);

        _ = await first;

    }

    // W3.3 Fix 4 (atomic single-running enforcement): the on-demand path previously
    // did a non-atomic HasRunningExecution check followed by StartAsync, so two
    // concurrent on-demand starts could both pass the check and both start the job.
    // The fix replaces check+start with an atomic TryStart that reserves the
    // in-flight slot via ConcurrentDictionary.TryAdd. Two simultaneous on-demand
    // starts must yield exactly one success and one Daemon.AlreadyRunning.
    [Fact]
    public async Task RunAsync_ConcurrentOnDemandStarts_StartExactlyOne()
    {

        FakeDaemonJob job = new("job-race", "Job Race", canRunOnDemand: true)
        {
            RunUntilSignal = true,
        };

        DaemonRunner runner = CreateRunner([job]);

        using Barrier barrier = new(2);

        Task<Result<DaemonExecutionSummary>>[] runs = Enumerable.Range(0, 2)
            .Select(_ => Task.Run(() =>
            {

                barrier.SignalAndWait();

                return runner.RunAsync("job-race", force: false, CancellationToken.None);

            }))
            .ToArray();

        // Exactly one run should be rejected as AlreadyRunning promptly. If neither
        // is rejected within the window, both started (the TOCTOU bug) — the timeout
        // is the failure signal for the unfixed code.
        await Task.WhenAny(runs[0], runs[1], Task.Delay(TimeSpan.FromSeconds(2)));

        Result<DaemonExecutionSummary>? loser = runs
            .FirstOrDefault(r => r.IsCompleted && r.Result.IsFailure && r.Result.Error.Code == "Daemon.AlreadyRunning")
            ?.Result;

        Assert.NotNull(loser);

        job.SignalCompletion();

        await Task.WhenAll(runs);

        Assert.Equal(1, runs.Count(r => r.Result.IsSuccess));

        Assert.Equal(1, runs.Count(r => r.Result.IsFailure && r.Result.Error.Code == "Daemon.AlreadyRunning"));

    }

    private static DaemonRunner CreateRunner(
        IEnumerable<IDaemonJob> jobs,
        CapturingEventBus? bus = null)
    {

        ArcanumSettings settings = new()
        {
            Logs = new LogSettings { RingBufferCapacity = 16 },
            EventBus = new EventBusSettings { ChannelCapacity = 8 },
        };

        InMemoryLogRingBuffer logBuffer = new(new TestOptionsMonitor<ArcanumSettings>(settings));

        InMemoryDaemonExecutionRepository repository = new(
            new TestOptionsMonitor<ArcanumSettings>(settings),
            logBuffer);

        DaemonJobRegistry registry = new(jobs);

        CapturingEventBus eventBus = bus ?? new CapturingEventBus();

        FakeDaemonLogAttacher logAttacher = new();

        return new DaemonRunner(registry, repository, eventBus, logAttacher);

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
