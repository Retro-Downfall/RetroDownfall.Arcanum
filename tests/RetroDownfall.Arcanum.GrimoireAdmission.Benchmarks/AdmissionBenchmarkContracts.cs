namespace RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks;

internal static class AdmissionBenchmarkOperations
{

    internal static readonly string[] All =
    [
        "request.finite",
        "request.quiesceable",
        "work.db-only",
        "work.effect",
        "open.failed",
        "request.open",
        "generation.read",
        "ordinary.mixed",
        "ef.pooled",
    ];

    internal static bool IsDirect(string operation) => operation != "ef.pooled";

}

internal sealed record AdmissionBenchmarkConcurrency(string Id, int Workers);

internal sealed record AdmissionBenchmarkProfile(
    string Name,
    int WarmupIterations,
    int LatencyBundleSize,
    int LatencySampleCount,
    int ThroughputIterations,
    int MaximumDurationSeconds,
    string[] MixedSchedule);

internal static class AdmissionBenchmarkExpected
{

    internal static long OperationCount(AdmissionBenchmarkProfile profile) =>
        checked((long)profile.WarmupIterations
            + ((long)profile.LatencySampleCount * profile.LatencyBundleSize)
            + profile.ThroughputIterations);

    internal static long Checksum(
        AdmissionBenchmarkProfile profile,
        string operation,
        int workers)
    {

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workers);

        if (operation != "ordinary.mixed")
        {

            return checked(OperationCount(profile) * Contribution(operation));

        }

        return checked(
            PhaseChecksum(profile.MixedSchedule, profile.WarmupIterations, 1, workers)
            + PhaseChecksum(profile.MixedSchedule, profile.LatencySampleCount, profile.LatencyBundleSize, workers)
            + PhaseChecksum(profile.MixedSchedule, profile.ThroughputIterations, 1, workers));

    }

    private static long PhaseChecksum(
        IReadOnlyList<string> schedule,
        int totalUnits,
        int bundleSize,
        int workers)
    {

        long total = 0;

        for (int worker = 0; worker < workers; worker++)
        {

            int units = totalUnits / workers + (worker < totalUnits % workers ? 1 : 0);

            long operationCount = checked((long)units * bundleSize);

            long cycleCount = operationCount / schedule.Count;

            int remainder = checked((int)(operationCount % schedule.Count));

            long cycleChecksum = schedule.Sum(static item => Contribution(item));

            total = checked(total + (cycleCount * cycleChecksum));

            for (int index = 0; index < remainder; index++)
            {

                total = checked(total + Contribution(schedule[(worker + index) % schedule.Count]));

            }

        }

        return total;

    }

    private static int Contribution(string operation) => operation switch
    {
        "request.finite" => 1,
        "request.quiesceable" => 2,
        "work.db-only" => 1,
        "work.effect" => 1,
        "open.failed" => 1,
        "request.open" => 2,
        "generation.read" => 1,
        "ef.pooled" => 1,
        _ => throw new InvalidDataException($"Unsupported benchmark operation '{operation}'."),
    };

}

internal sealed record AdmissionBenchmarkThresholds(
    double SingleThreadP50MaximumRatio,
    double P99MaximumRatio,
    double EfMaximumRatio,
    double EfAllocationMaximumRatio,
    double MixedThroughputMinimumRatio);

internal sealed record AdmissionBenchmarkBootstrap(
    string Algorithm,
    ulong Seed,
    int ReplicateCount,
    double LowerQuantile);

internal sealed record AdmissionBenchmarkDigestEntry(
    string Path,
    string Digest,
    bool Present);

internal sealed record AdmissionBenchmarkCatalogEntry(
    string Path,
    bool Optional);

internal sealed record AdmissionBenchmarkInputIdentity(
    string CatalogShapeDigest,
    string ImmutableContentDigest,
    string ManifestDigest,
    string HarnessDigest,
    string ProjectDigest,
    string LockfileDigest,
    string ToolchainDigest,
    string NativeManifestDigest,
    string NativeBinaryDigest,
    AdmissionBenchmarkDigestEntry[] Inputs);

internal sealed record AdmissionBenchmarkEnvironmentIdentity(
    string RuntimeIdentifier,
    string ProcessArchitecture,
    string OsArchitecture,
    string OsVersion,
    string RuntimeVersion,
    string SdkVersion,
    string CpuIdentity,
    int LogicalProcessorCount,
    long StopwatchFrequency,
    bool ServerGc,
    bool ConcurrentGc,
    bool DynamicCodeSupported);

internal sealed record AdmissionBenchmarkCellResult(
    string Operation,
    string Concurrency,
    int Workers,
    long WarmupOperationCount,
    long LatencyBundleCount,
    long LatencyOperationCount,
    long ThroughputOperationCount,
    double OperationsPerSecond,
    double P50Nanoseconds,
    double P95Nanoseconds,
    double P99Nanoseconds,
    long AllocatedBytes,
    long AllocationOperationCount,
    double BytesPerOperation,
    int Gen0Collections,
    long LockContentions,
    long SuccessCount,
    long FailureCount,
    long Checksum,
    long MaterializedTerminalCallbackDelta);

internal sealed record AdmissionBenchmarkFinalState(
    long LiveRequests,
    long LiveWork,
    long LiveOpens,
    long LiveEffects,
    long LiveWaiters,
    bool DrainSucceeded,
    bool ReopenSucceeded);

internal sealed record AdmissionBenchmarkHistoricalChurnResult(
    int DisposedAdmissions,
    long RetainedBytesBeforeClose,
    long RetainedBytesAfterClose,
    double CloseNanoseconds,
    bool DrainSucceeded);

internal sealed record AdmissionBenchmarkRevisionRun(
    string Revision,
    bool CleanTree,
    string SessionId,
    int PairIndex,
    int OrderPosition,
    string Role,
    string Profile,
    AdmissionBenchmarkInputIdentity Inputs,
    AdmissionBenchmarkEnvironmentIdentity Environment,
    AdmissionBenchmarkCellResult[] Cells,
    AdmissionBenchmarkFinalState FinalState,
    int ExitCode)
{

    public AdmissionBenchmarkHistoricalChurnResult[] HistoricalChurn { get; init; } = [];

}

internal sealed record AdmissionBenchmarkPair(
    int PairIndex,
    string FirstRole,
    string SecondRole,
    AdmissionBenchmarkRevisionRun Baseline,
    AdmissionBenchmarkRevisionRun Candidate);

internal sealed record AdmissionBenchmarkEvidenceBundle(
    string SessionId,
    AdmissionBenchmarkPair[] Pairs);

internal sealed record AdmissionBenchmarkComparisonReport(
    bool Valid,
    bool Accepted,
    int ExitCode,
    double MixedThroughputPointRatio,
    double MixedThroughputLowerBound,
    double EfP99UpperBound,
    string[] Reasons);
