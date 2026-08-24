namespace RetroDownfall.Arcanum.Core.Performance;

/// <summary>
/// The comparative half of the gate: this run against a recorded baseline.
/// </summary>
/// <remarks>
/// Everything before the ratios is a refusal rather than a caveat. A comparison between two runs that
/// do not share a fingerprint is not a weaker answer than a comparison between two that do; it is a
/// different measurement reported in the vocabulary of a regression, and the reader has no way to
/// tell from the output which one they are looking at.
/// </remarks>
internal static class BenchmarkBaselineComparison
{

    internal static int Compare(BenchmarkRun run, BenchmarkRun? baseline, TextWriter report)
    {

        ArgumentNullException.ThrowIfNull(run);

        ArgumentNullException.ThrowIfNull(report);

        if (baseline is null)
        {

            report.WriteLine("The baseline could not be read.");

            return 2;

        }

        // Pairing cancels the drift one machine accumulates over a long run. It cannot cancel the
        // difference between two machines, so a baseline recorded elsewhere reports the other host as
        // a code regression, or hides a real one behind a faster one.
        if (!string.Equals(baseline.RuntimeIdentifier, run.RuntimeIdentifier, StringComparison.Ordinal))
        {

            report.WriteLine(
                $"The baseline was recorded on {baseline.RuntimeIdentifier} and this run is on "
                + $"{run.RuntimeIdentifier}; a cross-host comparison reports the host as the change.");

            return 2;

        }

        if (!string.Equals(baseline.WorkloadId, run.WorkloadId, StringComparison.Ordinal))
        {

            report.WriteLine(
                $"The baseline was recorded under workload {baseline.WorkloadId} and this run measured "
                + $"{run.WorkloadId}; the two are not the same experiment.");

            return 2;

        }

        if (baseline.SchemaVersion != run.SchemaVersion)
        {

            report.WriteLine(
                $"The baseline states schema {baseline.SchemaVersion} and this run states "
                + $"{run.SchemaVersion}; the fields do not necessarily mean the same thing.");

            return 2;

        }

        if (!string.Equals(baseline.CorpusDigest, run.CorpusDigest, StringComparison.Ordinal))
        {

            report.WriteLine("The baseline was recorded against a different corpus; the comparison would be meaningless.");

            return 2;

        }

        // The corpus digest is taken at seed time, so it pins the bytes and not the batch counts, the
        // operation order, or the ceilings. Two runs measured under different measurement blocks carry
        // identical corpus digests and are not comparable.
        if (!string.Equals(baseline.ManifestDigest, run.ManifestDigest, StringComparison.Ordinal))
        {

            report.WriteLine("The baseline was recorded under a different measurement block or operation order.");

            return 2;

        }

        if (baseline.Operations.Length == 0)
        {

            report.WriteLine("The baseline records no operations, so there is nothing to compare against.");

            return 2;

        }

        // Set equality in both directions. A baseline missing an operation used to print a skip line
        // and leave the exit code alone, which made an empty or truncated baseline indistinguishable
        // from a clean comparison; an operation only the baseline carries was never noticed at all.
        HashSet<string> recorded = new(
            baseline.Operations.Select(static entry => entry.Id),
            StringComparer.Ordinal);

        HashSet<string> measured = new(
            run.Operations.Select(static entry => entry.Id),
            StringComparer.Ordinal);

        if (!recorded.SetEquals(measured))
        {

            report.WriteLine(
                $"The baseline measured [{string.Join(", ", recorded.Order(StringComparer.Ordinal))}] and this "
                + $"run measured [{string.Join(", ", measured.Order(StringComparer.Ordinal))}].");

            return 2;

        }

        int exit = 0;

        foreach (BenchmarkOperationResult candidate in run.Operations)
        {

            BenchmarkOperationResult baselineOperation = baseline.Operations
                .First(entry => string.Equals(entry.Id, candidate.Id, StringComparison.Ordinal));

            BenchmarkRatioInterval interval = BenchmarkComparison.Compare(
                baselineOperation.Batches,
                candidate.Batches);

            report.WriteLine(
                $"  {candidate.Id,-18} ratio {interval.ObservedRatio:F3} "
                + $"[{interval.LowerBound:F3}, {interval.UpperBound:F3}]"
                + (interval.IsRegression ? "  REGRESSION" : string.Empty));

            if (interval.IsRegression)
            {

                exit = 1;

            }

        }

        return exit;

    }

}
