namespace RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks;

internal static class AdmissionBenchmarkComparison
{

    internal static AdmissionBenchmarkComparisonReport Compare(
        AdmissionBenchmarkManifest manifest,
        AdmissionBenchmarkEvidenceBundle evidence)
    {

        ArgumentNullException.ThrowIfNull(manifest);

        ArgumentNullException.ThrowIfNull(evidence);

        List<string> invalid = ValidateShape(manifest, evidence);

        if (invalid.Count != 0)
        {

            return new(false, false, 2, 0, 0, 0, invalid.ToArray());

        }

        List<string> rejected = [];

        List<double> mixedRatios = [];

        List<double> efRatios = [];

        for (int pairIndex = 0; pairIndex < evidence.Pairs.Length; pairIndex++)
        {

            AdmissionBenchmarkPair pair = evidence.Pairs[pairIndex];

            Dictionary<(string Operation, string Concurrency), AdmissionBenchmarkCellResult> baseline = pair.Baseline.Cells.ToDictionary(static cell => (cell.Operation, cell.Concurrency));

            Dictionary<(string Operation, string Concurrency), AdmissionBenchmarkCellResult> candidate = pair.Candidate.Cells.ToDictionary(static cell => (cell.Operation, cell.Concurrency));

            foreach (((string operation, string concurrency), AdmissionBenchmarkCellResult baselineCell) in baseline)
            {

                AdmissionBenchmarkCellResult candidateCell = candidate[(operation, concurrency)];

                if (concurrency == "one"
                    && CostRatio(baselineCell.P50Nanoseconds, candidateCell.P50Nanoseconds) > manifest.Thresholds.SingleThreadP50MaximumRatio)
                {

                    rejected.Add($"p50: {operation}@{concurrency} exceeds the maximum ratio.");

                }

                if (operation != "ef.pooled"
                    && CostRatio(baselineCell.P99Nanoseconds, candidateCell.P99Nanoseconds) > manifest.Thresholds.P99MaximumRatio)
                {

                    rejected.Add($"p99: {operation}@{concurrency} exceeds the maximum ratio.");

                }

                if (AdmissionBenchmarkOperations.IsDirect(operation)
                    && candidateCell.BytesPerOperation > baselineCell.BytesPerOperation)
                {

                    rejected.Add($"direct-allocation: {operation}@{concurrency} allocated more bytes.");

                }

                if (candidateCell.MaterializedTerminalCallbackDelta != 0
                    || baselineCell.MaterializedTerminalCallbackDelta != 0)
                {

                    rejected.Add($"terminal-waiter: {operation}@{concurrency} materialized an ordinary terminal waiter.");

                }

            }

            AdmissionBenchmarkCellResult baseMixed = baseline[(manifest.MaterialImprovementOperation, manifest.MaterialImprovementConcurrency)];

            AdmissionBenchmarkCellResult candidateMixed = candidate[(manifest.MaterialImprovementOperation, manifest.MaterialImprovementConcurrency)];

            mixedRatios.Add(candidateMixed.OperationsPerSecond / baseMixed.OperationsPerSecond);

            AdmissionBenchmarkCellResult baseEf = baseline[("ef.pooled", "one")];

            AdmissionBenchmarkCellResult candidateEf = candidate[("ef.pooled", "one")];

            efRatios.Add(CostRatio(baseEf.P99Nanoseconds, candidateEf.P99Nanoseconds));

            if (CostRatio(baseEf.BytesPerOperation, candidateEf.BytesPerOperation) > manifest.Thresholds.EfAllocationMaximumRatio)
            {

                rejected.Add("ef-allocation: ef.pooled@one exceeds the allocation ratio.");

            }

        }

        double mixedPoint = mixedRatios.Average();

        double mixedLower = BootstrapQuantile(
            mixedRatios,
            manifest.Bootstrap,
            manifest.Bootstrap.LowerQuantile);

        double efUpper = BootstrapQuantile(
            efRatios,
            manifest.Bootstrap,
            1 - manifest.Bootstrap.LowerQuantile);

        if (mixedPoint < manifest.Thresholds.MixedThroughputMinimumRatio)
        {

            rejected.Add("mixed-point: ordinary.mixed@logical point ratio is below the threshold.");

        }

        if (mixedLower < manifest.Thresholds.MixedThroughputMinimumRatio)
        {

            rejected.Add("mixed-lower: ordinary.mixed@logical bootstrap lower bound is below the threshold.");

        }

        if (efUpper > manifest.Thresholds.EfMaximumRatio)
        {

            rejected.Add("ef-p99: ef.pooled@one bootstrap upper bound exceeds the threshold.");

        }

        string[] reasons = rejected.Distinct(StringComparer.Ordinal).ToArray();

        bool accepted = reasons.Length == 0;

        return new(true, accepted, accepted ? 0 : 1, mixedPoint, mixedLower, efUpper, reasons);

    }

    private static List<string> ValidateShape(
        AdmissionBenchmarkManifest manifest,
        AdmissionBenchmarkEvidenceBundle evidence)
    {

        List<string> errors = [];

        if (string.IsNullOrWhiteSpace(evidence.SessionId))
        {

            errors.Add("The evidence session id is missing.");

        }

        if (evidence.Pairs is null || evidence.Pairs.Length != 6)
        {

            errors.Add("Qualification requires exactly six process pairs.");

            return errors;

        }

        HashSet<(string Operation, string Concurrency)> required =
        [
            .. manifest.Operations.SelectMany(
                _ => manifest.Concurrency,
                static (operation, concurrency) => (operation, concurrency.Id)),
        ];

        for (int index = 0; index < evidence.Pairs.Length; index++)
        {

            AdmissionBenchmarkPair pair = evidence.Pairs[index];

            string expectedFirst = index % 2 == 0 ? "B" : "C";

            string expectedSecond = index % 2 == 0 ? "C" : "B";

            if (pair.PairIndex != index
                || pair.FirstRole != expectedFirst
                || pair.SecondRole != expectedSecond
                || pair.Baseline.PairIndex != index
                || pair.Candidate.PairIndex != index
                || pair.Baseline.Role != "B"
                || pair.Candidate.Role != "C"
                || pair.Baseline.OrderPosition != (expectedFirst == "B" ? 0 : 1)
                || pair.Candidate.OrderPosition != (expectedFirst == "C" ? 0 : 1))
            {

                errors.Add($"Pair {index} does not match the counterbalanced order.");

            }

            ValidateRun(pair.Baseline, evidence.SessionId, required, errors);

            ValidateRun(pair.Candidate, evidence.SessionId, required, errors);

        }

        return errors;

    }

    private static void ValidateRun(
        AdmissionBenchmarkRevisionRun run,
        string sessionId,
        IReadOnlySet<(string Operation, string Concurrency)> required,
        ICollection<string> errors)
    {

        if (run.SessionId != sessionId || run.ExitCode != 0)
        {

            errors.Add($"Run {run.Role}/{run.PairIndex} has mismatched session or exit metadata.");

        }

        if (run.Cells is null)
        {

            errors.Add($"Run {run.Role}/{run.PairIndex} has no cells.");

            return;

        }

        HashSet<(string Operation, string Concurrency)> actual = [];

        foreach (AdmissionBenchmarkCellResult cell in run.Cells)
        {

            if (!actual.Add((cell.Operation, cell.Concurrency)))
            {

                errors.Add($"Run {run.Role}/{run.PairIndex} has a duplicate cell.");

            }

            if (cell.Workers <= 0
                || !PositiveFinite(cell.OperationsPerSecond)
                || !NonnegativeFinite(cell.P50Nanoseconds)
                || !NonnegativeFinite(cell.P95Nanoseconds)
                || !NonnegativeFinite(cell.P99Nanoseconds)
                || cell.P50Nanoseconds > cell.P95Nanoseconds
                || cell.P95Nanoseconds > cell.P99Nanoseconds
                || cell.BytesPerOperation < 0
                || cell.Gen0Collections < 0
                || cell.LockContentions < 0
                || cell.SuccessCount < 0
                || cell.FailureCount < 0
                || cell.FailureCount != 0
                || cell.SuccessCount + cell.FailureCount <= 0)
            {

                errors.Add($"Run {run.Role}/{run.PairIndex} has an invalid metric in {cell.Operation}@{cell.Concurrency}.");

            }

        }

        if (!actual.SetEquals(required))
        {

            errors.Add($"Run {run.Role}/{run.PairIndex} does not contain the exact required cells.");

        }

    }

    private static double BootstrapQuantile(
        IReadOnlyList<double> ratios,
        AdmissionBenchmarkBootstrap bootstrap,
        double quantile)
    {

        double[] means = new double[bootstrap.ReplicateCount];

        ulong state = bootstrap.Seed;

        for (int replicate = 0; replicate < means.Length; replicate++)
        {

            double sum = 0;

            for (int sample = 0; sample < ratios.Count; sample++)
            {

                state ^= state >> 12;

                state ^= state << 25;

                state ^= state >> 27;

                ulong random = state * 2685821657736338717UL;

                sum += ratios[(int)(random % (ulong)ratios.Count)];

            }

            means[replicate] = sum / ratios.Count;

        }

        Array.Sort(means);

        int rank = Math.Clamp((int)Math.Ceiling(quantile * means.Length) - 1, 0, means.Length - 1);

        return means[rank];

    }

    private static double CostRatio(double baseline, double candidate) =>
        baseline == 0
            ? candidate == 0 ? 1 : double.PositiveInfinity
            : candidate / baseline;

    private static bool PositiveFinite(double value) => value > 0 && double.IsFinite(value);

    private static bool NonnegativeFinite(double value) => value >= 0 && double.IsFinite(value);

}
