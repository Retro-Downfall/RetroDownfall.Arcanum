namespace RetroDownfall.TheForge.Core.Models.Comparisons;

/// <summary>Root document for <c>~/.config/arcanum/the-forge-comparisons.json</c>.</summary>
public sealed record ComparisonStoreDocument(
    int SchemaVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ComparisonRunRecord> Runs);

public sealed record ComparisonRunRecord(
    Guid Id,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string SourceKind,
    string? SourceId,
    string InputPreview,
    IReadOnlyList<ComparisonVariantResultRecord> Variants);

public sealed record ComparisonVariantResultRecord(
    Guid VariantId,
    string Label,
    string? Model,
    string? Provider,
    string Output,
    int? PromptTokens,
    int? CompletionTokens,
    int? TotalTokens,
    int? CachedTokens,
    long? LatencyMs,
    string? FinishReason,
    string? CostLabel,
    decimal? CostUsd,
    IReadOnlyList<string> ToolCallNames,
    string? Error);
