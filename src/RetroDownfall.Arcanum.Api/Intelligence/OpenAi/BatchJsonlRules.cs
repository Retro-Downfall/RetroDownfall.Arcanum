namespace RetroDownfall.Arcanum.Api.Intelligence.OpenAi;

/// <summary>
/// The rules a <c>/v1/batches</c> input JSONL record must meet, in one place so the host's processor and
/// the CLI's local preflight cannot drift apart.
/// </summary>
/// <remarks>
/// The host stays authoritative: it enforces these per record and checkpoints a bad line as an error.
/// The CLI preflight reads the same values to refuse an obviously unusable file before uploading it.
/// </remarks>
public static class BatchJsonlRules
{
    /// <summary>
    /// Only chat-completion batches are supported; this mirrors the JSONL body contract
    /// <c>BatchProcessingService</c> understands.
    /// </summary>
    public const string SupportedEndpoint = "/v1/chat/completions";

    /// <summary>Every request wrapper names this HTTP method.</summary>
    public const string RequiredMethod = "POST";

    /// <summary>
    /// The largest single record (one physical line) the host will materialize. A larger record is
    /// checkpointed as an error without being allocated or sent to a provider.
    /// </summary>
    public const long MaxRecordBytes = 64L * 1024L * 1024L;
}
