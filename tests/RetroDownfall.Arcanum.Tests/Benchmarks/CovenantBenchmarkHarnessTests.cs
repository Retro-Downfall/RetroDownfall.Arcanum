using RetroDownfall.Arcanum.Core.Performance;

namespace RetroDownfall.Arcanum.Tests.Benchmarks;

/// <summary>
/// The allocation control, which every allocation number the gate publishes is a difference against.
/// </summary>
public sealed class CovenantBenchmarkHarnessTests
{

    [Fact]
    public async Task The_allocation_control_measures_what_the_loop_pays_around_an_operation()
    {

        double[] samples = await BenchmarkHarness.MeasureControlAsync(
            new WorkloadMeasurement(WarmupIterations: 0, Batches: 0, IterationsPerBatch: 0, AllocationControlIterations: 64));

        Assert.Equal(64, samples.Length);

        // The control used to await an already-completed task with no delegate and no state machine,
        // so every sample was exactly zero. That made the paired subtraction a subtraction of nothing:
        // no corrected allocation could ever come out negative, the spread and the deviation were
        // trivially inside their bounds, and the noisy-control branch of the gate was unreachable.
        Assert.True(
            NearestRankPercentile.Of(samples, 50) > 0,
            "The median control sample is zero, so the allocation numbers are corrected by nothing.");

    }

    [Fact]
    public async Task The_control_drives_the_operation_it_is_handed_once_per_iteration()
    {

        int invocations = 0;

        // Through the same Func<Task> the measured loop uses. A control measured any other way would
        // subtract a number the measured runs never paid.
        _ = await BenchmarkHarness.MeasureControlAsync(
            new WorkloadMeasurement(WarmupIterations: 0, Batches: 0, IterationsPerBatch: 0, AllocationControlIterations: 32),
            () =>
            {

                invocations++;

                return Task.CompletedTask;

            });

        Assert.Equal(32, invocations);

    }

    [Fact]
    public async Task A_measured_operation_reports_the_percentiles_of_every_sample_it_took()
    {

        BenchmarkOperationResult result = await BenchmarkHarness.MeasureAsync(
            "sample.operation",
            new WorkloadMeasurement(WarmupIterations: 1, Batches: 3, IterationsPerBatch: 4, AllocationControlIterations: 0),
            controlBytesPerIteration: 0,
            static () => Task.CompletedTask);

        Assert.Equal("sample.operation", result.Id);

        // Batches are kept rather than pooled, because the comparative bootstrap pairs batch to batch.
        Assert.Equal(3, result.Batches.Length);

        Assert.All(result.Batches, static batch => Assert.Equal(4, batch.Length));

        Assert.True(result.P50Microseconds <= result.P95Microseconds);

        Assert.True(result.P95Microseconds <= result.P99Microseconds);

    }

}
