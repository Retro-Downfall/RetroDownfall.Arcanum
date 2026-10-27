using System.Diagnostics;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;

namespace RetroDownfall.Arcanum.Tests.Weave.Tapestry;

public sealed partial class TapestryRecursiveScaleProbe
{
    [Fact]
    public async Task Consumer_budget_links_caller_cancellation_after_leaf_embedding_starts()
    {
        await SeedAsync(24, CancellationToken.None);

        using CancellationTokenSource caller = new();

        using ConsumerBudget budget = new(30, 4L * 1024 * 1024 * 1024, caller.Token);

        _embedding.BeforeBatch = token =>
        {
            caller.Cancel();

            token.ThrowIfCancellationRequested();
        };

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BuildAsync(budget.Token));

        Assert.Equal("caller", budget.Reason);

        Assert.Equal(0, _summary.Calls);

        Assert.Null(await _store.GetCurrentGenerationAsync(Scope, CancellationToken.None));
    }

    [Fact]
    public async Task Consumer_budget_phase_expiry_reaches_the_real_weaver()
    {
        await SeedAsync(24, CancellationToken.None);

        using ConsumerBudget budget = new(0.02, 4L * 1024 * 1024 * 1024, CancellationToken.None);

        _embedding.BeforeBatch = token => Assert.True(token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)), "The phase token must expire while the provider boundary is held.");

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BuildAsync(budget.Token));

        Assert.Equal("time", budget.Reason);

        Assert.Equal(0, _summary.Calls);

        Assert.Null(await _store.GetCurrentGenerationAsync(Scope, CancellationToken.None));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(6)]
    public async Task Consumer_budget_keeps_its_exact_memory_boundary_then_refuses_an_observed_overage(int gibibytes)
    {
        await SeedAsync(24, CancellationToken.None);

        long ceiling = (long)gibibytes * 1024 * 1024 * 1024;

        using ConsumerBudget budget = new(30, ceiling, CancellationToken.None);

        budget.ObserveResources(ceiling, 0);

        Assert.Equal(TapestryWeaveStatus.Woven, (await BuildAsync(budget.Token)).Status);

        budget.ObserveResources(ceiling + 1, 0);

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BuildAsync(budget.Token));

        Assert.Equal("memory", budget.Reason);
    }

    private interface IConsumerResourceReader : IDisposable
    {
        long ReadWorkingSet();
    }

    private sealed class ProcessResourceReader : IConsumerResourceReader
    {
        private readonly System.Diagnostics.Process _process = System.Diagnostics.Process.GetCurrentProcess();

        public long ReadWorkingSet()
        {
            _process.Refresh();

            return _process.WorkingSet64;
        }

        public void Dispose() => _process.Dispose();
    }

    private sealed class ConsumerBudget : IDisposable
    {
        private readonly CancellationTokenSource _source = new();

        private readonly IConsumerResourceReader _reader;

        private readonly Timer _timer;

        private readonly CancellationTokenRegistration _caller;

        private readonly double _seconds;

        private readonly long _memoryCeiling;

        private readonly long _started = Stopwatch.GetTimestamp();

        private long _peak;

        private long _canceled;

        private string _reason = "none";

        private int _callbackFailed = 0;

        private int _sampling;

        internal ConsumerBudget(double seconds, long memoryCeiling, CancellationToken caller)
            : this(seconds, memoryCeiling, caller, new ProcessResourceReader(), true)
        {
        }

        internal ConsumerBudget(double seconds, long memoryCeiling, CancellationToken caller, IConsumerResourceReader reader, bool startTimer)
        {
            _reader = reader;

            _seconds = seconds;

            _memoryCeiling = memoryCeiling;

            try
            {
                _peak = StartWorkingSet = _reader.ReadWorkingSet();
            }
            catch (Exception observationFailure)
            {
                try
                {
                    _reader.Dispose();
                }
                catch (Exception cleanupFailure)
                {
                    throw new AggregateException("Resource observation and initialization cleanup both failed.", observationFailure, cleanupFailure);
                }
                finally
                {
                    _source.Dispose();
                }

                throw;
            }

            _caller = caller.Register(() => Cancel("caller"));

            _timer = new Timer(_ => SampleResources(), null,
                startTimer ? TimeSpan.Zero : Timeout.InfiniteTimeSpan, TimeSpan.FromMilliseconds(10));
        }

        internal void SampleResources()
        {
            if (Interlocked.CompareExchange(ref _sampling, 1, 0) != 0)
            {
                // A slow observation must not queue overlapping Process reads or suppress the clock deadline.
                if (Stopwatch.GetElapsedTime(_started).TotalSeconds >= _seconds) Cancel("time");

                return;
            }

            try
            {
                long current;

                try
                {
                    current = _reader.ReadWorkingSet();
                }
                catch (Exception)
                {
                    Cancel("resource-unavailable");

                    return;
                }

                long previous;

                do
                {
                    previous = Interlocked.Read(ref _peak);
                }

                while (current > previous && Interlocked.CompareExchange(ref _peak, current, previous) != previous);

                ObserveResources(current, Stopwatch.GetElapsedTime(_started).TotalSeconds);
            }
            finally
            {
                Volatile.Write(ref _sampling, 0);
            }
        }

        internal CancellationToken Token => _source.Token;

        internal string Reason => Volatile.Read(ref _reason);

        internal bool CallbackFailed => Volatile.Read(ref _callbackFailed) != 0;

        internal long StartWorkingSet { get; }

        internal long PeakWorkingSet => Interlocked.Read(ref _peak);

        internal double CancelLatencyMilliseconds => Interlocked.Read(ref _canceled) is long stamp && stamp != 0
            ? Stopwatch.GetElapsedTime(stamp).TotalMilliseconds : 0;

        internal void ObserveResources(long current, double elapsedSeconds)
        {
            if (current > _memoryCeiling) Cancel("memory");

            else if (elapsedSeconds >= _seconds) Cancel("time");
        }

        private void Cancel(string reason)
        {
            if (Interlocked.CompareExchange(ref _canceled, Stopwatch.GetTimestamp(), 0) != 0) return;

            Volatile.Write(ref _reason, reason);

            try
            {
                _source.Cancel();
            }
            catch (AggregateException)
            {
                // The token is already canceled; retain containment and its first cause without escaping the timer.
                Volatile.Write(ref _callbackFailed, 1);
            }
        }

        public void Dispose()
        {
            _timer.DisposeAsync().AsTask().GetAwaiter().GetResult();

            _caller.Dispose();

            _reader.Dispose();

            _source.Dispose();
        }
    }
}
