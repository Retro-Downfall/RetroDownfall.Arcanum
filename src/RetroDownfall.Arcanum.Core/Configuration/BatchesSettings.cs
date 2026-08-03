namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Runtime projection for OpenAI-compatible <c>/v1/batches</c> asynchronous bulk chat-completion
/// processing. Host concurrency comes from <c>Arcanum:Execution</c>; request and expiry mechanics
/// are code-owned.
/// </summary>
public sealed record BatchesSettings
{

    /// <summary>Maximum number of batches processed concurrently across the whole server. Default 3; clamped 1–20.</summary>
    public int MaxConcurrentBatches { get; set; } = 3;

    /// <summary>Maximum chat-completion requests run concurrently within a single batch. Default 1 (sequential); clamped 1–10.</summary>
    public int MaxConcurrentRequestsPerBatch { get; set; } = 1;

}
