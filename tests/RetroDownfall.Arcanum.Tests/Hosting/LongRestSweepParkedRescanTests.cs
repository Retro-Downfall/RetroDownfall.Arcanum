using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Operations.LongRest;
using RetroDownfall.Arcanum.Tests.LongRest;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Hosting;

/// <summary>
/// The worker's Parked Rescan: nothing signals a cooldown that expires or a recovery pass that parks a sweep, so the worker
/// also wakes on a clock timer, named by the earliest cooldown the discovery reports. The clock here is manual, so the
/// deadline a test advances to is the only thing that can fire the timer.
/// </summary>
public sealed class LongRestSweepParkedRescanTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task The_worker_sleeps_until_the_next_cooldown_due_time_then_scans_without_any_queue_signal()
    {
        await using Harness h = new();

        h.Store.Script(new LongRestRunnableSweep(null, h.Clock.GetUtcNow().AddSeconds(30)));

        await h.StartAsync();

        await h.Store.Call(1);

        await h.Clock.WhenArmedTimersAsync(static armed => armed == 1, Wait);

        Assert.Equal(TimeSpan.FromSeconds(30), h.Clock.NextDueIn);

        // Not due yet: a clock that moves 29 seconds fires nothing, so the store is not asked again.
        h.Clock.Advance(TimeSpan.FromSeconds(29));

        Assert.Equal(1, h.Store.Calls);

        h.Store.Script(new LongRestRunnableSweep(h.Operation, null), new LongRestRunnableSweep(null, null));

        h.Clock.Advance(TimeSpan.FromSeconds(1));

        await h.Store.Call(3);

        Assert.Equal([h.Operation], h.Sweep.Resumed);

        // Nothing is cooling any more: the next sleep is the plain rescan interval.
        await h.Clock.WhenArmedTimersAsync(static armed => armed == 1, Wait);

        Assert.Equal(LongRestSweepHostedService.ParkedRescanInterval, h.Clock.NextDueIn);
    }

    [Fact]
    public async Task With_nothing_cooling_the_worker_rescans_every_sixty_seconds()
    {
        await using Harness h = new();

        await h.StartAsync();

        await h.Store.Call(1);

        await h.Clock.WhenArmedTimersAsync(static armed => armed == 1, Wait);

        Assert.Equal(TimeSpan.FromSeconds(60), LongRestSweepHostedService.ParkedRescanInterval);

        Assert.Equal(LongRestSweepHostedService.ParkedRescanInterval, h.Clock.NextDueIn);

        h.Clock.Advance(LongRestSweepHostedService.ParkedRescanInterval - TimeSpan.FromSeconds(1));

        Assert.Equal(1, h.Store.Calls);

        h.Clock.Advance(TimeSpan.FromSeconds(1));

        await h.Store.Call(2);

        await h.Clock.WhenArmedTimersAsync(static armed => armed == 1, Wait);

        h.Clock.Advance(LongRestSweepHostedService.ParkedRescanInterval);

        await h.Store.Call(3);
    }

    [Fact]
    public async Task A_due_time_beyond_the_interval_is_capped_and_one_already_past_is_floored_at_one_second()
    {
        await using Harness h = new();

        h.Store.Script(new LongRestRunnableSweep(null, h.Clock.GetUtcNow().AddMinutes(10)), new LongRestRunnableSweep(null, h.Clock.GetUtcNow().AddSeconds(-5)));

        await h.StartAsync();

        await h.Store.Call(1);

        await h.Clock.WhenArmedTimersAsync(static armed => armed == 1, Wait);

        Assert.Equal(LongRestSweepHostedService.ParkedRescanInterval, h.Clock.NextDueIn);

        h.Clock.Advance(LongRestSweepHostedService.ParkedRescanInterval);

        await h.Store.Call(2);

        await h.Clock.WhenArmedTimersAsync(static armed => armed == 1, Wait);

        // The second report is already overdue: the worker still sleeps a floor so a refused candidate cannot busy-loop it.
        Assert.Equal(LongRestSweepHostedService.MinimumRescanDelay, h.Clock.NextDueIn);
    }

    [Fact]
    public async Task A_queue_hint_wakes_the_worker_at_once_and_one_read_stays_pending_across_timer_wakes()
    {
        await using Harness h = new();

        await h.StartAsync();

        await h.Store.Call(1);

        await h.Clock.WhenArmedTimersAsync(static armed => armed == 1, Wait);

        // Two timer wakes first: the single-reader hint channel must not have lost or duplicated its pending read.
        h.Clock.Advance(LongRestSweepHostedService.ParkedRescanInterval);

        await h.Store.Call(2);

        await h.Clock.WhenArmedTimersAsync(static armed => armed == 1, Wait);

        h.Clock.Advance(LongRestSweepHostedService.ParkedRescanInterval);

        await h.Store.Call(3);

        await h.Clock.WhenArmedTimersAsync(static armed => armed == 1, Wait);

        // No time passes: only the hint can start the next pass.
        h.Queue.Signal(Guid.NewGuid());

        await h.Store.Call(4);

        await h.Clock.WhenArmedTimersAsync(static armed => armed == 1, Wait);

        h.Queue.Signal(Guid.NewGuid());

        await h.Store.Call(5);
    }

    [Fact]
    public async Task Stopping_ends_the_loop_and_leaves_no_timer_armed()
    {
        await using Harness h = new();

        await h.StartAsync();

        await h.Store.Call(1);

        await h.Clock.WhenArmedTimersAsync(static armed => armed == 1, Wait);

        await h.StopAsync();

        Assert.True(h.Service.ExecuteTask!.IsCompleted);

        Assert.Equal(0, h.Clock.ArmedTimers);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly LongRestConfigurationEpoch _epoch;

        private readonly GrimoireConnectionAdmissionGate _gate = new(TimeProvider.System);

        private bool _started;

        internal Harness()
        {
            _epoch = new(new LongRestConfigurationEpochTests.NotifyingOptions(LongRestConfigurationEpochTests.Settings()), Guid.NewGuid());

            Store = new();

            Sweep = new();

            Service = new(new Scopes(Store, Sweep), Queue, _epoch, _gate, Clock, NullLoggerFactory.Instance);
        }

        internal ManualWallClockTimeProvider Clock { get; } = new();

        internal LongRestSweepQueue Queue { get; } = new();

        internal ScriptedStore Store { get; }

        internal RecordingSweep Sweep { get; }

        internal LongRestSweepHostedService Service { get; }

        internal Guid Operation { get; } = Guid.NewGuid();

        internal async Task StartAsync()
        {
            _started = true;

            await Service.StartAsync(CancellationToken.None);
        }

        internal async Task StopAsync()
        {
            if (!_started) return;

            _started = false;

            using CancellationTokenSource deadline = new(Wait);

            await Service.StopAsync(deadline.Token);

            if (Service.ExecuteTask is { } execution) await execution.WaitAsync(deadline.Token);
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();

            Service.Dispose();

            _epoch.Dispose();
        }
    }

    private sealed class Scopes(ScriptedStore store, RecordingSweep sweep) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(store, sweep);
    }

    private sealed class Scope(ScriptedStore store, RecordingSweep sweep) : IServiceScope, IServiceProvider, IAsyncDisposable
    {
        public IServiceProvider ServiceProvider => this;

        public object? GetService(Type serviceType) =>
            serviceType == typeof(ILongRestSweepStore) ? store : serviceType == typeof(ILongRestAdmittedSweep) ? sweep : null;

        public void Dispose() => throw new InvalidOperationException("Async scope disposal is required.");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Answers each discovery with the next scripted result (default: nothing runnable, nothing cooling).</summary>
    private sealed class ScriptedStore : ILongRestSweepStore
    {
        private readonly Queue<LongRestRunnableSweep> _script = new();

        private readonly List<TaskCompletionSource> _calls = [];

        private readonly object _gate = new();

        internal int Calls
        {
            get
            {
                lock (_gate)
                {
                    return _calls.Count(static call => call.Task.IsCompleted);
                }
            }
        }

        internal void Script(params LongRestRunnableSweep[] results)
        {
            lock (_gate)
            {
                foreach (LongRestRunnableSweep result in results) _script.Enqueue(result);
            }
        }

        /// <summary>Completes when the n-th discovery has been asked.</summary>
        internal Task Call(int ordinal)
        {
            lock (_gate)
            {
                while (_calls.Count < ordinal) _calls.Add(new(TaskCreationOptions.RunContinuationsAsynchronously));

                return _calls[ordinal - 1].Task.WaitAsync(Wait);
            }
        }

        public Task<Result<LongRestRunnableSweep>> FindNextRunnableCleanAsync(LongRestConfigurationStamp configuration, Guid? afterOperationId, CancellationToken ct)
        {
            LongRestRunnableSweep answer;

            TaskCompletionSource observed;

            lock (_gate)
            {
                int ordinal = _calls.Count(static call => call.Task.IsCompleted) + 1;

                while (_calls.Count < ordinal) _calls.Add(new(TaskCreationOptions.RunContinuationsAsynchronously));

                observed = _calls[ordinal - 1];

                answer = _script.Count > 0 ? _script.Dequeue() : new(null, null);
            }

            observed.TrySetResult();

            return Task.FromResult(Result<LongRestRunnableSweep>.Success(answer));
        }

        public Task<Result<LongRestSweep>> BeginOrResumeAsync(Guid operationId, LongRestSweepScope scope, LongRestConfigurationStamp configuration, CancellationToken ct) => throw new NotSupportedException();

        public Task<Result<Guid?>> FindCompletedReplayAsync(CanonicalCampaignContext scope, LongRestSweepScope partition, LongRestRecipeIdentity recipe, CancellationToken ct) => throw new NotSupportedException();

        public Task<Result<Guid?>> FindCompletedReplayAsync(CanonicalCampaignContext scope, LongRestRecipeIdentity recipe, CancellationToken ct) => throw new NotSupportedException();

        public Task<Result<LongRestSweepWork?>> ReadNextWorkAsync(LongRestSweep expected, LongRestMaintenanceCapability? capability, CancellationToken ct) => throw new NotSupportedException();

        public Task<Result<LongRestAdjudication>> CommitAcceptedAdjudicationAsync(LongRestSweep expected, LongRestAdjudication accepted, LongRestMaintenanceCapability? capability, CancellationToken ct) => throw new NotSupportedException();

        public Task<Result<LongRestAdjudication>> CommitAcceptedAdjudicationAsync(LongRestSweep expected, LongRestAdjudication accepted, LongRestDispatchCompletion completion, string workerOwner, LongRestMaintenanceCapability? capability, CancellationToken ct, bool committedOnly = false) => throw new NotSupportedException();

        public Task<Result<LongRestSweep>> CommitExactDuplicatesAsync(LongRestSweep expected, LongRestWorkIdentity work, LongRestMaintenanceCapability? capability, CancellationToken ct) => throw new NotSupportedException();

        public Task<Result<LongRestSweep>> CommitDecisionAsync(LongRestSweep expected, LongRestAdjudication decision, LongRestMaintenanceCapability? capability, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class RecordingSweep : ILongRestAdmittedSweep
    {
        internal List<Guid> Resumed { get; } = [];

        public Task<Result<LongRestSweepProgress>> ResumeAdmittedAsync(Guid operationId, IGrimoireWorkLease admission, CancellationToken ct)
        {
            Resumed.Add(operationId);

            return Task.FromResult(Result<LongRestSweepProgress>.Success(new(operationId, Guid.NewGuid(), LongRestSweepScopeKind.Global, null,
                LongRestSweepPhase.Completed, 0, null, null)));
        }
    }
}
