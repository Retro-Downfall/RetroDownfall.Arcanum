using RetroDownfall.Arcanum.Core.Performance;

namespace RetroDownfall.Arcanum.Tests.Benchmarks;

/// <summary>
/// The absolute gate's exit mapping, which is the whole of what CI reads.
/// </summary>
/// <remarks>
/// This arithmetic used to live in the benchmark host, which is outside the solution and outside
/// <c>dotnet test</c>. Nothing compiled it against an assertion, so replacing the decision with
/// <c>return 0</c> or inverting the ceiling comparison left every lane green forever. The exit code
/// is the contract between the gate and the workflow, so it is asserted here one verdict at a time.
/// </remarks>
public sealed class CovenantBenchmarkGateTests
{

    private const string Corpus = "da36b50984f67ef5b6dc12cbb3d7a6efe3d186bc52863b7dc2b441d8b27476d2";

    private const string Fingerprint = "6a2f0c1b8e4d7395a1c60f2b8d4e7a95c3081f6b2d5e8a47c90b1f3e6d2a5c84";

    [Theory]

    [InlineData("a p95 above its stated ceiling", 1)]

    [InlineData("an allocation correction below the control", 1)]

    [InlineData("a control too noisy to subtract", 1)]

    [InlineData("a ceiling the manifest never states", 2)]

    [InlineData("an operation the run never measured", 2)]

    [InlineData("a run inside every stated bound", 0)]

    public void The_gate_reports_a_breach_and_an_unmeasurable_run_as_different_exit_codes(
        string verdict,
        int expected)
    {

        (BenchmarkRun run, WorkloadOperation[] operations) = Scenario(verdict);

        using StringWriter report = new();

        Assert.Equal(expected, BenchmarkGate.Evaluate(run, operations, report));

        // A non-zero exit that said nothing would leave an operator with a failed lane and no reason
        // for it; a zero exit that reported a breach would be the same defect read the other way.
        Assert.Equal(expected != 0, report.ToString().Length > 0);

    }

    [Fact]
    public void A_run_measuring_an_operation_the_manifest_does_not_name_is_not_gated_by_it()
    {

        BenchmarkRun run = Run([Measured("turn.plan", 601, 1501, 200_000)]);

        using StringWriter report = new();

        // The manifest is the list of what gates, not the run. An operation measured for information
        // and left out of the manifest must not fail a lane, or adding a diagnostic number to the host
        // would break the build.
        Assert.Equal(0, BenchmarkGate.Evaluate(run, [], report));

    }

    private static (BenchmarkRun Run, WorkloadOperation[] Operations) Scenario(string verdict) => verdict switch
    {

        "a p95 above its stated ceiling" =>
            (Run([Measured("turn.plan", 601, 1400, 150_000)]), [Ceilings("turn.plan")]),

        "an allocation correction below the control" =>
            (Run([Measured("turn.plan", 500, 1400, -1)]), [Ceilings("turn.plan")]),

        "a control too noisy to subtract" =>
            (Run([Measured("turn.plan", 500, 1400, 150_000)], controlAcceptable: false), [Ceilings("turn.plan")]),

        "a ceiling the manifest never states" =>
            (Run([Measured("turn.plan", 500, 1400, 150_000)]), [Ceilings("turn.plan", p95: null)]),

        "an operation the run never measured" =>
            (Run([Measured("turn.plan", 500, 1400, 150_000)]), [Ceilings("turn.plan"), Ceilings("status.census")]),

        "a run inside every stated bound" =>
            (Run([Measured("turn.plan", 500, 1400, 150_000)]), [Ceilings("turn.plan")]),

        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "No such gate scenario."),

    };

    private static BenchmarkRun Run(
        BenchmarkOperationResult[] operations,
        bool controlAcceptable = true) =>
        new(
            "covenant-workload-v1",
            1,
            "osx-arm64",
            Corpus,
            Fingerprint,
            operations,
            new BenchmarkControlResult(
                SpreadBytes: 64,
                MedianAbsoluteDeviationBytes: 16,
                NegativeFraction: 0,
                Acceptable: controlAcceptable));

    private static BenchmarkOperationResult Measured(string id, double p95, double p99, double allocation) =>
        new(id, p95 / 2, p95, p99, allocation, [[p95]]);

    private static WorkloadOperation Ceilings(
        string id,
        double? p95 = 600,
        double? p99 = 1500,
        long? allocation = 160_000) =>
        new(id, p95, p99, allocation);

}
