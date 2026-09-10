using System.Collections.Concurrent;
using System.Reflection;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Daemons;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Daemons;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Hosting;

internal sealed class UnseenServantAdmissionHarness : IAsyncDisposable
{
    internal ConcurrentQueue<string> Events { get; } = new();

    internal Func<string, CancellationToken, Task> OnStep { get; set; } = static (_, _) => Task.CompletedTask;

    internal GrimoireConnectionAdmissionGate Inner { get; } = new(TimeProvider.System);

    internal RecordingGrimoireWorkAdmissionGate Gate { get; }

    internal UnseenServantAdmissionObserver Admission { get; }

    internal DaemonJobStatus RunnerStatus { get; set; } = DaemonJobStatus.Completed;

    internal TestCapturingLogger<UnseenServantService> Logger { get; } = new();

    internal UnseenServantService Service { get; }

    internal ArcanumSettings Settings { get; } = new();

    internal WatermarkStore Store { get; }

    internal UnseenServantJobTracker Tracker { get; } = new();

    internal ScopeFactory Scopes { get; }

    internal SchedulerClock Clock { get; } = new();

    private readonly ServiceProvider _provider;

    internal UnseenServantAdmissionHarness()
    {
        Gate = new RecordingGrimoireWorkAdmissionGate(Inner)
        {
            BeforeEffectGroupDisposalAsync = () => new ValueTask(StepAsync("group-dispose", CancellationToken.None)),
            BeforeWorkLeaseDisposalAsync = () => new ValueTask(StepAsync("lease-dispose", CancellationToken.None)),
        };

        Store = new WatermarkStore(this);

        Admission = new UnseenServantAdmissionObserver(this, Gate);

        ServiceCollection services = new();

        services.AddSingleton<IUnseenServantWatermarkStore>(Store);

        services.AddSingleton<IIdempotencyStore>(new LegacyStore(this));

        services.AddSingleton<IIdempotencyClaimStore>(new ClaimStore(this));

        services.AddSingleton<IGrimoireConnectionAdmissionGate>(Admission);

        services.AddSingleton<TimeProvider>(Clock);

        _provider = services.BuildServiceProvider();

        Scopes = new ScopeFactory(this, _provider.GetRequiredService<IServiceScopeFactory>());

        Service = ActivatorUtilities.CreateInstance<UnseenServantService>(
            _provider,
            new TestOptionsMonitor<ArcanumSettings>(Settings),
            new Pacer(),
            Tracker,
            new Runner(this),
            Logger,
            Scopes);
    }

    internal async Task StepAsync(string step, CancellationToken token)
    {
        Events.Enqueue(step);

        await OnStep(step, token);
    }

    internal Task InvokeAsync(string method, CancellationToken token = default) =>
        (Task)typeof(UnseenServantService).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Service, [token])!;

    internal T ReadField<T>(string name) =>
        (T)typeof(UnseenServantService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Service)!;

    internal void Dispatch(CancellationToken token = default) =>
        typeof(UnseenServantService).GetMethod("DispatchDueJobs", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Service, [token]);

    internal Task[] ActiveTasks => ReadField<ConcurrentDictionary<Guid, Task>>("_activeJobTasks").Values.ToArray();

    internal string[] RunningKeys => ReadField<ConcurrentDictionary<string, byte>>("_runningJobs").Keys.ToArray();

    internal async Task<UnseenServantJob> ConfigureDueJobAsync()
    {
        UnseenServantJob job = new() { Name = "watch", TargetSpell = "patrol", IntervalMinutes = 5 };

        Settings.Daemon.Jobs = [job];

        await Tracker.HydrateAsync([new UnseenServantWatermark("watch\0patrol", Clock.GetUtcNow().AddHours(-2), 5)]);

        return job;
    }

    public async ValueTask DisposeAsync()
    {
        Service.Dispose();

        await _provider.DisposeAsync();
    }

    internal sealed class Checkpoint
    {
        internal TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal async Task PauseAsync(CancellationToken token = default)
        {
            Reached.TrySetResult();

            await Release.Task.WaitAsync(token);
        }

        internal Task WaitAsync() => Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    internal sealed class SchedulerClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        internal TaskCompletionSource<TickTimer> Created { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override DateTimeOffset GetUtcNow() => _now;

        internal async Task TickAsync()
        {
            TickTimer timer = await Created.Task.WaitAsync(TimeSpan.FromSeconds(10));

            _now = _now.AddMinutes(1);

            timer.Tick();
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            TickTimer timer = new(callback, state);

            Created.TrySetResult(timer);

            return timer;
        }

        internal sealed class TickTimer(TimerCallback callback, object? state) : ITimer
        {
            private int _disposed;

            internal void Tick()
            {
                if (Volatile.Read(ref _disposed) == 0)
                {
                    callback(state);
                }
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose() => Interlocked.Exchange(ref _disposed, 1);

            public ValueTask DisposeAsync()
            {
                Dispose();

                return ValueTask.CompletedTask;
            }
        }
    }

    internal sealed class ScopeFactory(UnseenServantAdmissionHarness harness, IServiceScopeFactory inner) : IServiceScopeFactory
    {
        internal int Created { get; private set; }

        public IServiceScope CreateScope()
        {
            Created++;

            harness.Events.Enqueue("scope-create");

            return new Scope(harness, inner.CreateScope());
        }

        private sealed class Scope(UnseenServantAdmissionHarness harness, IServiceScope inner) : IServiceScope, IAsyncDisposable
        {
            public IServiceProvider ServiceProvider => inner.ServiceProvider;

            public void Dispose()
            {
                harness.Events.Enqueue("scope-dispose-sync");

                inner.Dispose();
            }

            public async ValueTask DisposeAsync()
            {
                await harness.StepAsync("scope-dispose", CancellationToken.None);

                await ((IAsyncDisposable)inner).DisposeAsync();
            }
        }
    }

    internal sealed class WatermarkStore(UnseenServantAdmissionHarness harness) : IUnseenServantWatermarkStore
    {
        internal List<UnseenServantWatermark> Rows { get; } = [];

        public async Task<IReadOnlyList<UnseenServantWatermark>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            await harness.StepAsync("hydrate", cancellationToken);

            return Rows.ToArray();
        }

        public Task<UnseenServantWatermark?> GetAsync(string jobKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(Rows.SingleOrDefault(row => row.JobKey == jobKey));

        public async Task SaveAsync(string jobKey, DateTimeOffset lastRunAt, int effectiveIntervalMinutes, CancellationToken cancellationToken = default)
        {
            await harness.StepAsync("watermark", cancellationToken);

            Rows.RemoveAll(row => row.JobKey == jobKey);

            Rows.Add(new UnseenServantWatermark(jobKey, lastRunAt, effectiveIntervalMinutes));
        }

        public Task DeleteAsync(string jobKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task SaveLastRunAsync(string jobKey, DateTimeOffset lastRunAt, int initialIntervalMinutes, CancellationToken cancellationToken = default) =>
            SaveAsync(jobKey, lastRunAt, Rows.SingleOrDefault(row => row.JobKey == jobKey)?.EffectiveIntervalMinutes ?? initialIntervalMinutes, cancellationToken);

        public Task SaveIntervalAsync(string jobKey, DateTimeOffset initialLastRunAt, int effectiveIntervalMinutes, CancellationToken cancellationToken = default) =>
            SaveAsync(jobKey, Rows.SingleOrDefault(row => row.JobKey == jobKey)?.LastRunAt ?? initialLastRunAt, effectiveIntervalMinutes, cancellationToken);
    }

    private sealed class Pacer : IUnseenServantPacer
    {
        public Task<bool> SetDynamicIntervalAsync(string jobName, int intervalMinutes, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public int GetEffectiveInterval(UnseenServantJob job) => job.IntervalMinutes;

        public Task HydrateAsync(IReadOnlyList<UnseenServantWatermark> watermarks, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class Runner(UnseenServantAdmissionHarness harness) : IDaemonRunner
    {
        public Task<Result<DaemonExecutionSummary>> RunAsync(string daemonId, bool force, CancellationToken ct) => throw new NotSupportedException();

        public async Task<Result<DaemonExecutionSummary>> RunScheduledAsync(string daemonId, CancellationToken ct)
        {
            await harness.StepAsync("runner:" + daemonId, ct);

            return new DaemonExecutionSummary("execution", daemonId, "watch", harness.RunnerStatus, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, harness.RunnerStatus == DaemonJobStatus.Completed ? null : "runner disposition");
        }
    }

    private sealed class LegacyStore(UnseenServantAdmissionHarness harness) : IIdempotencyStore
    {
        public async Task<int> DeleteExpiredAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default)
        {
            await harness.StepAsync("legacy-cleanup", cancellationToken);

            return 0;
        }

        public Task<IdempotencyRecord?> TryGetAsync(string keyHash, DateTimeOffset notOlderThan, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task SaveAsync(string keyHash, int statusCode, string? contentType, string responseBody, DateTimeOffset createdAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ClaimStore(UnseenServantAdmissionHarness harness) : IIdempotencyClaimStore
    {
        public async Task<int> DeleteExpiredAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default)
        {
            await harness.StepAsync("claim-cleanup", cancellationToken);

            return 0;
        }

        public Task<IdempotencyClaim?> TryGetAsync(string claimKeyHash, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IdempotencyClaim?> GetByIdAsync(Guid claimId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IdempotencyClaimAcquireResult> TryAcquireAsync(IdempotencyClaimAcquireRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> HeartbeatAsync(Guid claimId, string ownerId, DateTimeOffset leaseExpiresAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task CompleteAsync(Guid claimId, string ownerId, int statusCode, string? contentType, string responseBody, bool terminalStreamValid, Guid? runId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task MarkFailedAsync(Guid claimId, string ownerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task MarkAbandonedAsync(Guid claimId, string ownerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> TryReclaimAsync(Guid claimId, string newOwnerId, DateTimeOffset leaseExpiresAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task LinkRunAsync(Guid claimId, Guid runId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
