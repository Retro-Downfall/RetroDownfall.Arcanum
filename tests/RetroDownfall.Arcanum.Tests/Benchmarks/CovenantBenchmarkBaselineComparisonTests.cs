using RetroDownfall.Arcanum.Core.Performance;

namespace RetroDownfall.Arcanum.Tests.Benchmarks;

/// <summary>
/// What a comparison against a recorded baseline refuses before it reports a ratio.
/// </summary>
/// <remarks>
/// The comparison used to refuse on the corpus digest alone. Everything else the run records was
/// written and never read again: a baseline recorded on another runtime identifier, under another
/// workload, under another schema, or carrying no operations at all compared silently and exited
/// zero. A comparison that cannot be trusted is worse than none, because its output reads exactly
/// like one that can.
/// </remarks>
public sealed class CovenantBenchmarkBaselineComparisonTests
{

    private const string Corpus = "da36b50984f67ef5b6dc12cbb3d7a6efe3d186bc52863b7dc2b441d8b27476d2";

    private const string Fingerprint = "6a2f0c1b8e4d7395a1c60f2b8d4e7a95c3081f6b2d5e8a47c90b1f3e6d2a5c84";

    [Fact]
    public void A_baseline_that_could_not_be_read_refuses_rather_than_passing()
    {

        using StringWriter report = new();

        Assert.Equal(2, BenchmarkBaselineComparison.Compare(Run(), baseline: null, report));

    }

    [Fact]
    public void A_baseline_recorded_on_another_host_refuses_even_when_the_corpus_matches()
    {

        using StringWriter report = new();

        // Pairing cancels one machine's drift and cannot cancel the difference between two machines,
        // so this comparison would report the runner as the code change.
        Assert.Equal(
            2,
            BenchmarkBaselineComparison.Compare(Run(), Run(runtimeIdentifier: "linux-x64"), report));

        Assert.Contains("linux-x64", report.ToString(), StringComparison.Ordinal);

    }

    [Fact]
    public void A_baseline_recorded_under_another_workload_refuses()
    {

        using StringWriter report = new();

        Assert.Equal(
            2,
            BenchmarkBaselineComparison.Compare(Run(), Run(workloadId: "covenant-workload-v2"), report));

    }

    [Fact]
    public void A_baseline_recorded_under_another_schema_refuses()
    {

        using StringWriter report = new();

        Assert.Equal(2, BenchmarkBaselineComparison.Compare(Run(), Run(schemaVersion: 2), report));

    }

    [Fact]
    public void A_baseline_recorded_against_another_corpus_refuses()
    {

        using StringWriter report = new();

        Assert.Equal(2, BenchmarkBaselineComparison.Compare(Run(), Run(corpusDigest: new string('0', 64)), report));

    }

    [Fact]
    public void A_baseline_recorded_under_another_measurement_block_refuses()
    {

        using StringWriter report = new();

        // The corpus digest is taken at seed time, so two runs measured under different batch counts
        // or a different operation order carry the same one. This is the half that catches that.
        Assert.Equal(2, BenchmarkBaselineComparison.Compare(Run(), Run(manifestDigest: new string('1', 64)), report));

    }

    [Fact]
    public void A_baseline_carrying_no_operations_refuses_rather_than_comparing_nothing()
    {

        using StringWriter report = new();

        Assert.Equal(2, BenchmarkBaselineComparison.Compare(Run(), Run(operationIds: []), report));

    }

    [Fact]
    public void A_baseline_missing_an_operation_the_run_measured_refuses()
    {

        using StringWriter report = new();

        Assert.Equal(
            2,
            BenchmarkBaselineComparison.Compare(Run(), Run(operationIds: ["turn.plan"]), report));

    }

    [Fact]
    public void An_operation_only_the_baseline_carries_refuses_as_well()
    {

        using StringWriter report = new();

        // The old loop walked the run's operations and never noticed one the baseline had and the run
        // did not, so dropping an operation from the host disarmed its comparison with no signal.
        Assert.Equal(
            2,
            BenchmarkBaselineComparison.Compare(
                Run(operationIds: ["turn.plan"]),
                Run(operationIds: ["turn.plan", "status.census"]),
                report));

    }

    [Fact]
    public void A_run_that_matches_its_baseline_compares_clean()
    {

        using StringWriter report = new();

        Assert.Equal(0, BenchmarkBaselineComparison.Compare(Run(), Run(), report));

        Assert.Contains("turn.plan", report.ToString(), StringComparison.Ordinal);

    }

    [Fact]
    public void A_run_half_again_as_slow_as_its_baseline_is_a_regression()
    {

        using StringWriter report = new();

        Assert.Equal(
            1,
            BenchmarkBaselineComparison.Compare(Run(scale: 1.5), Run(), report));

        Assert.Contains("REGRESSION", report.ToString(), StringComparison.Ordinal);

    }

    private static BenchmarkRun Run(
        string workloadId = "covenant-workload-v1",
        int schemaVersion = 1,
        string runtimeIdentifier = "osx-arm64",
        string corpusDigest = Corpus,
        string manifestDigest = Fingerprint,
        string[]? operationIds = null,
        double scale = 1.0) =>
        new(
            workloadId,
            schemaVersion,
            runtimeIdentifier,
            corpusDigest,
            manifestDigest,
            [.. (operationIds ?? ["turn.plan", "status.census"]).Select(id => Measured(id, scale))],
            new BenchmarkControlResult(
                SpreadBytes: 64,
                MedianAbsoluteDeviationBytes: 16,
                NegativeFraction: 0,
                Acceptable: true));

    private static BenchmarkOperationResult Measured(string id, double scale)
    {

        double[][] batches = new double[6][];

        for (int batch = 0; batch < batches.Length; batch++)
        {

            double[] samples = new double[8];

            for (int index = 0; index < samples.Length; index++)
            {

                samples[index] = (100 + batch + index) * scale;

            }

            batches[batch] = samples;

        }

        return new BenchmarkOperationResult(id, 100 * scale, 110 * scale, 115 * scale, 1024, batches);

    }

}
