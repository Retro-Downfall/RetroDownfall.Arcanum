namespace RetroDownfall.Arcanum.Core.Performance;

/// <summary>
/// The absolute bounds a run has to clear, independent of any baseline.
/// </summary>
/// <remarks>
/// Absolute ceilings are the authoritative half of the gate. A comparison against a baseline only
/// says a revision did not make things worse, which stays true all the way down a long slow slide of
/// individually-acceptable steps; a ceiling says what the product may cost.
///
/// <para>The arithmetic lives in Core rather than in the benchmark host so that the exit mapping is
/// something a test can call. In the host it was the only decision surface in the repository with no
/// caller under test: replacing this method's body with <c>return 0</c> left every lane green.</para>
/// </remarks>
internal static class BenchmarkGate
{

    internal static int Evaluate(
        BenchmarkRun run,
        IReadOnlyList<WorkloadOperation> operations,
        TextWriter report)
    {

        ArgumentNullException.ThrowIfNull(run);

        ArgumentNullException.ThrowIfNull(operations);

        ArgumentNullException.ThrowIfNull(report);

        int exit = 0;

        foreach (WorkloadOperation operation in operations)
        {

            BenchmarkOperationResult? measured = run.Operations
                .FirstOrDefault(entry => string.Equals(entry.Id, operation.Id, StringComparison.Ordinal));

            if (measured is null)
            {

                report.WriteLine($"  {operation.Id}: the manifest names it and the run did not measure it.");

                exit = Math.Max(exit, 2);

                continue;

            }

            // An unset ceiling fails the run rather than passing it. Absent and unlimited read the
            // same in the file and opposite in the results, and the one that ships quietly is wrong.
            if (operation.P95CeilingMicroseconds is null
                || operation.P99CeilingMicroseconds is null
                || operation.AllocationCeilingBytes is null)
            {

                report.WriteLine($"  {operation.Id}: the manifest states no ceiling, so this run gates nothing.");

                exit = Math.Max(exit, 2);

                continue;

            }

            exit = Math.Max(exit, Check(
                report,
                operation.Id,
                "p95",
                measured.P95Microseconds,
                operation.P95CeilingMicroseconds.Value,
                "us"));

            exit = Math.Max(exit, Check(
                report,
                operation.Id,
                "p99",
                measured.P99Microseconds,
                operation.P99CeilingMicroseconds.Value,
                "us"));

            exit = Math.Max(exit, Check(
                report,
                operation.Id,
                "alloc",
                measured.AllocationBytes,
                operation.AllocationCeilingBytes.Value,
                "B"));

            // A correction below zero says the operation allocated less than the loop around it, which
            // means the subtraction is not measuring what it claims to. It is a failure, not a small
            // number, and it has to be named rather than left to read as a very good result.
            if (measured.AllocationBytes < 0)
            {

                report.WriteLine(
                    $"  {operation.Id} alloc: {measured.AllocationBytes:F0}B is below the control, "
                    + "so the paired subtraction is not measuring this operation.");

                exit = Math.Max(exit, 1);

            }

        }

        if (!run.Control.Acceptable)
        {

            // The allocation numbers are differences against this control. A noisy control turns a
            // difference of two large numbers into a measurement of the noise, so the run is failed
            // rather than reported with a caveat nobody reads.
            report.WriteLine(
                $"  control: spread {run.Control.SpreadBytes:F0}B, "
                + $"MAD {run.Control.MedianAbsoluteDeviationBytes:F0}B, "
                + $"negative {run.Control.NegativeFraction:P1} - too noisy to subtract.");

            exit = Math.Max(exit, 1);

        }

        return exit;

    }

    private static int Check(
        TextWriter report,
        string id,
        string statistic,
        double measured,
        double ceiling,
        string unit)
    {

        if (measured <= ceiling)
        {

            return 0;

        }

        report.WriteLine(
            $"  {id} {statistic}: {measured:F1}{unit} exceeds the {ceiling:F1}{unit} ceiling.");

        return 1;

    }

}
