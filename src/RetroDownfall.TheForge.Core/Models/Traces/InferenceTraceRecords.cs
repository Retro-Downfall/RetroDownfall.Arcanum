namespace RetroDownfall.TheForge.Core.Models.Traces;

/// <summary>Root document for <c>~/.config/arcanum/the-forge-inference-traces.json</c>.</summary>
public sealed record InferenceTraceStoreDocument(
    int SchemaVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<InferenceTraceRecord> Traces);

public sealed record InferenceTraceRecord(
    Guid Id,
    DateTimeOffset CapturedAt,
    string SourceKind,
    string? SourceId,
    string? SessionId,
    IReadOnlyList<InferenceTraceEventRecord> Events);

public sealed record InferenceTraceEventRecord(
    string Type,
    string Message,
    string? Data,
    int? PromptTokens,
    int? CompletionTokens,
    int? TotalTokens,
    int? CachedTokens,
    string? FinishReason,
    string? ToolCallId,
    string? ToolName,
    string? ToolRoundId,
    DateTimeOffset? Timestamp);
