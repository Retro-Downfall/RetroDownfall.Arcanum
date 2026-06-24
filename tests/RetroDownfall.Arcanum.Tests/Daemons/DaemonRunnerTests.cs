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

        await Task.Delay(25);

        Result<DaemonExecutionSummary> second = await runner.RunAsync("job-c", force: false, CancellationToken.None);

        Assert.True(second.IsFailure);

        Assert.Equal("Daemon.AlreadyRunning", second.Error.Code);

        _ = await first;

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

        public string Id { get; } = id;

        public string Name { get; } = name;

        public string? Description => null;

        public bool CanRunOnDemand { get; } = canRunOnDemand;

        public string TargetSpell => "spell";

        public TimeSpan RunDelay { get; init; } = TimeSpan.Zero;

        public async Task RunAsync(CancellationToken ct)
        {

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
