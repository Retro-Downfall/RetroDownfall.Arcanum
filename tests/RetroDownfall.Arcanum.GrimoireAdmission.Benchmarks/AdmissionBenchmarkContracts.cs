using System.Text.Json.Serialization;

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
    [property: JsonRequired] string Operation,
    [property: JsonRequired] string Concurrency,
    [property: JsonRequired] int Workers,
    [property: JsonRequired] long WarmupOperationCount,
    [property: JsonRequired] long LatencyBundleCount,
    [property: JsonRequired] long LatencyOperationCount,
    [property: JsonRequired] long ThroughputOperationCount,
    [property: JsonRequired] double OperationsPerSecond,
    [property: JsonRequired] double P50Nanoseconds,
    [property: JsonRequired] double P95Nanoseconds,
    [property: JsonRequired] double P99Nanoseconds,
    [property: JsonRequired] long AllocatedBytes,
    [property: JsonRequired] long AllocationOperationCount,
    [property: JsonRequired] double BytesPerOperation,
    [property: JsonRequired] int Gen0Collections,
    [property: JsonRequired] long LockContentions,
    [property: JsonRequired] long SuccessCount,
    [property: JsonRequired] long FailureCount,
    [property: JsonRequired] long Checksum,
    [property: JsonRequired] long MaterializedTerminalCallbackDelta);

internal sealed record AdmissionBenchmarkFinalState(
    [property: JsonRequired] long LiveRequests,
    [property: JsonRequired] long LiveWork,
    [property: JsonRequired] long LiveOpens,
    [property: JsonRequired] long LiveEffects,
    [property: JsonRequired] long LiveWaiters,
    [property: JsonRequired] bool DrainSucceeded,
    [property: JsonRequired] bool ReopenSucceeded);

internal sealed record AdmissionBenchmarkHistoricalChurnResult(
    [property: JsonRequired] int DisposedAdmissions,
    [property: JsonRequired] long RetainedBytesBeforeClose,
    [property: JsonRequired] long RetainedBytesAfterClose,
    [property: JsonRequired] double CloseNanoseconds,
    [property: JsonRequired] bool DrainSucceeded);

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
