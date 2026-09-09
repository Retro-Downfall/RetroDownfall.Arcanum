using System.Diagnostics;

namespace RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks;

internal enum AdmissionBenchmarkPhaseKind : byte
{
    Warmup = 1,

    Latency = 2,

    Throughput = 3,
}

internal enum AdmissionBenchmarkProbeEvent : byte
{
    WorkerArmed = 1,

    ProcessBaseline = 2,

    RunPublished = 3,

    WorkerLoopStarted = 4,

    WorkerLoopCompleted = 5,

    ProcessTerminal = 6,

    WorkerParked = 7,
}

internal interface IAdmissionBenchmarkOrderProbe
{
    void Record(int phase, int worker, AdmissionBenchmarkProbeEvent value);
}

internal delegate bool AdmissionBenchmarkOperationDelegate(
    int workerIndex,
    int iteration,
    CancellationToken cancellationToken,
    ref long checksum);

internal sealed record AdmissionBenchmarkPhaseCommand(
    AdmissionBenchmarkPhaseKind Kind,
    int TotalUnits,
    int BundleSize,
    AdmissionBenchmarkOperationDelegate Operation)
{
    internal int ActiveWorkerCount { get; init; }
}

internal sealed record AdmissionBenchmarkPhaseResult(
    long OperationCount,
    long SuccessCount,
    long FailureCount,
    long Checksum,
    long MinimumWorkerStartTimestamp,
    long MaximumWorkerEndTimestamp,
    long AllocatedBytes,
    int Gen0Collections,
    long LockContentions,
    double[] BundleNanosecondsPerOperation);

internal sealed record AdmissionBenchmarkCellMeasurement(
    AdmissionBenchmarkPhaseResult Warmup,
    AdmissionBenchmarkPhaseResult Latency,
    AdmissionBenchmarkPhaseResult Throughput,
    long MaterializedTerminalCallbackDelta);

internal sealed record AdmissionBenchmarkMeasuredResourceWitness(
    bool WorkerThreadsTerminated,
    bool WorkerConnectionsDisposed)
{
    internal bool HomeDeletionAuthorized => WorkerThreadsTerminated && WorkerConnectionsDisposed;
}

internal sealed record AdmissionBenchmarkTeardownWitness(
    bool RuntimeResourcesCreated,
    bool PreDrainSucceeded,
    bool ProviderDisposed,
    bool FinalDrainSucceeded,
    bool PoolsCleared,
    string[] Errors)
{
    internal bool HomeDeletionAuthorized => !RuntimeResourcesCreated
        || ProviderDisposed && FinalDrainSucceeded && PoolsCleared;
}

internal static class AdmissionBenchmarkTeardownCoordinator
{
    internal static async ValueTask<AdmissionBenchmarkTeardownWitness> RunAsync(
        bool runtimeResourcesCreated,
        Func<CancellationToken, ValueTask> preDrain,
        Func<ValueTask> disposeProvider,
        Func<CancellationToken, ValueTask> finalDrain,
        Action clearPools,
        CancellationToken cleanupCancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preDrain);

        ArgumentNullException.ThrowIfNull(disposeProvider);

        ArgumentNullException.ThrowIfNull(finalDrain);

        ArgumentNullException.ThrowIfNull(clearPools);

        List<string> errors = [];

        bool preDrainSucceeded = await AttemptAsync(
            "pre-drain",
            () => preDrain(cleanupCancellationToken),
            cleanupCancellationToken,
            errors).ConfigureAwait(false);

        bool providerDisposed = await AttemptAsync(
            "provider disposal",
            disposeProvider,
            cleanupCancellationToken,
            errors).ConfigureAwait(false);

        bool finalDrainSucceeded = await AttemptAsync(
            "final drain",
            () => finalDrain(cleanupCancellationToken),
            cleanupCancellationToken,
            errors).ConfigureAwait(false);

        bool poolsCleared;

        try
        {
            clearPools();

            poolsCleared = true;
        }
        catch (Exception exception)
        {
            errors.Add($"pool clearing: {exception.Message}");

            poolsCleared = false;
        }

        return new(
            runtimeResourcesCreated,
            preDrainSucceeded,
            providerDisposed,
            finalDrainSucceeded,
            poolsCleared,
            errors.ToArray());
    }

    private static async ValueTask<bool> AttemptAsync(
        string name,
        Func<ValueTask> operation,
        CancellationToken cleanupCancellationToken,
        ICollection<string> errors)
    {
        try
        {
            await operation().AsTask().WaitAsync(cleanupCancellationToken).ConfigureAwait(false);

            return true;
        }
        catch (Exception exception)
        {
            errors.Add($"{name}: {exception.Message}");

            return false;
        }
    }
}

internal static class AdmissionBenchmarkLifecycleCoordinator
{
    internal static async ValueTask<T> DisposeThenSampleAsync<T>(
        Func<AdmissionBenchmarkMeasuredResourceWitness> disposeMeasuredResources,
        Func<CancellationToken, ValueTask<T>> sampleFinalState,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(disposeMeasuredResources);

        ArgumentNullException.ThrowIfNull(sampleFinalState);

        AdmissionBenchmarkMeasuredResourceWitness witness = disposeMeasuredResources();

        if (!witness.HomeDeletionAuthorized)
        {
            throw new InvalidDataException(
                "Measured-resource teardown did not establish worker termination and connection disposal.");
        }

        return await sampleFinalState(cancellationToken).ConfigureAwait(false);
    }
}

internal static class AdmissionBenchmarkWorkerTeardown
{
    internal static Exception[] AttemptAll(IEnumerable<Action> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);

        List<Exception> errors = [];

        foreach (Action action in actions)
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                errors.Add(exception);
            }
        }

        return errors.ToArray();
    }
}

internal static class AdmissionBenchmarkCellRunner
{
    internal static AdmissionBenchmarkCellMeasurement Run(
        PersistentWorkerHarness harness,
        AdmissionBenchmarkProfile profile,
        int activeWorkerCount,
        AdmissionBenchmarkOperationDelegate operation,
        Func<long> readMaterializedTerminalCallbacks,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(harness);

        ArgumentNullException.ThrowIfNull(profile);

        ArgumentNullException.ThrowIfNull(operation);

        ArgumentNullException.ThrowIfNull(readMaterializedTerminalCallbacks);

        long callbackBaseline = readMaterializedTerminalCallbacks();

        AdmissionBenchmarkPhaseResult warmup = harness.Run(
            new(
                AdmissionBenchmarkPhaseKind.Warmup,
                profile.WarmupIterations,
                1,
                operation)
            {
                ActiveWorkerCount = activeWorkerCount,
            },
            cancellationToken);

        AdmissionBenchmarkPhaseResult latency = harness.Run(
            new(
                AdmissionBenchmarkPhaseKind.Latency,
                profile.LatencySampleCount,
                profile.LatencyBundleSize,
                operation)
            {
                ActiveWorkerCount = activeWorkerCount,
            },
            cancellationToken);

        AdmissionBenchmarkPhaseResult throughput = harness.Run(
            new(
                AdmissionBenchmarkPhaseKind.Throughput,
                profile.ThroughputIterations,
                1,
                operation)
            {
                ActiveWorkerCount = activeWorkerCount,
            },
            cancellationToken);

        long callbackDelta = checked(readMaterializedTerminalCallbacks() - callbackBaseline);

        return new(warmup, latency, throughput, callbackDelta);
    }
}

internal sealed class PersistentWorkerHarness : IDisposable
{
    private readonly Thread[] _threads;

    private readonly AutoResetEvent[] _wake;

    private readonly AutoResetEvent _allArmed = new(false);

    private readonly AutoResetEvent _allCompleted = new(false);

    private readonly AutoResetEvent _allParked = new(false);

    private readonly ManualResetEvent _parkRelease = new(false);

    private readonly WorkerResult[] _results;

    private readonly long[] _workerArmedStamp;

    private readonly long[] _workerStartedStamp;

    private readonly long[] _workerCompletedStamp;

    private readonly long[] _workerParkedStamp;

    private readonly IAdmissionBenchmarkOrderProbe? _probe;

    private AdmissionBenchmarkPhaseCommand? _command;

    private CancellationToken _cancellationToken;

    private Exception? _error;

    private int _commandEpoch;

    private int _runEpoch;

    private int _armedCount;

    private int _loopCompleteCount;

    private int _parkedCount;

    private int _activeWorkerCount;

    private long _processBaselineStamp;

    private long _runPublishedStamp;

    private long _processTerminalStamp;

    private long _transitionSequence;

    private int _processTerminalEpoch;

    private int _stop;

    private int _disposed;

    private int _handlesDisposed;

    private int _workerTerminationSucceeded;

    internal PersistentWorkerHarness(
        int workerCount,
        IAdmissionBenchmarkOrderProbe? probe = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workerCount);

        _probe = probe;

        _threads = new Thread[workerCount];

        _wake = new AutoResetEvent[workerCount];

        _results = new WorkerResult[workerCount];

        _workerArmedStamp = new long[workerCount];

        _workerStartedStamp = new long[workerCount];

        _workerCompletedStamp = new long[workerCount];

        _workerParkedStamp = new long[workerCount];

        Warm(_allArmed);

        Warm(_allCompleted);

        Warm(_allParked);

        _parkRelease.Set();

        _ = _parkRelease.WaitOne(0);

        _parkRelease.Reset();

        for (int index = 0; index < workerCount; index++)
        {
            int worker = index;

            _wake[index] = new(false);

            _results[index] = new();

            _threads[index] = new Thread(() => WorkerLoop(worker))
            {
                IsBackground = true,
                Name = $"grimoire-admission-{index}",
            };

            _threads[index].Start();
        }
    }

    internal int ThreadsCreated => _threads.Length;

    internal int[] ManagedThreadIds => _threads.Select(static thread => thread.ManagedThreadId).ToArray();

    internal bool AllWorkersParked => Volatile.Read(ref _parkedCount) == Volatile.Read(ref _activeWorkerCount)
        || Volatile.Read(ref _commandEpoch) == 0;

    internal bool WorkerTerminationSucceeded => Volatile.Read(ref _workerTerminationSucceeded) != 0;

    internal AdmissionBenchmarkPhaseResult Run(
        AdmissionBenchmarkPhaseCommand command,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        ArgumentNullException.ThrowIfNull(command);

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(command.TotalUnits);

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(command.BundleSize);

        cancellationToken.ThrowIfCancellationRequested();

        int activeWorkers = command.ActiveWorkerCount == 0
            ? _threads.Length
            : command.ActiveWorkerCount;

        if (activeWorkers <= 0 || activeWorkers > _threads.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(command), "The active worker prefix is invalid.");
        }

        int epoch = checked(Volatile.Read(ref _commandEpoch) + 1);

        _command = command;

        _cancellationToken = cancellationToken;

        _error = null;

        Volatile.Write(ref _activeWorkerCount, activeWorkers);

        Volatile.Write(ref _armedCount, 0);

        Volatile.Write(ref _loopCompleteCount, 0);

        Volatile.Write(ref _parkedCount, 0);

        Volatile.Write(ref _processTerminalEpoch, 0);

        _parkRelease.Reset();

        for (int worker = 0; worker < _threads.Length; worker++)
        {
            _workerArmedStamp[worker] = 0;

            _workerStartedStamp[worker] = 0;

            _workerCompletedStamp[worker] = 0;

            _workerParkedStamp[worker] = 0;

            int units = worker < activeWorkers
                ? UnitsForWorker(command.TotalUnits, activeWorkers, worker)
                : 0;

            _results[worker].Reset(
                units,
                command.Kind == AdmissionBenchmarkPhaseKind.Latency ? units : 0);
        }

        Volatile.Write(ref _commandEpoch, epoch);

        for (int worker = 0; worker < activeWorkers; worker++)
        {
            _wake[worker].Set();
        }

        WaitForSignal(_allArmed, cancellationToken);

        _processBaselineStamp = NextStamp();

        int gen0Start = GC.CollectionCount(0);

        long contentionStart = Monitor.LockContentionCount;

        _runPublishedStamp = NextStamp();

        Volatile.Write(ref _runEpoch, epoch);

        WaitForSignal(_allCompleted, cancellationToken);

        int gen0 = checked(GC.CollectionCount(0) - gen0Start);

        long contention = checked(Monitor.LockContentionCount - contentionStart);

        _processTerminalStamp = NextStamp();

        Volatile.Write(ref _processTerminalEpoch, epoch);

        _parkRelease.Set();

        WaitForSignal(_allParked, cancellationToken);

        ReplayProbe(epoch);

        if (_error is not null)
        {
            throw _error;
        }

        long operations = 0;

        long successes = 0;

        long failures = 0;

        long checksum = 0;

        long allocated = 0;

        long minimumStart = long.MaxValue;

        long maximumEnd = long.MinValue;

        List<double> samples = [];

        for (int worker = 0; worker < activeWorkers; worker++)
        {
            WorkerResult result = _results[worker];

            operations = checked(operations + result.Operations);

            successes = checked(successes + result.Successes);

            failures = checked(failures + result.Failures);

            checksum = checked(checksum + result.Checksum);

            allocated = checked(allocated + result.AllocatedBytes);

            minimumStart = Math.Min(minimumStart, result.StartTimestamp);

            maximumEnd = Math.Max(maximumEnd, result.EndTimestamp);

            samples.AddRange(result.Samples.AsSpan(0, result.SampleCount));
        }

        return new(
            operations,
            successes,
            failures,
            checksum,
            minimumStart,
            maximumEnd,
            allocated,
            gen0,
            contention,
            samples.ToArray());
    }

    internal static string ScheduledOperation(
        IReadOnlyList<string> schedule,
        int workerIndex,
        int iteration)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        if (schedule.Count == 0)
        {
            throw new ArgumentException("The mixed schedule must not be empty.", nameof(schedule));
        }

        return schedule[checked(workerIndex + iteration) % schedule.Count];
    }

    public void Dispose()
    {
        if (Volatile.Read(ref _handlesDisposed) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _disposed, 1);

        Volatile.Write(ref _stop, 1);

        Volatile.Write(ref _runEpoch, int.MaxValue);

        long deadline = checked(Stopwatch.GetTimestamp() + (5 * Stopwatch.Frequency));

        List<Action> signalActions =
        [
            () => _parkRelease.Set(),
        ];

        foreach (AutoResetEvent signal in _wake)
        {
            signalActions.Add(() => signal.Set());
        }

        Exception[] signalErrors = AdmissionBenchmarkWorkerTeardown.AttemptAll(signalActions);

        List<Action> joinActions = [];

        foreach (Thread thread in _threads)
        {
            joinActions.Add(() =>
            {
                long remainingTicks = deadline - Stopwatch.GetTimestamp();

                if (remainingTicks <= 0
                    || !thread.Join(TimeSpan.FromSeconds(remainingTicks / (double)Stopwatch.Frequency)))
                {
                    throw new InvalidOperationException(
                        "A persistent benchmark worker did not stop within the bounded join.");
                }
            });
        }

        Exception[] joinErrors = AdmissionBenchmarkWorkerTeardown.AttemptAll(joinActions);

        if (joinErrors.Length != 0)
        {
            throw new InvalidOperationException(
                "Persistent benchmark worker teardown was incomplete.",
                new AggregateException(signalErrors.Concat(joinErrors)));
        }

        Interlocked.Exchange(ref _workerTerminationSucceeded, 1);

        Interlocked.Exchange(ref _handlesDisposed, 1);

        List<Action> handleActions = [];

        foreach (AutoResetEvent signal in _wake)
        {
            handleActions.Add(signal.Dispose);
        }

        handleActions.Add(_allArmed.Dispose);

        handleActions.Add(_allCompleted.Dispose);

        handleActions.Add(_allParked.Dispose);

        handleActions.Add(_parkRelease.Dispose);

        Exception[] handleErrors = AdmissionBenchmarkWorkerTeardown.AttemptAll(handleActions);

        if (signalErrors.Length != 0 || handleErrors.Length != 0)
        {
            throw new InvalidOperationException(
                "Persistent benchmark worker teardown was incomplete.",
                new AggregateException(signalErrors.Concat(handleErrors)));
        }
    }

    private void WorkerLoop(int workerIndex)
    {
        while (true)
        {
            _wake[workerIndex].WaitOne();

            if (Volatile.Read(ref _stop) != 0)
            {
                return;
            }

            int epoch = Volatile.Read(ref _commandEpoch);

            AdmissionBenchmarkPhaseCommand command = _command
                ?? throw new InvalidOperationException("A worker was awakened without a phase command.");

            WorkerResult result = _results[workerIndex];

            _workerArmedStamp[workerIndex] = NextStamp();

            if (Interlocked.Increment(ref _armedCount) == Volatile.Read(ref _activeWorkerCount))
            {
                _allArmed.Set();
            }

            while (Volatile.Read(ref _runEpoch) < epoch)
            {
                Thread.SpinWait(32);
            }

            _workerStartedStamp[workerIndex] = NextStamp();

            result.StartTimestamp = Stopwatch.GetTimestamp();

            long allocationStart = GC.GetAllocatedBytesForCurrentThread();

            try
            {
                Execute(command, workerIndex, result, _cancellationToken);
            }
            catch (Exception exception)
            {
                Interlocked.CompareExchange(ref _error, exception, null);
            }

            result.AllocatedBytes = checked(GC.GetAllocatedBytesForCurrentThread() - allocationStart);

            result.EndTimestamp = Stopwatch.GetTimestamp();

            _workerCompletedStamp[workerIndex] = NextStamp();

            if (Interlocked.Increment(ref _loopCompleteCount) == Volatile.Read(ref _activeWorkerCount))
            {
                _allCompleted.Set();
            }

            _parkRelease.WaitOne();

            if (Volatile.Read(ref _processTerminalEpoch) < epoch)
            {
                Interlocked.CompareExchange(
                    ref _error,
                    new InvalidOperationException("A worker parked before the process terminal."),
                    null);
            }

            _workerParkedStamp[workerIndex] = NextStamp();

            if (Interlocked.Increment(ref _parkedCount) == Volatile.Read(ref _activeWorkerCount))
            {
                _allParked.Set();
            }
        }
    }

    private void ReplayProbe(int epoch)
    {
        if (_probe is null)
        {
            return;
        }

        int activeWorkers = Volatile.Read(ref _activeWorkerCount);

        List<(long Stamp, int Worker, AdmissionBenchmarkProbeEvent Event)> events = new((activeWorkers * 4) + 3)
        {
            (_processBaselineStamp, -1, AdmissionBenchmarkProbeEvent.ProcessBaseline),
            (_runPublishedStamp, -1, AdmissionBenchmarkProbeEvent.RunPublished),
            (_processTerminalStamp, -1, AdmissionBenchmarkProbeEvent.ProcessTerminal),
        };

        for (int worker = 0; worker < activeWorkers; worker++)
        {
            events.Add((_workerArmedStamp[worker], worker, AdmissionBenchmarkProbeEvent.WorkerArmed));

            events.Add((_workerStartedStamp[worker], worker, AdmissionBenchmarkProbeEvent.WorkerLoopStarted));

            events.Add((_workerCompletedStamp[worker], worker, AdmissionBenchmarkProbeEvent.WorkerLoopCompleted));

            events.Add((_workerParkedStamp[worker], worker, AdmissionBenchmarkProbeEvent.WorkerParked));
        }

        foreach ((long stamp, int worker, AdmissionBenchmarkProbeEvent value) in events.OrderBy(static item => item.Stamp))
        {
            if (stamp <= 0)
            {
                throw new InvalidOperationException("A worker omitted a measured lifecycle transition.");
            }

            _probe.Record(epoch, worker, value);
        }
    }

    private static void Execute(
        AdmissionBenchmarkPhaseCommand command,
        int workerIndex,
        WorkerResult result,
        CancellationToken cancellationToken)
    {
        for (int iteration = 0; iteration < result.AssignedUnits; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (command.Kind == AdmissionBenchmarkPhaseKind.Latency)
            {
                long start = Stopwatch.GetTimestamp();

                for (int bundle = 0; bundle < command.BundleSize; bundle++)
                {
                    RunOperation(command, workerIndex, checked(iteration * command.BundleSize + bundle), result, cancellationToken);
                }

                long elapsed = checked(Stopwatch.GetTimestamp() - start);

                result.Samples[result.SampleCount++] = elapsed * 1_000_000_000d / Stopwatch.Frequency / command.BundleSize;
            }
            else
            {
                RunOperation(command, workerIndex, iteration, result, cancellationToken);
            }
        }
    }

    private static void RunOperation(
        AdmissionBenchmarkPhaseCommand command,
        int workerIndex,
        int iteration,
        WorkerResult result,
        CancellationToken cancellationToken)
    {
        bool success = command.Operation(workerIndex, iteration, cancellationToken, ref result.Checksum);

        result.Operations++;

        if (success)
        {
            result.Successes++;
        }
        else
        {
            result.Failures++;
        }
    }

    private long NextStamp() => Interlocked.Increment(ref _transitionSequence);

    private static int UnitsForWorker(
        int totalUnits,
        int activeWorkers,
        int workerIndex)
    {
        int quotient = totalUnits / activeWorkers;

        int remainder = totalUnits % activeWorkers;

        return quotient + (workerIndex < remainder ? 1 : 0);
    }

    private static void WaitForSignal(
        AutoResetEvent signal,
        CancellationToken cancellationToken)
    {
        while (!signal.WaitOne(TimeSpan.FromMilliseconds(100)))
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static void Warm(AutoResetEvent signal)
    {
        signal.Set();

        _ = signal.WaitOne(0);
    }

    private sealed class WorkerResult
    {
        internal long Operations;

        internal long Successes;

        internal long Failures;

        internal long Checksum;

        internal long StartTimestamp;

        internal long EndTimestamp;

        internal long AllocatedBytes;

        internal int AssignedUnits;

        internal double[] Samples = [];

        internal int SampleCount;

        internal void Reset(
            int assignedUnits,
            int sampleCount)
        {
            Operations = 0;

            Successes = 0;

            Failures = 0;

            Checksum = 0;

            StartTimestamp = 0;

            EndTimestamp = 0;

            AllocatedBytes = 0;

            AssignedUnits = assignedUnits;

            SampleCount = 0;

            if (Samples.Length != sampleCount)
            {
                Samples = new double[sampleCount];
            }
        }
    }
}
