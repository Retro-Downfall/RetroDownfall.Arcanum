using RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Benchmarks;

public sealed class GrimoireAdmissionBenchmarkComparisonTests
{
    [Fact]
    public void Six_counterbalanced_pairs_accept_only_the_predeclared_mixed_cell()
    {
        AdmissionBenchmarkEvidenceBundle evidence = Evidence();

        AdmissionBenchmarkComparisonReport report = AdmissionBenchmarkComparison.Compare(
            AdmissionBenchmarkManifest.CreateDefault(),
            evidence);

        Assert.True(report.Valid, string.Join(global::System.Environment.NewLine, report.Reasons));

        Assert.True(report.Accepted, string.Join(global::System.Environment.NewLine, report.Reasons));

        Assert.Equal(1.25, report.MixedThroughputPointRatio, 10);

        Assert.Equal(1.25, report.MixedThroughputLowerBound, 10);

        Assert.Equal(1, report.EfP99UpperBound, 10);
    }

    [Theory]
    [InlineData("p50")]
    [InlineData("p99")]
    [InlineData("ef-universal-p99")]
    [InlineData("ef-p99")]
    [InlineData("direct-allocation")]
    [InlineData("ef-allocation")]
    [InlineData("ef-allocation-two")]
    [InlineData("mixed-point")]
    [InlineData("mixed-lower")]
    public void Every_acceptance_requirement_rejects_independently(string breakName)
    {
        AdmissionBenchmarkEvidenceBundle evidence = Evidence(breakName);

        AdmissionBenchmarkComparisonReport report = AdmissionBenchmarkComparison.Compare(
            AdmissionBenchmarkManifest.CreateDefault(),
            evidence);

        Assert.True(report.Valid, string.Join(global::System.Environment.NewLine, report.Reasons));

        Assert.False(report.Accepted);

        string expectedReasonPrefix = breakName switch
        {
            "ef-universal-p99" => "p99",
            "ef-allocation-two" => "ef-allocation",
            _ => breakName,
        };

        Assert.Contains(report.Reasons, reason => reason.StartsWith(expectedReasonPrefix, StringComparison.Ordinal));
    }

    [Fact]
    public void Zero_allocations_are_compared_exactly_and_contention_is_diagnostic_only()
    {
        AdmissionBenchmarkEvidenceBundle evidence = Evidence(
            baseAllocation: 0,
            candidateAllocation: 0,
            baseContention: 0,
            candidateContention: long.MaxValue);

        AdmissionBenchmarkComparisonReport report = AdmissionBenchmarkComparison.Compare(
            AdmissionBenchmarkManifest.CreateDefault(),
            evidence);

        Assert.True(report.Valid);

        Assert.True(report.Accepted, string.Join(global::System.Environment.NewLine, report.Reasons));
    }

    [Fact]
    public void Finite_extreme_mixed_ratios_produce_a_finite_overflow_safe_average()
    {
        AdmissionBenchmarkComparisonReport report = AdmissionBenchmarkComparison.Compare(
            AdmissionBenchmarkManifest.CreateDefault(),
            Evidence("overflowed-average"));

        Assert.True(report.Valid, string.Join(global::System.Environment.NewLine, report.Reasons));

        Assert.True(double.IsFinite(report.MixedThroughputPointRatio));

        Assert.Equal(double.MaxValue, report.MixedThroughputPointRatio);
    }

    [Theory]
    [InlineData("missing-cell")]
    [InlineData("duplicate-cell")]
    [InlineData("negative")]
    [InlineData("non-finite")]
    [InlineData("zero-throughput")]
    [InlineData("wrong-order")]
    [InlineData("five-pairs")]
    [InlineData("wrong-profile")]
    [InlineData("wrong-success-count")]
    [InlineData("wrong-checksum")]
    [InlineData("overflowed-derived")]
    [InlineData("warmup-count")]
    [InlineData("latency-bundle-count")]
    [InlineData("latency-operation-count")]
    [InlineData("throughput-count")]
    [InlineData("allocation-denominator")]
    [InlineData("derived-bytes")]
    [InlineData("wrong-worker-count")]
    [InlineData("terminal-waiter")]
    [InlineData("wrong-final-live")]
    [InlineData("wrong-final-reopen")]
    [InlineData("missing-churn")]
    [InlineData("wrong-churn-count")]
    [InlineData("failed-churn-drain")]
    public void Malformed_or_incomplete_evidence_is_invalid_not_a_measured_rejection(string breakName)
    {
        AdmissionBenchmarkEvidenceBundle evidence = Evidence(breakName);

        AdmissionBenchmarkComparisonReport report = AdmissionBenchmarkComparison.Compare(
            AdmissionBenchmarkManifest.CreateDefault(),
            evidence);

        Assert.False(report.Valid);

        Assert.False(report.Accepted);

        Assert.Equal(2, report.ExitCode);
    }

    private static AdmissionBenchmarkEvidenceBundle Evidence(
        string? breakName = null,
        long baseAllocation = 16,
        long candidateAllocation = 16,
        long baseContention = 0,
        long candidateContention = 0)
    {
        List<AdmissionBenchmarkPair> pairs = [];

        for (int pair = 0; pair < 6; pair++)
        {
            string firstRole = pair % 2 == 0 ? "B" : "C";

            string secondRole = pair % 2 == 0 ? "C" : "B";

            AdmissionBenchmarkRevisionRun baseline = Run(
                "B",
                pair,
                firstRole == "B" ? 0 : 1,
                100,
                baseAllocation,
                baseContention);

            AdmissionBenchmarkRevisionRun candidate = Run(
                "C",
                pair,
                firstRole == "C" ? 0 : 1,
                125,
                candidateAllocation,
                candidateContention);

            pairs.Add(new(pair, firstRole, secondRole, baseline, candidate));
        }

        AdmissionBenchmarkEvidenceBundle evidence = new("session", pairs.ToArray());

        return breakName switch
        {
            "p50" => MutateCell(evidence, "request.finite", "one", static cell => cell with { P50Nanoseconds = 106, P95Nanoseconds = 106, P99Nanoseconds = 106 }),
            "p99" => MutateCell(evidence, "request.finite", "two", static cell => cell with { P99Nanoseconds = 111 }),
            "ef-universal-p99" => MutateCell(evidence, "ef.pooled", "two", static cell => cell with { P99Nanoseconds = 111 }),
            "ef-p99" => MutateCell(evidence, "ef.pooled", "one", static cell => cell with { P99Nanoseconds = 106 }),
            "direct-allocation" => MutateCell(evidence, "request.finite", "two", static cell => cell with
            {
                AllocatedBytes = checked(17 * cell.AllocationOperationCount),
                BytesPerOperation = 17,
            }),
            "ef-allocation" => MutateCell(evidence, "ef.pooled", "one", static cell => cell with
            {
                AllocatedBytes = checked(17 * cell.AllocationOperationCount),
                BytesPerOperation = 17,
            }),
            "ef-allocation-two" => MutateCell(evidence, "ef.pooled", "two", static cell => cell with
            {
                AllocatedBytes = checked(17 * cell.AllocationOperationCount),
                BytesPerOperation = 17,
            }),
            "terminal-waiter" => MutateCell(evidence, "request.finite", "one", static cell => cell with { MaterializedTerminalCallbackDelta = 1 }),
            "mixed-point" => MutateCell(evidence, "ordinary.mixed", "logical", static cell => cell with { OperationsPerSecond = 119 }),
            "mixed-lower" => MutatePairCells(evidence, [101d, 101d, 101d, 149d, 149d, 149d]),
            "missing-cell" => evidence with { Pairs = evidence.Pairs.Select(static pair => pair with { Candidate = pair.Candidate with { Cells = pair.Candidate.Cells[1..] } }).ToArray() },
            "duplicate-cell" => evidence with { Pairs = evidence.Pairs.Select(static pair => pair with { Candidate = pair.Candidate with { Cells = [.. pair.Candidate.Cells, pair.Candidate.Cells[0]] } }).ToArray() },
            "negative" => MutateCell(evidence, "request.finite", "one", static cell => cell with { P95Nanoseconds = -1 }),
            "non-finite" => MutateCell(evidence, "request.finite", "one", static cell => cell with { P95Nanoseconds = double.NaN }),
            "zero-throughput" => MutateCell(evidence, "ordinary.mixed", "logical", static cell => cell with { OperationsPerSecond = 0 }),
            "wrong-order" => evidence with { Pairs = evidence.Pairs.Select(static pair => pair with { FirstRole = "B", SecondRole = "C" }).ToArray() },
            "five-pairs" => evidence with { Pairs = evidence.Pairs[..5] },
            "wrong-profile" => evidence with { Pairs = evidence.Pairs.Select(static pair => pair with { Candidate = pair.Candidate with { Profile = "smoke" } }).ToArray() },
            "wrong-success-count" => MutateCell(evidence, "request.finite", "one", static cell => cell with { SuccessCount = 99 }),
            "wrong-checksum" => MutateCell(evidence, "request.finite", "one", static cell => cell with { Checksum = 0 }),
            "overflowed-derived" => MutateCell(
                MutateBaselineCell(evidence, "ordinary.mixed", "logical", static cell => cell with { OperationsPerSecond = double.Epsilon }),
                "ordinary.mixed",
                "logical",
                static cell => cell with { OperationsPerSecond = double.MaxValue }),
            "overflowed-average" => MutateCell(
                MutateBaselineCell(evidence, "ordinary.mixed", "logical", static cell => cell with { OperationsPerSecond = 1 }),
                "ordinary.mixed",
                "logical",
                static cell => cell with { OperationsPerSecond = double.MaxValue }),
            "warmup-count" => MutateCell(evidence, "request.finite", "one", static cell => cell with { WarmupOperationCount = cell.WarmupOperationCount - 1 }),
            "latency-bundle-count" => MutateCell(evidence, "request.finite", "one", static cell => cell with { LatencyBundleCount = cell.LatencyBundleCount - 1 }),
            "latency-operation-count" => MutateCell(evidence, "request.finite", "one", static cell => cell with { LatencyOperationCount = cell.LatencyOperationCount - 1 }),
            "throughput-count" => MutateCell(evidence, "request.finite", "one", static cell => cell with { ThroughputOperationCount = cell.ThroughputOperationCount - 1 }),
            "allocation-denominator" => MutateCell(evidence, "request.finite", "one", static cell => cell with { AllocationOperationCount = cell.AllocationOperationCount - 1 }),
            "derived-bytes" => MutateCell(evidence, "request.finite", "one", static cell => cell with { BytesPerOperation = cell.BytesPerOperation + 0.5 }),
            "wrong-worker-count" => MutateCell(evidence, "request.finite", "one", static cell => cell with { Workers = 2 }),
            "wrong-final-live" => evidence with
            {
                Pairs = evidence.Pairs.Select(static pair => pair with
                {
                    Candidate = pair.Candidate with
                    {
                        FinalState = pair.Candidate.FinalState with { LiveWaiters = 1 },
                    },
                }).ToArray(),
            },
            "wrong-final-reopen" => evidence with
            {
                Pairs = evidence.Pairs.Select(static pair => pair with
                {
                    Candidate = pair.Candidate with
                    {
                        FinalState = pair.Candidate.FinalState with { ReopenSucceeded = false },
                    },
                }).ToArray(),
            },
            "missing-churn" => evidence with
            {
                Pairs = evidence.Pairs.Select(static pair => pair with
                {
                    Candidate = pair.Candidate with { HistoricalChurn = [] },
                }).ToArray(),
            },
            "wrong-churn-count" => evidence with
            {
                Pairs = evidence.Pairs.Select(static pair => pair with
                {
                    Candidate = pair.Candidate with
                    {
                        HistoricalChurn =
                        [
                            pair.Candidate.HistoricalChurn[0],
                            pair.Candidate.HistoricalChurn[1] with { DisposedAdmissions = 63 },
                            pair.Candidate.HistoricalChurn[2],
                        ],
                    },
                }).ToArray(),
            },
            "failed-churn-drain" => evidence with
            {
                Pairs = evidence.Pairs.Select(static pair => pair with
                {
                    Candidate = pair.Candidate with
                    {
                        HistoricalChurn =
                        [
                            pair.Candidate.HistoricalChurn[0],
                            pair.Candidate.HistoricalChurn[1] with { DrainSucceeded = false },
                            pair.Candidate.HistoricalChurn[2],
                        ],
                    },
                }).ToArray(),
            },
            _ => evidence,
        };
    }

    private static AdmissionBenchmarkRevisionRun Run(
        string role,
        int pair,
        int order,
        double mixedThroughput,
        long allocation,
        long contention)
    {
        List<AdmissionBenchmarkCellResult> cells = [];

        AdmissionBenchmarkProfile profile = AdmissionBenchmarkManifest.CreateDefault().Profiles[0];

        foreach (string operation in AdmissionBenchmarkOperations.All)
        {
            foreach ((string concurrency, int workers) in new[]
            {
                ("one", 1),
                ("two", 2),
                ("eight", 8),
                ("logical", 4),
            })
            {
                cells.Add(
                    new(
                        operation,
                        concurrency,
                        workers,
                        profile.WarmupIterations,
                        profile.LatencySampleCount,
                        checked((long)profile.LatencySampleCount * profile.LatencyBundleSize),
                        profile.ThroughputIterations,
                        operation == "ordinary.mixed" && concurrency == "logical"
                            ? mixedThroughput
                            : 100,
                        100,
                        100,
                        100,
                        checked(allocation * profile.ThroughputIterations),
                        profile.ThroughputIterations,
                        allocation,
                        0,
                        contention,
                        AdmissionBenchmarkExpected.OperationCount(profile),
                        0,
                        AdmissionBenchmarkExpected.Checksum(profile, operation, workers),
                        0));
            }
        }

        return new(
            role == "B" ? "base-sha" : "candidate-sha",
            true,
            "session",
            pair,
            order,
            role,
            "qualification",
            InputIdentity(),
            EnvironmentIdentity(),
            cells.ToArray(),
            new(0, 0, 0, 0, 0, true, true),
            0)
        {
            HistoricalChurn =
            [
                new(0, 0, 0, 1, true),
                new(64, 0, 0, 1, true),
                new(640, 0, 0, 1, true),
            ],
        };
    }

    private static AdmissionBenchmarkInputIdentity InputIdentity() =>
        new(
            "catalog-shape",
            "immutable",
            "manifest",
            "harness",
            "project",
            "lock",
            "toolchain",
            "native-manifest",
            "native-binary",
            [new("src/example.cs", "source", true)]);

    private static AdmissionBenchmarkEnvironmentIdentity EnvironmentIdentity() =>
        new(
            "osx-arm64",
            "Arm64",
            "Arm64",
            "macOS",
            ".NET 10",
            "10.0.100",
            "CPU",
            4,
            1_000_000_000,
            false,
            false,
            false);

    private static AdmissionBenchmarkEvidenceBundle MutateCell(
        AdmissionBenchmarkEvidenceBundle evidence,
        string operation,
        string concurrency,
        Func<AdmissionBenchmarkCellResult, AdmissionBenchmarkCellResult> mutation) =>
        evidence with
        {
            Pairs = evidence.Pairs.Select(
                pair => pair with
                {
                    Candidate = pair.Candidate with
                    {
                        Cells = pair.Candidate.Cells.Select(
                            cell => cell.Operation == operation && cell.Concurrency == concurrency
                                ? mutation(cell)
                                : cell).ToArray(),
                    },
                }).ToArray(),
        };

    private static AdmissionBenchmarkEvidenceBundle MutatePairCells(
        AdmissionBenchmarkEvidenceBundle evidence,
        IReadOnlyList<double> throughputs) =>
        evidence with
        {
            Pairs = evidence.Pairs.Select(
                (pair, index) => pair with
                {
                    Candidate = pair.Candidate with
                    {
                        Cells = pair.Candidate.Cells.Select(
                            cell => cell.Operation == "ordinary.mixed" && cell.Concurrency == "logical"
                                ? cell with { OperationsPerSecond = throughputs[index] }
                                : cell).ToArray(),
                    },
                }).ToArray(),
        };

    private static AdmissionBenchmarkEvidenceBundle MutateBaselineCell(
        AdmissionBenchmarkEvidenceBundle evidence,
        string operation,
        string concurrency,
        Func<AdmissionBenchmarkCellResult, AdmissionBenchmarkCellResult> mutation) =>
        evidence with
        {
            Pairs = evidence.Pairs.Select(
                pair => pair with
                {
                    Baseline = pair.Baseline with
                    {
                        Cells = pair.Baseline.Cells.Select(
                            cell => cell.Operation == operation && cell.Concurrency == concurrency
                                ? mutation(cell)
                                : cell).ToArray(),
                    },
                }).ToArray(),
        };
}
