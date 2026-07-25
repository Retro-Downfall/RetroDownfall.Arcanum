namespace RetroDownfall.Arcanum.Core.Intelligence.Models;

/// <summary>
/// One JSONL line in the persisted inference audit log (<c>Arcanum:Host:AuditLog</c>, §8.26). Written
/// by <c>InferenceAuditLogger</c> only for a <em>successfully completed</em> turn — not for
/// error/timeout/interrupted turns, which already have their own error-code telemetry
/// (<c>arcanum_inference_*</c> metrics, §16) and would otherwise double-count. Field names are
/// deliberately camelCase (via <c>AuditJsonContext</c>'s naming policy) to match the audit log's own
/// stable on-disk contract, independent of any HTTP wire convention.
/// </summary>
public sealed record InferenceAuditRecord(
    string Timestamp,
    string? SessionId,
    string RequestType,
    string? Model,
    string? Provider,
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens,
    long LatencyMs,
    int ToolCalls,
    List<string> ToolNames,
    List<string>? ToolArgumentsJson,
    string? FinishReason,
    string? ClientIp,
    string? SpellName,
    string? CampaignId,
    int ReasoningTokens = 0);
