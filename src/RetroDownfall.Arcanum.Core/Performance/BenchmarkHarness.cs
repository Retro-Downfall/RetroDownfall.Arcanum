using System.Diagnostics;

namespace RetroDownfall.Arcanum.Core.Performance;

/// <summary>
/// The measurement loop, and the only place a duration or an allocation is recorded.
/// </summary>
/// <remarks>
/// Every statistic it reports comes from <see cref="NearestRankPercentile"/> and
/// <see cref="BenchmarkControlNoise"/>, and the loop itself lives beside them rather than in the
/// benchmark host so the ordinary suite can drive it. A control measured in an assembly no lane
/// compiles is a control nothing can prove is measuring anything.
/// </remarks>
internal static class BenchmarkHarness
{

    /// <summary>
    /// Measures what one iteration of the loop itself costs, over an operation that does nothing.
    /// </summary>
    /// <remarks>
    /// This is subtracted from every operation's allocation below, so it has to be measured through
    /// the same loop rather than estimated: a control taken any other way would subtract a number the
    /// measured runs never paid.
    /// </remarks>
    internal static Task<double[]> MeasureControlAsync(WorkloadMeasurement measurement) =>
        MeasureControlAsync(measurement, EmptyAsync);

    internal static async Task<double[]> MeasureControlAsync(
        WorkloadMeasurement measurement,
        Func<Task> operation)
    {

        ArgumentNullException.ThrowIfNull(measurement);

        ArgumentNullException.ThrowIfNull(operation);

        double[] samples = new double[measurement.AllocationControlIterations];

        for (int iteration = 0; iteration < measurement.AllocationControlIterations; iteration++)
        {

            long before = GC.GetTotalAllocatedBytes(precise: true);

            long start = Stopwatch.GetTimestamp();

            await operation().ConfigureAwait(false);

            _ = Stopwatch.GetElapsedTime(start);

            samples[iteration] = GC.GetTotalAllocatedBytes(precise: true) - before;

        }

        return samples;

    }

    internal static async Task<BenchmarkOperationResult> MeasureAsync(
        string id,
        WorkloadMeasurement measurement,
        double controlBytesPerIteration,
        Func<Task> operation)
    {

        ArgumentNullException.ThrowIfNull(measurement);

        ArgumentNullException.ThrowIfNull(operation);

        // Warmup retires costs the shipped binary pays once per process — first statement preparation,
        // first buffer growth, first page read. It is not a device for reaching a state the product
        // never runs in, which is why the count is pinned in the manifest rather than tuned until the
        // numbers look good.
        for (int iteration = 0; iteration < measurement.WarmupIterations; iteration++)
        {

            await operation().ConfigureAwait(false);

        }

        double[][] batches = new double[measurement.Batches][];

        // The sample buffers are allocated before the snapshot rather than inside the loop. They are
        // the harness's own storage, the control never pays for them, and leaving them inside the
        // measured window would charge every operation for bytes its own control could not subtract.
        for (int batch = 0; batch < measurement.Batches; batch++)
        {

            batches[batch] = new double[measurement.IterationsPerBatch];

        }

        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);

        long totalIterations = 0;

        for (int batch = 0; batch < measurement.Batches; batch++)
        {

            double[] samples = batches[batch];

            for (int iteration = 0; iteration < measurement.IterationsPerBatch; iteration++)
            {

                long start = Stopwatch.GetTimestamp();

                await operation().ConfigureAwait(false);

                samples[iteration] = Stopwatch.GetElapsedTime(start).TotalMicroseconds;

            }

            totalIterations += measurement.IterationsPerBatch;

        }

        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;

        double[] pooled = [.. batches.SelectMany(static batch => batch)];

        Array.Sort(pooled);

        // Nothing is clamped. A correction below zero means the operation allocated less than the loop
        // around it, which is evidence the pairing is not working rather than a small number to floor
        // at zero — and the control check is what reads that evidence.
        double corrected = totalIterations == 0
            ? 0
            : ((double)allocated / totalIterations) - controlBytesPerIteration;

        return new BenchmarkOperationResult(
            id,
            NearestRankPercentile.OfSorted(pooled, 50),
            NearestRankPercentile.OfSorted(pooled, 95),
            NearestRankPercentile.OfSorted(pooled, 99),
            corrected,
            batches);

    }

    /// <summary>Judges whether the control was quiet enough for the subtraction above to mean anything.</summary>
    internal static BenchmarkControlResult Judge(
        double[] controlSamples,
        IReadOnlyList<BenchmarkOperationResult> operations)
    {

        ArgumentNullException.ThrowIfNull(operations);

        BenchmarkControlNoise noise = BenchmarkControlNoise.Measure(
            controlSamples,
            [.. operations.Select(static operation => operation.AllocationBytes)]);

        return new BenchmarkControlResult(
            noise.SpreadBytes,
            noise.MedianAbsoluteDeviationBytes,
            noise.NegativeFraction,
            noise.IsAcceptable);

    }

    /// <summary>
    /// An operation that does nothing, and pays what the loop pays around one that does something.
    /// </summary>
    /// <remarks>
    /// It has to suspend. Returning <see cref="Task.CompletedTask"/> made every control sample exactly
    /// zero: the await completed synchronously, no per-iteration state machine was ever boxed, and the
    /// subtraction that the allocation ceilings and the negative-correction check are both built on
    /// became a subtraction of nothing. Measured operations reach real asynchronous work through this
    /// same delegate, so the control has to reach a suspension through it too.
    /// </remarks>
    private static async Task EmptyAsync() => await Task.Yield();

}
