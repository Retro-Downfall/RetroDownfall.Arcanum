using System.Diagnostics;

using RetroDownfall.Arcanum.Core.Performance;

namespace RetroDownfall.Arcanum.Covenant.Benchmarks;

/// <summary>
/// The measurement loop, and the only place a duration or an allocation is recorded.
/// </summary>
/// <remarks>
/// Every statistic it reports comes from <see cref="NearestRankPercentile"/> and
/// <see cref="BenchmarkControlNoise"/> in Core, which the ordinary suite already covers. Computing a
/// percentile here would mean the gate's arithmetic had no tests and the tested arithmetic had no
/// caller.
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
    internal static async Task<double[]> MeasureControlAsync(WorkloadMeasurement measurement)
    {

        double[] samples = new double[measurement.AllocationControlIterations];

        for (int iteration = 0; iteration < measurement.AllocationControlIterations; iteration++)
        {

            long before = GC.GetTotalAllocatedBytes(precise: true);

            long start = Stopwatch.GetTimestamp();

            await EmptyAsync().ConfigureAwait(false);

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

        // Warmup retires costs the shipped binary pays once per process — first statement preparation,
        // first buffer growth, first page read. It is not a device for reaching a state the product
        // never runs in, which is why the count is pinned in the manifest rather than tuned until the
        // numbers look good.
        for (int iteration = 0; iteration < measurement.WarmupIterations; iteration++)
        {

            await operation().ConfigureAwait(false);

        }

        double[][] batches = new double[measurement.Batches][];

        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);

        long totalIterations = 0;

        for (int batch = 0; batch < measurement.Batches; batch++)
        {

            double[] samples = new double[measurement.IterationsPerBatch];

            for (int iteration = 0; iteration < measurement.IterationsPerBatch; iteration++)
            {

                long start = Stopwatch.GetTimestamp();

                await operation().ConfigureAwait(false);

                samples[iteration] = Stopwatch.GetElapsedTime(start).TotalMicroseconds;

            }

            batches[batch] = samples;

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

        BenchmarkControlNoise noise = BenchmarkControlNoise.Measure(
            controlSamples,
            [.. operations.Select(static operation => operation.AllocationBytes)]);

        return new BenchmarkControlResult(
            noise.SpreadBytes,
            noise.MedianAbsoluteDeviationBytes,
            noise.NegativeFraction,
            noise.IsAcceptable);

    }

    private static Task EmptyAsync() => Task.CompletedTask;

}
