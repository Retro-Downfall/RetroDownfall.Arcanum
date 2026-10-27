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

    private sealed class ConsumerBudget : IDisposable
    {
        private readonly CancellationTokenSource _source = new();

        private readonly System.Diagnostics.Process _process = System.Diagnostics.Process.GetCurrentProcess();

        private readonly Timer _timer;

        private readonly CancellationTokenRegistration _caller;

        private readonly double _seconds;

        private readonly long _memoryCeiling;

        private readonly long _started = Stopwatch.GetTimestamp();

        private long _peak;

        private long _canceled;

        private string _reason = "none";

        internal ConsumerBudget(double seconds, long memoryCeiling, CancellationToken caller)
        {
            _seconds = seconds;

            _memoryCeiling = memoryCeiling;

            _process.Refresh();

            _peak = StartWorkingSet = _process.WorkingSet64;

            _caller = caller.Register(() => Cancel("caller"));

            _timer = new Timer(_ =>
            {
                _process.Refresh();

                long current = _process.WorkingSet64;

                long previous;

                do
                {
                    previous = Interlocked.Read(ref _peak);
                }

                while (current > previous && Interlocked.CompareExchange(ref _peak, current, previous) != previous);

                ObserveResources(current, Stopwatch.GetElapsedTime(_started).TotalSeconds);
            }, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(10));
        }

        internal CancellationToken Token => _source.Token;

        internal string Reason => Volatile.Read(ref _reason);

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

            _source.Cancel();
        }

        public void Dispose()
        {
            _timer.DisposeAsync().AsTask().GetAwaiter().GetResult();

            _caller.Dispose();

            _process.Dispose();

            _source.Dispose();
        }
    }
}
