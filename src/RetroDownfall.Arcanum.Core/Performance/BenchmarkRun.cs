namespace RetroDownfall.Arcanum.Core.Performance;

/// <summary>One complete measured run, as the gate and a recorded baseline both read it.</summary>
/// <remarks>
/// The first four fields are the run's fingerprint. A comparison is only meaningful between two runs
/// that agree on all of them: pairing cancels the drift one machine accumulates over a long run and
/// cannot cancel the difference between two machines, a schema change alters what the fields mean,
/// and either digest changing alters what was measured.
/// </remarks>
internal sealed record BenchmarkRun(
    string WorkloadId,
    int SchemaVersion,
    string RuntimeIdentifier,
    string CorpusDigest,
    string ManifestDigest,
    BenchmarkOperationResult[] Operations,
    BenchmarkControlResult Control);

internal sealed record BenchmarkOperationResult(
    string Id,
    double P50Microseconds,
    double P95Microseconds,
    double P99Microseconds,
    double AllocationBytes,

    /// <summary>Per-batch samples, kept so a comparison can pair batch to batch rather than pool them.</summary>
    double[][] Batches);

internal sealed record BenchmarkControlResult(
    double SpreadBytes,
    double MedianAbsoluteDeviationBytes,
    double NegativeFraction,
    bool Acceptable);
