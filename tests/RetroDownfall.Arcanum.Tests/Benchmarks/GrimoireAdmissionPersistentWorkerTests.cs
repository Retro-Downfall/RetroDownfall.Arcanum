using System.Collections.Concurrent;

using RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Benchmarks;

public sealed class GrimoireAdmissionPersistentWorkerTests
{

    [Fact]
    public void Workers_are_persistent_and_phase_accounting_is_exact()
    {

        OrderedProbe probe = new();

        using PersistentWorkerHarness harness = new(3, probe);

        probe.AllWorkersParked = () => harness.AllWorkersParked;

        AdmissionBenchmarkPhaseResult warmup = harness.Run(
            new(
                AdmissionBenchmarkPhaseKind.Warmup,
                7,
                1,
                static (int worker, int iteration, CancellationToken _, ref long checksum) =>
                {

                    checksum = checked(checksum + worker + iteration + 1);

                    return true;

                }),
            CancellationToken.None);

        int[] firstIds = harness.ManagedThreadIds;

        AdmissionBenchmarkPhaseResult measured = harness.Run(
            new(
                AdmissionBenchmarkPhaseKind.Throughput,
                11,
                1,
                static (int worker, int iteration, CancellationToken _, ref long checksum) =>
                {

                    checksum = checked(checksum + worker + iteration + 1);

                    return true;

                }),
            CancellationToken.None);

        Assert.Equal(3, harness.ThreadsCreated);

        Assert.Equal(firstIds, harness.ManagedThreadIds);

        Assert.Equal(21, warmup.OperationCount);

        Assert.Equal(33, measured.OperationCount);

        Assert.Equal(33, measured.SuccessCount);

        Assert.Equal(0, measured.FailureCount);

        Assert.Equal(231, measured.Checksum);

        Assert.True(measured.MaximumWorkerEndTimestamp >= measured.MinimumWorkerStartTimestamp);

        Assert.Equal(2, probe.CompletedPhases);

        probe.AssertEveryPhaseHasMeasuredWindowOrder();

    }

    [Fact]
    public void Mixed_schedule_is_stable_for_worker_and_iteration()
    {

        string[] schedule = ["a", "b", "c", "d"];

        Assert.Equal("a", PersistentWorkerHarness.ScheduledOperation(schedule, 0, 0));

        Assert.Equal("b", PersistentWorkerHarness.ScheduledOperation(schedule, 0, 1));

        Assert.Equal("b", PersistentWorkerHarness.ScheduledOperation(schedule, 1, 0));

        Assert.Equal("a", PersistentWorkerHarness.ScheduledOperation(schedule, 3, 1));

    }

    [Fact]
    public void Worker_exception_is_reported_after_every_worker_reaches_a_safe_boundary()
    {

        using PersistentWorkerHarness harness = new(2);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => harness.Run(
                new(
                    AdmissionBenchmarkPhaseKind.Throughput,
                    4,
                    1,
                    static (int worker, int iteration, CancellationToken _, ref long checksum) =>
                    {

                        if (worker == 1 && iteration == 2)
                        {

                            throw new InvalidOperationException("named worker break");

                        }

                        return true;

                    }),
                CancellationToken.None));

        Assert.Equal("named worker break", exception.Message);

        Assert.True(harness.AllWorkersParked);

    }

    [Fact]
    public void Cancellation_is_observed_before_a_phase_is_armed()
    {

        using PersistentWorkerHarness harness = new(2);

        using CancellationTokenSource cancellation = new();

        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => harness.Run(
                new(
                    AdmissionBenchmarkPhaseKind.Throughput,
                    1,
                    1,
                    static (int worker, int iteration, CancellationToken token, ref long checksum) => true),
                cancellation.Token));

        Assert.True(harness.AllWorkersParked);

    }

    private sealed class OrderedProbe : IAdmissionBenchmarkOrderProbe
    {

        private readonly ConcurrentQueue<(int Phase, AdmissionBenchmarkProbeEvent Event)> _events = new();

        private int _phase;

        internal Func<bool>? AllWorkersParked { get; set; }

        internal int CompletedPhases => Volatile.Read(ref _phase);

        public void Record(int phase, int worker, AdmissionBenchmarkProbeEvent value)
        {

            Assert.True(AllWorkersParked?.Invoke());

            _events.Enqueue((phase, value));

            if (value == AdmissionBenchmarkProbeEvent.ProcessTerminal)
            {

                Interlocked.Exchange(ref _phase, phase);

            }

        }

        internal void AssertEveryPhaseHasMeasuredWindowOrder()
        {

            foreach (IGrouping<int, (int Phase, AdmissionBenchmarkProbeEvent Event)> group in _events.GroupBy(static item => item.Phase))
            {

                (int Phase, AdmissionBenchmarkProbeEvent Event)[] events = group.ToArray();

                int baseline = Array.FindIndex(events, static item => item.Event == AdmissionBenchmarkProbeEvent.ProcessBaseline);

                int run = Array.FindIndex(events, static item => item.Event == AdmissionBenchmarkProbeEvent.RunPublished);

                int terminal = Array.FindIndex(events, static item => item.Event == AdmissionBenchmarkProbeEvent.ProcessTerminal);

                int firstLoop = Array.FindIndex(events, static item => item.Event == AdmissionBenchmarkProbeEvent.WorkerLoopStarted);

                int lastComplete = Array.FindLastIndex(events, static item => item.Event == AdmissionBenchmarkProbeEvent.WorkerLoopCompleted);

                int firstPark = Array.FindIndex(events, static item => item.Event == AdmissionBenchmarkProbeEvent.WorkerParked);

                Assert.True(baseline >= 0 && baseline < run);

                Assert.True(run < firstLoop);

                Assert.True(lastComplete < terminal);

                Assert.True(terminal < firstPark);

            }

        }

    }

}
