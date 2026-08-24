using System.Text.Json;

using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Covenant.Benchmarks;

/// <summary>
/// The pinned workload every Covenant benchmark number is produced against.
/// </summary>
/// <remarks>
/// Read from the embedded copy rather than from disk. A gate that read a manifest beside the binary
/// would let an operator move a ceiling by editing a file next to the executable, and a baseline
/// recorded under one workload would silently compare against a candidate measured under another.
/// </remarks>
public sealed record WorkloadManifest(
    int SchemaVersion,
    string WorkloadId,
    WorkloadCorpus Corpus,
    WorkloadMeasurement Measurement,
    WorkloadOperation[] Operations,
    WorkloadComparison Comparison);

public sealed record WorkloadCorpus(
    int GlobalConfirmedEntries,
    int CampaignConfirmedEntriesPerCampaign,
    int CampaignProposedEntriesPerCampaign,
    int Campaigns,
    string KeyTemplate,
    string ContentTemplate,
    string[] FillerWords,
    int FillerWordsPerEntry,
    string? CorpusDigest);

public sealed record WorkloadMeasurement(
    int WarmupIterations,
    int Batches,
    int IterationsPerBatch,
    int AllocationControlIterations);

/// <summary>
/// One measured operation and the absolute ceilings it may not cross.
/// </summary>
/// <remarks>
/// A null ceiling is an unset ceiling, and the gate refuses a run rather than treating it as
/// unlimited. Absent and infinite look identical to a reader of the file and opposite to a reader of
/// the results, and the one that ships silently is the wrong one.
/// </remarks>
public sealed record WorkloadOperation(
    string Id,
    double? P95CeilingMicroseconds,
    double? P99CeilingMicroseconds,
    long? AllocationCeilingBytes);

public sealed record WorkloadComparison(
    double ObservedRatioThreshold,
    double IntervalLowerBoundThreshold,
    int BootstrapReplicates,
    string PairedBootstrapSeed);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(WorkloadManifest))]
[JsonSerializable(typeof(BenchmarkRun))]
public sealed partial class BenchmarkJsonContext : JsonSerializerContext
{
}

/// <summary>One complete measured run, as the gate and a recorded baseline both read it.</summary>
public sealed record BenchmarkRun(
    string WorkloadId,
    int SchemaVersion,
    string RuntimeIdentifier,
    string CorpusDigest,
    BenchmarkOperationResult[] Operations,
    BenchmarkControlResult Control);

public sealed record BenchmarkOperationResult(
    string Id,
    double P50Microseconds,
    double P95Microseconds,
    double P99Microseconds,
    double AllocationBytes,

    /// <summary>Per-batch samples, kept so a comparison can pair batch to batch rather than pool them.</summary>
    double[][] Batches);

public sealed record BenchmarkControlResult(
    double SpreadBytes,
    double MedianAbsoluteDeviationBytes,
    double NegativeFraction,
    bool Acceptable);

public static class WorkloadManifestLoader
{

    public static WorkloadManifest Load()
    {

        using Stream stream = typeof(WorkloadManifestLoader).Assembly
            .GetManifestResourceStream(
                "RetroDownfall.Arcanum.Covenant.Benchmarks.covenant-workload-v1.json")
            ?? throw new InvalidOperationException("The embedded workload manifest is missing from this build.");

        return JsonSerializer.Deserialize(stream, BenchmarkJsonContext.Default.WorkloadManifest)
            ?? throw new InvalidOperationException("The embedded workload manifest is empty.");

    }

}
