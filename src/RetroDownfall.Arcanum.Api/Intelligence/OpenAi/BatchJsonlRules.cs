namespace RetroDownfall.Arcanum.Api.Intelligence.OpenAi;

/// <summary>
/// The rules a <c>/v1/batches</c> input JSONL record must meet that the host's processor and the CLI's
/// local preflight apply from one definition, so the preflight cannot refuse a line the host would
/// accept or accept one the host would refuse on these three points.
/// </summary>
/// <remarks>
/// <para>What is shared is exactly this: the request wrapper's method (<see cref="IsRequiredMethod"/>),
/// the endpoint (<see cref="SupportedEndpoint"/>, which the host checks against both the create-batch
/// <c>endpoint</c> field and each line's <c>url</c>), and the per-record size limit
/// (<see cref="MaxRecordBytes"/>).</para>
/// <para>What is not shared: the preflight's own <c>custom_id</c> presence and uniqueness check and its
/// "the body is a JSON object" check are a client restatement of what the host reads and tracks in its
/// own form, so they are a courtesy that can be stricter than the host, never a second source of truth.
/// The host stays authoritative: it enforces its rules per record and checkpoints a bad line as an
/// error, and the preflight only refuses an obviously unusable file before uploading it.</para>
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

    /// <summary>
    /// Whether a request wrapper's <c>method</c> is the one the host accepts. HTTP method names are
    /// case-insensitive on the wire, so <c>post</c> is accepted exactly as <see cref="RequiredMethod"/> is.
    /// </summary>
    /// <param name="method">The wrapper's <c>method</c> value.</param>
    public static bool IsRequiredMethod(string? method) =>
        string.Equals(method, RequiredMethod, StringComparison.OrdinalIgnoreCase);
}
