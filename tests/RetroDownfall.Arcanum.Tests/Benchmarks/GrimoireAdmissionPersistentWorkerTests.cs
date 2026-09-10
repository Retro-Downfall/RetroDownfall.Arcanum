using System.Collections.Concurrent;

using System.Reflection;

using RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Benchmarks;

public sealed class GrimoireAdmissionPersistentWorkerTests
{
    [Fact]
    public void Cell_runner_brackets_warmup_latency_and_throughput_callback_materialization()
    {
        using PersistentWorkerHarness harness = new(4);

        long callbacks = 0;

        AdmissionBenchmarkProfile profile = new(
            "test",
            3,
            4,
            2,
            5,
            10,
            ["request.finite"]);

        AdmissionBenchmarkCellMeasurement measurement = AdmissionBenchmarkCellRunner.Run(
            harness,
            profile,
            2,
            (int worker, int iteration, CancellationToken token, ref long checksum) =>
            {
                Interlocked.Increment(ref callbacks);

                return true;
            },
            () => Interlocked.Read(ref callbacks),
            CancellationToken.None);

        Assert.Equal(3, measurement.Warmup.OperationCount);

        Assert.Equal(2, measurement.Latency.BundleNanosecondsPerOperation.Length);

        Assert.Equal(8, measurement.Latency.OperationCount);

        Assert.Equal(5, measurement.Throughput.OperationCount);

        Assert.Equal(16, measurement.MaterializedTerminalCallbackDelta);
    }

    [Fact]
    public void Phase_iteration_count_is_one_exact_cell_total_not_a_per_worker_multiplier()
    {
        using PersistentWorkerHarness harness = new(3);

        AdmissionBenchmarkPhaseResult result = harness.Run(
            new(
                AdmissionBenchmarkPhaseKind.Throughput,
                10,
                1,
                static (int worker, int iteration, CancellationToken _, ref long checksum) =>
                {
                    checksum = checked(checksum + worker + iteration + 1);

                    return true;
                }),
            CancellationToken.None);

        Assert.Equal(10, result.OperationCount);

        Assert.Equal(10, result.SuccessCount);
    }

    [Fact]
    public void Latency_sample_count_is_partitioned_exactly_before_bundle_expansion()
    {
        using PersistentWorkerHarness harness = new(3);

        AdmissionBenchmarkPhaseResult result = harness.Run(
            new(
                AdmissionBenchmarkPhaseKind.Latency,
                5,
                4,
                static (int worker, int iteration, CancellationToken _, ref long checksum) => true),
            CancellationToken.None);

        Assert.Equal(5, result.BundleNanosecondsPerOperation.Length);

        Assert.Equal(20, result.OperationCount);
    }

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

        Assert.Equal(7, warmup.OperationCount);

        Assert.Equal(11, measured.OperationCount);

        Assert.Equal(11, measured.SuccessCount);

        Assert.Equal(0, measured.FailureCount);

        Assert.Equal(36, measured.Checksum);

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
    public void Probe_replays_the_actual_asymmetric_worker_completion_order()
    {
        OrderedProbe probe = new();

        using PersistentWorkerHarness harness = new(2, probe);

        probe.AllWorkersParked = () => harness.AllWorkersParked;

        long[] completionStamps = (long[])typeof(PersistentWorkerHarness)
            .GetField("_workerCompletedStamp", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(harness)!;

        using ManualResetEventSlim workerOneCallbackReturned = new();

        _ = harness.Run(
            new(
                AdmissionBenchmarkPhaseKind.Throughput,
                2,
                1,
                (int worker, int iteration, CancellationToken _, ref long checksum) =>
                {
                    if (worker == 0)
                    {
                        workerOneCallbackReturned.Wait();

                        Assert.True(
                            SpinWait.SpinUntil(
                                () => Volatile.Read(ref completionStamps[1]) > 0,
                                TimeSpan.FromSeconds(5)),
                            "Worker one did not publish its completion stamp.");
                    }
                    else
                    {
                        workerOneCallbackReturned.Set();
                    }

                    return true;
                }),
            CancellationToken.None);

        Assert.Equal([1, 0], probe.WorkerCompletionOrder);
    }

    [Fact]
    public void Early_completer_and_controller_block_instead_of_consuming_cpu_until_release()
    {
        using PersistentWorkerHarness harness = new(2);

        Thread[] workers = (Thread[])typeof(PersistentWorkerHarness)
            .GetField("_threads", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(harness)!;

        using ManualResetEventSlim earlyWorkerCompleted = new();

        using ManualResetEventSlim releaseSlowWorker = new();

        Exception? error = null;

        Thread controller = new(
            () =>
            {
                try
                {
                    _ = harness.Run(
                        new(
                            AdmissionBenchmarkPhaseKind.Throughput,
                            2,
                            1,
                            (int worker, int iteration, CancellationToken cancellationToken, ref long checksum) =>
                            {
                                if (worker == 0)
                                {
                                    releaseSlowWorker.Wait(cancellationToken);
                                }
                                else
                                {
                                    earlyWorkerCompleted.Set();
                                }

                                return true;
                            }),
                        CancellationToken.None);
                }
                catch (Exception exception)
                {
                    error = exception;
                }
            });

        controller.Start();

        Assert.True(earlyWorkerCompleted.Wait(TimeSpan.FromSeconds(5)));

        try
        {
            Assert.True(
                SpinWait.SpinUntil(
                    () => workers[1].ThreadState.HasFlag(ThreadState.WaitSleepJoin)
                        && controller.ThreadState.HasFlag(ThreadState.WaitSleepJoin),
                    TimeSpan.FromSeconds(1)),
                $"Expected blocked threads; worker={workers[1].ThreadState}, controller={controller.ThreadState}.");
        }
        finally
        {
            releaseSlowWorker.Set();

            Assert.True(controller.Join(TimeSpan.FromSeconds(5)));
        }

        Assert.Null(error);
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
                        if (worker == 1 && iteration == 1)
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

    [Fact]
    public async Task Teardown_attempts_every_stage_after_pre_drain_failure()
    {
        List<string> calls = [];

        AdmissionBenchmarkTeardownWitness witness = await AdmissionBenchmarkTeardownCoordinator.RunAsync(
            runtimeResourcesCreated: true,
            _ =>
            {
                calls.Add("pre-drain");

                throw new InvalidDataException("pre-drain break");
            },
            () =>
            {
                calls.Add("provider");

                return ValueTask.CompletedTask;
            },
            _ =>
            {
                calls.Add("final-drain");

                return ValueTask.CompletedTask;
            },
            () => calls.Add("pools"),
            CancellationToken.None);

        Assert.Equal(["pre-drain", "provider", "final-drain", "pools"], calls);

        Assert.False(witness.PreDrainSucceeded);

        Assert.True(witness.ProviderDisposed);

        Assert.True(witness.FinalDrainSucceeded);

        Assert.True(witness.PoolsCleared);

        Assert.True(witness.HomeDeletionAuthorized);

        Assert.Contains(witness.Errors, static error => error.Contains("pre-drain break", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Teardown_bounds_provider_disposal_and_retains_the_home_without_proof()
    {
        TaskCompletionSource providerNeverCompletes = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        List<string> calls = [];

        using CancellationTokenSource cleanupCancellation = new();

        cleanupCancellation.Cancel();

        AdmissionBenchmarkTeardownWitness witness = await AdmissionBenchmarkTeardownCoordinator.RunAsync(
            runtimeResourcesCreated: true,
            _ => ValueTask.CompletedTask,
            () => new(providerNeverCompletes.Task),
            _ =>
            {
                calls.Add("final-drain");

                return ValueTask.CompletedTask;
            },
            () => calls.Add("pools"),
            cleanupCancellation.Token);

        Assert.Equal(["final-drain", "pools"], calls);

        Assert.False(witness.ProviderDisposed);

        Assert.True(witness.FinalDrainSucceeded);

        Assert.True(witness.PoolsCleared);

        Assert.False(witness.HomeDeletionAuthorized);
    }

    [Fact]
    public async Task Teardown_without_created_runtime_resources_authorizes_owned_home_cleanup()
    {
        using CancellationTokenSource cleanupCancellation = new();

        cleanupCancellation.Cancel();

        AdmissionBenchmarkTeardownWitness witness = await AdmissionBenchmarkTeardownCoordinator.RunAsync(
            runtimeResourcesCreated: false,
            _ => ValueTask.FromException(new InvalidOperationException("pre")),
            () => ValueTask.FromException(new InvalidOperationException("provider")),
            _ => ValueTask.FromException(new InvalidOperationException("final")),
            () => throw new InvalidOperationException("pools"),
            cleanupCancellation.Token);

        Assert.True(witness.HomeDeletionAuthorized);

        Assert.Equal(4, witness.Errors.Length);
    }

    [Fact]
    public async Task Final_state_is_sampled_only_after_measured_resources_are_disposed()
    {
        List<string> calls = [];

        bool disposed = false;

        int finalState = await AdmissionBenchmarkLifecycleCoordinator.DisposeThenSampleAsync(
            () =>
            {
                calls.Add("dispose");

                disposed = true;

                return new(true, true);
            },
            _ =>
            {
                Assert.True(disposed);

                calls.Add("sample");

                return ValueTask.FromResult(42);
            },
            CancellationToken.None);

        Assert.Equal(42, finalState);

        Assert.Equal(["dispose", "sample"], calls);
    }

    [Fact]
    public async Task Final_state_is_not_sampled_without_positive_worker_termination_witness()
    {
        List<string> calls = [];

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await AdmissionBenchmarkLifecycleCoordinator.DisposeThenSampleAsync(
                () =>
                {
                    calls.Add("dispose");

                    return new(false, true);
                },
                _ =>
                {
                    calls.Add("sample");

                    return ValueTask.FromResult(42);
                },
                CancellationToken.None));

        Assert.Equal(["dispose"], calls);
    }

    [Fact]
    public void Missed_worker_join_keeps_reachable_handles_alive_until_the_worker_terminates()
    {
        PersistentWorkerHarness harness = new(1);

        Thread[] workers = (Thread[])typeof(PersistentWorkerHarness)
            .GetField("_threads", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(harness)!;

        AutoResetEvent[] wake = (AutoResetEvent[])typeof(PersistentWorkerHarness)
            .GetField("_wake", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(harness)!;

        EventWaitHandle[] sharedHandles =
        [
            (EventWaitHandle)typeof(PersistentWorkerHarness)
                .GetField("_allArmed", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(harness)!,
            (EventWaitHandle)typeof(PersistentWorkerHarness)
                .GetField("_allCompleted", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(harness)!,
            (EventWaitHandle)typeof(PersistentWorkerHarness)
                .GetField("_allParked", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(harness)!,
            (EventWaitHandle)typeof(PersistentWorkerHarness)
                .GetField("_parkRelease", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(harness)!,
        ];

        using ManualResetEventSlim operationEntered = new();

        using ManualResetEventSlim releaseOperation = new();

        using CancellationTokenSource cancellation = new();

        Exception? controllerError = null;

        Thread controller = new(
            () =>
            {
                try
                {
                    _ = harness.Run(
                        new(
                            AdmissionBenchmarkPhaseKind.Throughput,
                            1,
                            1,
                            (int worker, int iteration, CancellationToken token, ref long checksum) =>
                            {
                                operationEntered.Set();

                                releaseOperation.Wait();

                                return true;
                            }),
                        cancellation.Token);
                }
                catch (Exception exception)
                {
                    controllerError = exception;
                }
            });

        controller.Start();

        Assert.True(operationEntered.Wait(TimeSpan.FromSeconds(5)));

        cancellation.Cancel();

        Assert.True(controller.Join(TimeSpan.FromSeconds(5)));

        Assert.IsAssignableFrom<OperationCanceledException>(controllerError);

        try
        {
            Assert.Throws<InvalidOperationException>(harness.Dispose);

            Assert.All(wake, static signal => Assert.False(signal.SafeWaitHandle.IsClosed));

            Assert.All(sharedHandles, static signal => Assert.False(signal.SafeWaitHandle.IsClosed));

            AdmissionBenchmarkMeasuredResourceWitness incomplete = new(
                harness.WorkerTerminationSucceeded,
                true);

            Assert.False(incomplete.HomeDeletionAuthorized);
        }
        finally
        {
            releaseOperation.Set();
        }

        Assert.True(workers[0].Join(TimeSpan.FromSeconds(5)));

        harness.Dispose();

        AdmissionBenchmarkMeasuredResourceWitness complete = new(
            harness.WorkerTerminationSucceeded,
            true);

        Assert.True(complete.HomeDeletionAuthorized);

        Assert.All(wake, static signal => Assert.True(signal.SafeWaitHandle.IsClosed));

        Assert.All(sharedHandles, static signal => Assert.True(signal.SafeWaitHandle.IsClosed));
    }

    [Fact]
    public void Worker_teardown_action_failures_do_not_suppress_later_join_or_handle_cleanup()
    {
        List<string> calls = [];

        Exception[] errors = AdmissionBenchmarkWorkerTeardown.AttemptAll(
        [
            () =>
            {
                calls.Add("join-one");

                throw new InvalidOperationException("join break");
            },
            () => calls.Add("join-two"),
            () =>
            {
                calls.Add("handle-one");

                throw new InvalidOperationException("handle break");
            },
            () => calls.Add("handle-two"),
        ]);

        Assert.Equal(["join-one", "join-two", "handle-one", "handle-two"], calls);

        Assert.Equal(2, errors.Length);

        Assert.Contains(errors, static error => error.Message == "join break");

        Assert.Contains(errors, static error => error.Message == "handle break");
    }

    private sealed class OrderedProbe : IAdmissionBenchmarkOrderProbe
    {
        private readonly ConcurrentQueue<(int Phase, int Worker, AdmissionBenchmarkProbeEvent Event)> _events = new();

        private int _phase;

        internal Func<bool>? AllWorkersParked { get; set; }

        internal int CompletedPhases => Volatile.Read(ref _phase);

        internal int[] WorkerCompletionOrder => _events
            .Where(static item => item.Event == AdmissionBenchmarkProbeEvent.WorkerLoopCompleted)
            .Select(static item => item.Worker)
            .ToArray();

        public void Record(int phase, int worker, AdmissionBenchmarkProbeEvent value)
        {
            Assert.True(AllWorkersParked?.Invoke());

            _events.Enqueue((phase, worker, value));

            if (value == AdmissionBenchmarkProbeEvent.ProcessTerminal)
            {
                Interlocked.Exchange(ref _phase, phase);
            }
        }

        internal void AssertEveryPhaseHasMeasuredWindowOrder()
        {
            foreach (IGrouping<int, (int Phase, int Worker, AdmissionBenchmarkProbeEvent Event)> group in _events.GroupBy(static item => item.Phase))
            {
                (int Phase, int Worker, AdmissionBenchmarkProbeEvent Event)[] events = group.ToArray();

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
