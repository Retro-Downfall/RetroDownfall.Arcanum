using System.Data.Common;

using Microsoft.Data.Sqlite;

using Microsoft.EntityFrameworkCore;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks;

/// <summary>
/// Host-only adapter over the real Grimoire admission state machine.
/// </summary>
internal sealed class GrimoireAdmissionWorkloadBed : IDisposable
{

    private readonly BenchmarkComposition _composition;

    private readonly WorkerState[] _workers;

    private string _operation = string.Empty;

    private string[] _mixedSchedule = [];

    private long _liveRequests;

    private long _liveWork;

    private long _liveEffects;

    private long _liveOpens;

    private long _liveWaiters;

    private int _disposed;

    private int _workerThreadsTerminated;

    private int _workerConnectionsDisposed;

    internal GrimoireAdmissionWorkloadBed(BenchmarkComposition composition)
    {

        _composition = composition;

        int maximumWorkers = Math.Max(8, global::System.Environment.ProcessorCount);

        _workers = Enumerable.Range(0, maximumWorkers)
            .Select(static _ => new WorkerState())
            .ToArray();

        SeedDatabase();

    }

    internal AdmissionBenchmarkCellResult[] Run(
        AdmissionBenchmarkManifest manifest,
        AdmissionBenchmarkProfile profile,
        CancellationToken cancellationToken)
    {

        _mixedSchedule = profile.MixedSchedule;

        List<AdmissionBenchmarkCellResult> cells = [];

        PersistentWorkerHarness harness = new(_workers.Length);

        Exception? primaryException = null;

        try
        {

            foreach (AdmissionBenchmarkConcurrency concurrency in manifest.Concurrency)
            {

                int workers = concurrency.Workers == 0
                    ? global::System.Environment.ProcessorCount
                    : concurrency.Workers;

                foreach (string operation in manifest.Operations)
                {

                    _operation = operation;

                    AdmissionBenchmarkCellMeasurement measurement = AdmissionBenchmarkCellRunner.Run(
                        harness,
                        profile,
                        workers,
                        Execute,
                        () => _composition.Gate.MaterializedTerminalCallbacks,
                        cancellationToken);

                    cells.Add(ToCell(
                        operation,
                        concurrency.Id,
                        workers,
                        measurement));

                }

            }

            return cells.ToArray();

        }
        catch (Exception exception)
        {

            primaryException = exception;

            throw;

        }
        finally
        {

            try
            {

                harness.Dispose();

            }
            catch (Exception exception) when (primaryException is not null)
            {

                Console.Error.WriteLine($"Persistent benchmark worker teardown failed: {exception.Message}");

            }
            finally
            {

                if (harness.WorkerTerminationSucceeded)
                {

                    Interlocked.Exchange(ref _workerThreadsTerminated, 1);

                }

            }

        }

    }

    internal AdmissionBenchmarkMeasuredResourceWitness MeasuredResourceWitness => new(
        Volatile.Read(ref _workerThreadsTerminated) != 0,
        Volatile.Read(ref _workerConnectionsDisposed) != 0);

    internal async ValueTask<AdmissionBenchmarkFinalState> ValidateFinalStateAsync(
        CancellationToken cancellationToken)
    {

        long observedGeneration = _composition.Gate.CurrentGeneration;

        Task<long> nextOpen = _composition.Gate.WaitForNextOpenGenerationAsync(
            observedGeneration,
            cancellationToken);

        Interlocked.Increment(ref _liveWaiters);

        bool drainSucceeded = false;

        bool reopenSucceeded = false;

        try
        {

            Result<IGrimoireClosingOwner> begun = _composition.Gate.BeginOrResumeExclusive(
                new(
                    Guid.Parse("00000000-0000-0000-0000-000000000001"),
                    CovenantExclusiveOperation.CovenantReset,
                    new CovenantDigest(Enumerable.Repeat((byte)1, 32).ToArray())));

            RequireSuccess(begun);

            await using IGrimoireClosingOwner closing = begun.Value;

            Result drained = await _composition.Gate.DrainRequestAndWorkAsync(
                closing,
                cancellationToken).ConfigureAwait(false);

            RequireSuccess(drained);

            drainSucceeded = drained.IsSuccess;

            Result<IGrimoireExclusiveClosedLease> closedResult = await _composition.Gate
                .CloseConnectionAdmissionAsync(closing, cancellationToken).ConfigureAwait(false);

            RequireSuccess(closedResult);

            if (nextOpen.IsCompleted)
            {

                throw new InvalidDataException("The next-open waiter completed while admission was closed.");

            }

            await using IGrimoireExclusiveClosedLease closed = closedResult.Value;

            Result reopened = await closed.CompleteAsync(
                CovenantExclusiveLeaseDisposition.RollbackAndReopen,
                cancellationToken).ConfigureAwait(false);

            RequireSuccess(reopened);

            reopenSucceeded = reopened.IsSuccess;

            long reopenedGeneration = await nextOpen.ConfigureAwait(false);

            if (reopenedGeneration != checked(observedGeneration + 1))
            {

                throw new InvalidDataException("The next-open waiter observed the wrong reopen generation.");

            }

        }
        finally
        {

            Interlocked.Decrement(ref _liveWaiters);

        }

        bool requestSucceeded = _composition.Gate.TryAcquireRequestLease(
            GrimoireRequestKind.Finite,
            out IGrimoireRequestLease? request);

        if (!requestSucceeded)
        {

            throw new InvalidDataException("Fresh request admission failed after benchmark maintenance.");

        }

        await request!.DisposeAsync().ConfigureAwait(false);

        using (SqliteConnection finalConnection = new("Data Source=:memory:;Pooling=False"))
        using (IGrimoireConnectionOpenTicket ticket = _composition.Gate.AcquireOrdinaryOpen(finalConnection))
        {

            RequireSuccess(ticket.RevalidateAfterNativeOpen());

            RequireSuccess(ticket.MarkOpened());

        }

        return new(
            Interlocked.Read(ref _liveRequests),
            Interlocked.Read(ref _liveWork),
            Interlocked.Read(ref _liveOpens),
            Interlocked.Read(ref _liveEffects),
            Interlocked.Read(ref _liveWaiters),
            drainSucceeded,
            reopenSucceeded);

    }

    internal async ValueTask<AdmissionBenchmarkHistoricalChurnResult[]> MeasureHistoricalChurnAsync(
        CancellationToken cancellationToken)
    {

        int[] counts = [0, 64, 640];

        AdmissionBenchmarkHistoricalChurnResult[] results = new AdmissionBenchmarkHistoricalChurnResult[counts.Length];

        for (int index = 0; index < counts.Length; index++)
        {

            int count = counts[index];

            CovenantConnectionDrain drain = new();

            GrimoireConnectionAdmissionGate gate = new(
                TimeProvider.System,
                drain,
                new ChurnMaintenancePaths($"churn-{index}.db"));

            for (int admission = 0; admission < count; admission++)
            {

                if (!gate.TryAcquireRequestLease(
                        GrimoireRequestKind.Finite,
                        out IGrimoireRequestLease? request))
                {

                    throw new InvalidDataException("Historical-churn admission was refused.");

                }

                request!.DisposeAsync().GetAwaiter().GetResult();

            }

            long before = GC.GetTotalMemory(forceFullCollection: true);

            long started = System.Diagnostics.Stopwatch.GetTimestamp();

            bool drained = await CloseDrainReopenAsync(
                gate,
                index + 10,
                cancellationToken).ConfigureAwait(false);

            long ended = System.Diagnostics.Stopwatch.GetTimestamp();

            long after = GC.GetTotalMemory(forceFullCollection: true);

            results[index] = new(
                count,
                before,
                after,
                (ended - started) * (1_000_000_000d / System.Diagnostics.Stopwatch.Frequency),
                drained);

        }

        return results;

    }

    public void Dispose()
    {

        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {

            return;

        }

        foreach (WorkerState worker in _workers)
        {

            worker.Dispose();

        }

        Interlocked.Exchange(ref _workerConnectionsDisposed, 1);

    }

    private bool Execute(
        int workerIndex,
        int iteration,
        CancellationToken cancellationToken,
        ref long checksum)
    {

        cancellationToken.ThrowIfCancellationRequested();

        string operation = _operation == "ordinary.mixed"
            ? PersistentWorkerHarness.ScheduledOperation(_mixedSchedule, workerIndex, iteration)
            : _operation;

        switch (operation)
        {
            case "request.finite":
                Request(GrimoireRequestKind.Finite, readRevocation: false, ref checksum);
                break;
            case "request.quiesceable":
                Request(GrimoireRequestKind.QuiesceableStream, readRevocation: true, ref checksum);
                break;
            case "work.db-only":
                Work(withEffect: false, ref checksum);
                break;
            case "work.effect":
                Work(withEffect: true, ref checksum);
                break;
            case "open.failed":
                FailedOpen(workerIndex, ref checksum);
                break;
            case "request.open":
                RequestOpen(workerIndex, ref checksum);
                break;
            case "generation.read":
                checksum = checked(checksum + _composition.Gate.CurrentGeneration);
                break;
            case "ef.pooled":
                PooledEf(ref checksum);
                break;
            default:
                throw new InvalidDataException($"Unsupported benchmark operation '{operation}'.");
        }

        return true;

    }

    private void Request(
        GrimoireRequestKind kind,
        bool readRevocation,
        ref long checksum)
    {

        if (!_composition.Gate.TryAcquireRequestLease(kind, out IGrimoireRequestLease? lease))
        {

            throw new InvalidDataException("Ordinary request admission was refused.");

        }

        Interlocked.Increment(ref _liveRequests);

        try
        {

            checksum = checked(checksum + lease!.Generation);

            if (readRevocation)
            {

                checksum = checked(checksum + (lease.MaintenanceRevocation.CanBeCanceled ? 1 : 0));

            }

            lease.DisposeAsync().GetAwaiter().GetResult();

        }
        finally
        {

            Interlocked.Decrement(ref _liveRequests);

        }

    }

    private void Work(bool withEffect, ref long checksum)
    {

        if (!_composition.Gate.TryAcquireWorkLease(
                GrimoireWorkKind.BatchProcessing,
                out IGrimoireWorkLease? lease))
        {

            throw new InvalidDataException("Ordinary work admission was refused.");

        }

        Interlocked.Increment(ref _liveWork);

        try
        {

            checksum = checked(checksum + lease!.Generation);

            if (withEffect)
            {

                if (!lease.TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? effect))
                {

                    throw new InvalidDataException("External-effect admission was refused.");

                }

                Interlocked.Increment(ref _liveEffects);

                try
                {

                    effect!.DisposeAsync().GetAwaiter().GetResult();

                }
                finally
                {

                    Interlocked.Decrement(ref _liveEffects);

                }

            }

            lease.DisposeAsync().GetAwaiter().GetResult();

        }
        finally
        {

            Interlocked.Decrement(ref _liveWork);

        }

    }

    private void FailedOpen(int workerIndex, ref long checksum)
    {

        Interlocked.Increment(ref _liveOpens);

        try
        {

            using IGrimoireConnectionOpenTicket ticket = _composition.Gate.AcquireOrdinaryOpen(
                _workers[workerIndex].Connection);

            checksum = checked(checksum + ticket.Generation);

            ticket.MarkFailed();

        }
        finally
        {

            Interlocked.Decrement(ref _liveOpens);

        }

    }

    private void RequestOpen(int workerIndex, ref long checksum)
    {

        if (!_composition.Gate.TryAcquireRequestLease(
                GrimoireRequestKind.Finite,
                out IGrimoireRequestLease? request))
        {

            throw new InvalidDataException("Nested request admission was refused.");

        }

        Interlocked.Increment(ref _liveRequests);

        try
        {

            Interlocked.Increment(ref _liveOpens);

            try
            {

                using IGrimoireConnectionOpenTicket ticket = _composition.Gate.AcquireOrdinaryOpen(
                    _workers[workerIndex].Connection);

                RequireSuccess(ticket.RevalidateAfterNativeOpen());

                RequireSuccess(ticket.MarkOpened());

                checksum = checked(checksum + request!.Generation + ticket.Generation);

            }
            finally
            {

                Interlocked.Decrement(ref _liveOpens);

            }

            request!.DisposeAsync().GetAwaiter().GetResult();

        }
        finally
        {

            Interlocked.Decrement(ref _liveRequests);

        }

    }

    private void PooledEf(ref long checksum)
    {

        using IServiceScope scope = _composition.Services.CreateScope();

        ArcanumDbContext context = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        context.Database.OpenConnection();

        try
        {

            using DbCommand command = context.Database.GetDbConnection().CreateCommand();

            command.CommandText = "SELECT 1";

            long scalar = Convert.ToInt64(command.ExecuteScalar());

            if (scalar != 1)
            {

                throw new InvalidDataException("The pooled EF scalar query returned an unexpected value.");

            }

            checksum = checked(checksum + scalar);

        }
        finally
        {

            context.Database.CloseConnection();

        }

    }

    private void SeedDatabase()
    {

        using IServiceScope scope = _composition.Services.CreateScope();

        ArcanumDbContext context = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        context.Database.OpenConnection();

        try
        {

            using DbCommand scalar = context.Database.GetDbConnection().CreateCommand();

            scalar.CommandText = "SELECT 1";

            if (Convert.ToInt64(scalar.ExecuteScalar()) != 1)
            {

                throw new InvalidDataException("The benchmark database seed query failed.");

            }

            using DbCommand cipher = context.Database.GetDbConnection().CreateCommand();

            cipher.CommandText = "PRAGMA cipher_version";

            if (string.IsNullOrWhiteSpace(Convert.ToString(cipher.ExecuteScalar())))
            {

                throw new InvalidDataException("The benchmark did not open the real SQLCipher provider.");

            }

        }
        finally
        {

            context.Database.CloseConnection();

        }

    }

    private static AdmissionBenchmarkCellResult ToCell(
        string operation,
        string concurrency,
        int workers,
        AdmissionBenchmarkCellMeasurement measurement)
    {

        AdmissionBenchmarkPhaseResult warmup = measurement.Warmup;

        AdmissionBenchmarkPhaseResult latency = measurement.Latency;

        AdmissionBenchmarkPhaseResult throughput = measurement.Throughput;

        if (warmup.FailureCount != 0
            || latency.FailureCount != 0
            || throughput.FailureCount != 0
            || latency.BundleNanosecondsPerOperation.Length == 0)
        {

            throw new InvalidDataException("A benchmark phase failed or produced no latency samples.");

        }

        double elapsedSeconds = (throughput.MaximumWorkerEndTimestamp - throughput.MinimumWorkerStartTimestamp)
            / (double)System.Diagnostics.Stopwatch.Frequency;

        if (elapsedSeconds <= 0)
        {

            throw new InvalidDataException("A benchmark phase produced a zero timing denominator.");

        }

        double[] samples = [.. latency.BundleNanosecondsPerOperation];

        Array.Sort(samples);

        return new(
            operation,
            concurrency,
            workers,
            warmup.OperationCount,
            latency.BundleNanosecondsPerOperation.LongLength,
            latency.OperationCount,
            throughput.OperationCount,
            throughput.OperationCount / elapsedSeconds,
            Percentile(samples, 0.50),
            Percentile(samples, 0.95),
            Percentile(samples, 0.99),
            throughput.AllocatedBytes,
            throughput.OperationCount,
            throughput.AllocatedBytes / (double)throughput.OperationCount,
            throughput.Gen0Collections,
            throughput.LockContentions,
            checked(warmup.SuccessCount + latency.SuccessCount + throughput.SuccessCount),
            checked(warmup.FailureCount + latency.FailureCount + throughput.FailureCount),
            checked(warmup.Checksum + latency.Checksum + throughput.Checksum),
            measurement.MaterializedTerminalCallbackDelta);

    }

    private static double Percentile(double[] sorted, double percentile)
    {

        int rank = Math.Clamp((int)Math.Ceiling(percentile * sorted.Length) - 1, 0, sorted.Length - 1);

        return sorted[rank];

    }

    private static void RequireSuccess(Result result)
    {

        if (result.IsFailure)
        {

            throw new InvalidDataException(result.Error.Message);

        }

    }

    private static async ValueTask<bool> CloseDrainReopenAsync(
        GrimoireConnectionAdmissionGate gate,
        int ownerByte,
        CancellationToken cancellationToken)
    {

        byte[] guidBytes = new byte[16];

        guidBytes[15] = checked((byte)ownerByte);

        Result<IGrimoireClosingOwner> begun = gate.BeginOrResumeExclusive(
            new(
                new Guid(guidBytes),
                CovenantExclusiveOperation.CovenantReset,
                new CovenantDigest(Enumerable.Repeat(checked((byte)ownerByte), 32).ToArray())));

        RequireSuccess(begun);

        await using IGrimoireClosingOwner closing = begun.Value;

        Result drained = await gate.DrainRequestAndWorkAsync(
            closing,
            cancellationToken).ConfigureAwait(false);

        RequireSuccess(drained);

        Result<IGrimoireExclusiveClosedLease> closedResult = await gate
            .CloseConnectionAdmissionAsync(closing, cancellationToken).ConfigureAwait(false);

        RequireSuccess(closedResult);

        await using IGrimoireExclusiveClosedLease closed = closedResult.Value;

        Result reopened = await closed.CompleteAsync(
            CovenantExclusiveLeaseDisposition.RollbackAndReopen,
            cancellationToken).ConfigureAwait(false);

        RequireSuccess(reopened);

        return drained.IsSuccess && reopened.IsSuccess;

    }

    private sealed class WorkerState : IDisposable
    {

        internal SqliteConnection Connection { get; } = new("Data Source=:memory:;Pooling=False");

        public void Dispose() => Connection.Dispose();

    }

    private sealed class ChurnMaintenancePaths(string canonicalPath) : IGrimoireMaintenancePathAuthority
    {

        public string CanonicalDatabasePath => canonicalPath;

        public string ExportStagingDatabasePath(Guid operationId) =>
            canonicalPath + "." + operationId.ToString("N") + ".candidate";

    }

}
