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
    int IterationsPerWorker,
    int BundleSize,
    AdmissionBenchmarkOperationDelegate Operation);

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

internal sealed class PersistentWorkerHarness : IDisposable
{

    private readonly Thread[] _threads;

    private readonly AutoResetEvent[] _wake;

    private readonly WorkerResult[] _results;

    private readonly int[] _workerStartedEpoch;

    private readonly int[] _workerCompletedEpoch;

    private readonly IAdmissionBenchmarkOrderProbe? _probe;

    private AdmissionBenchmarkPhaseCommand? _command;

    private CancellationToken _cancellationToken;

    private Exception? _error;

    private int _commandEpoch;

    private int _runEpoch;

    private int _parkEpoch;

    private int _armedCount;

    private int _loopCompleteCount;

    private int _parkedCount;

    private int _processBaselineEpoch;

    private int _processTerminalEpoch;

    private int _stop;

    private int _disposed;

    internal PersistentWorkerHarness(
        int workerCount,
        IAdmissionBenchmarkOrderProbe? probe = null)
    {

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workerCount);

        _probe = probe;

        _threads = new Thread[workerCount];

        _wake = new AutoResetEvent[workerCount];

        _results = new WorkerResult[workerCount];

        _workerStartedEpoch = new int[workerCount];

        _workerCompletedEpoch = new int[workerCount];

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

    internal bool AllWorkersParked => Volatile.Read(ref _parkedCount) == _threads.Length
        || Volatile.Read(ref _commandEpoch) == 0;

    internal AdmissionBenchmarkPhaseResult Run(
        AdmissionBenchmarkPhaseCommand command,
        CancellationToken cancellationToken)
    {

        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        ArgumentNullException.ThrowIfNull(command);

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(command.IterationsPerWorker);

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(command.BundleSize);

        cancellationToken.ThrowIfCancellationRequested();

        int epoch = checked(Volatile.Read(ref _commandEpoch) + 1);

        _command = command;

        _cancellationToken = cancellationToken;

        _error = null;

        Volatile.Write(ref _armedCount, 0);

        Volatile.Write(ref _loopCompleteCount, 0);

        Volatile.Write(ref _parkedCount, 0);

        Volatile.Write(ref _commandEpoch, epoch);

        foreach (AutoResetEvent signal in _wake)
        {

            signal.Set();

        }

        SpinUntil(ref _armedCount, _threads.Length, cancellationToken);

        int gen0Start = GC.CollectionCount(0);

        long contentionStart = Monitor.LockContentionCount;

        Volatile.Write(ref _processBaselineEpoch, epoch);

        Volatile.Write(ref _runEpoch, epoch);

        SpinUntil(ref _loopCompleteCount, _threads.Length, cancellationToken);

        int gen0 = checked(GC.CollectionCount(0) - gen0Start);

        long contention = checked(Monitor.LockContentionCount - contentionStart);

        Volatile.Write(ref _processTerminalEpoch, epoch);

        Volatile.Write(ref _parkEpoch, epoch);

        SpinUntil(ref _parkedCount, _threads.Length, CancellationToken.None);

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

        foreach (WorkerResult result in _results)
        {

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

        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {

            return;

        }

        Volatile.Write(ref _stop, 1);

        Volatile.Write(ref _runEpoch, int.MaxValue);

        Volatile.Write(ref _parkEpoch, int.MaxValue);

        foreach (AutoResetEvent signal in _wake)
        {

            signal.Set();

        }

        foreach (Thread thread in _threads)
        {

            thread.Join();

        }

        foreach (AutoResetEvent signal in _wake)
        {

            signal.Dispose();

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

            result.Reset(command.Kind == AdmissionBenchmarkPhaseKind.Latency
                ? command.IterationsPerWorker
                : 0);

            Interlocked.Increment(ref _armedCount);

            while (Volatile.Read(ref _runEpoch) < epoch)
            {

                Thread.SpinWait(32);

            }

            if (Volatile.Read(ref _processBaselineEpoch) < epoch)
            {

                Interlocked.CompareExchange(
                    ref _error,
                    new InvalidOperationException("A worker started before the process baseline."),
                    null);

            }

            Volatile.Write(ref _workerStartedEpoch[workerIndex], epoch);

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

            Volatile.Write(ref _workerCompletedEpoch[workerIndex], epoch);

            Interlocked.Increment(ref _loopCompleteCount);

            while (Volatile.Read(ref _parkEpoch) < epoch)
            {

                Thread.SpinWait(32);

            }

            if (Volatile.Read(ref _processTerminalEpoch) < epoch)
            {

                Interlocked.CompareExchange(
                    ref _error,
                    new InvalidOperationException("A worker parked before the process terminal."),
                    null);

            }

            Interlocked.Increment(ref _parkedCount);

        }

    }

    private void ReplayProbe(int epoch)
    {

        if (_probe is null)
        {

            return;

        }

        for (int worker = 0; worker < _threads.Length; worker++)
        {

            _probe.Record(epoch, worker, AdmissionBenchmarkProbeEvent.WorkerArmed);

        }

        _probe.Record(epoch, -1, AdmissionBenchmarkProbeEvent.ProcessBaseline);

        _probe.Record(epoch, -1, AdmissionBenchmarkProbeEvent.RunPublished);

        for (int worker = 0; worker < _threads.Length; worker++)
        {

            if (Volatile.Read(ref _workerStartedEpoch[worker]) != epoch
                || Volatile.Read(ref _workerCompletedEpoch[worker]) != epoch)
            {

                throw new InvalidOperationException("A worker omitted a measured lifecycle transition.");

            }

            _probe.Record(epoch, worker, AdmissionBenchmarkProbeEvent.WorkerLoopStarted);

            _probe.Record(epoch, worker, AdmissionBenchmarkProbeEvent.WorkerLoopCompleted);

        }

        _probe.Record(epoch, -1, AdmissionBenchmarkProbeEvent.ProcessTerminal);

        for (int worker = 0; worker < _threads.Length; worker++)
        {

            _probe.Record(epoch, worker, AdmissionBenchmarkProbeEvent.WorkerParked);

        }

    }

    private static void Execute(
        AdmissionBenchmarkPhaseCommand command,
        int workerIndex,
        WorkerResult result,
        CancellationToken cancellationToken)
    {

        for (int iteration = 0; iteration < command.IterationsPerWorker; iteration++)
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

    private static void SpinUntil(
        ref int location,
        int target,
        CancellationToken cancellationToken)
    {

        while (Volatile.Read(ref location) != target)
        {

            cancellationToken.ThrowIfCancellationRequested();

            Thread.SpinWait(32);

        }

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

        internal double[] Samples = [];

        internal int SampleCount;

        internal void Reset(int sampleCount)
        {

            Operations = 0;

            Successes = 0;

            Failures = 0;

            Checksum = 0;

            StartTimestamp = 0;

            EndTimestamp = 0;

            AllocatedBytes = 0;

            SampleCount = 0;

            if (Samples.Length != sampleCount)
            {

                Samples = new double[sampleCount];

            }

        }

    }

}
