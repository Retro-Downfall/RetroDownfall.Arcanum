namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// OpenAI-compatible <c>/v1/batches</c> asynchronous bulk chat-completion processing. Bound from
/// <c>Arcanum:Batches</c>. See DESIGN.md §11.21.
/// </summary>
public sealed record BatchesSettings
{

    /// <summary>Maximum number of batches processed concurrently across the whole server. Default 3; clamped 1–20.</summary>
    public int MaxConcurrentBatches { get; init; } = 3;

    /// <summary>Maximum JSONL request lines accepted in a single batch input file. Default 50,000; clamped 1–1,000,000.</summary>
    public int MaxRequestsPerBatch { get; init; } = 50_000;

    /// <summary>How long after creation a non-terminal batch is force-expired (input/output files deleted). Default 24; clamped 1–168.</summary>
    public int BatchExpiryHours { get; init; } = 24;

    /// <summary>Maximum chat-completion requests run concurrently within a single batch. Default 1 (sequential); clamped 1–10.</summary>
    public int MaxConcurrentRequestsPerBatch { get; init; } = 1;

}
