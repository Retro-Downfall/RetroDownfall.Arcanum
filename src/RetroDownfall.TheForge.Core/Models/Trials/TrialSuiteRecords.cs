using RetroDownfall.Arcanum.Core.ProvingGrounds;

namespace RetroDownfall.TheForge.Core.Models.Trials;

/// <summary>Root document for <c>~/.config/arcanum/the-forge-trial-suites.json</c>.</summary>
public sealed record TrialSuiteStoreDocument(
    int SchemaVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<TrialSuiteRecord> Suites);

public sealed record TrialSuiteRecord(
    Guid Id,
    string Name,
    string? Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<TrialSuiteItemRecord> Trials,
    IReadOnlyList<TrialSuiteRunRecord> Runs);

public sealed record TrialSuiteItemRecord(
    Guid Id,
    string Name,
    Trial Trial,
    IReadOnlyList<string> Tags,
    string? ExpectedNotes);

public sealed record TrialSuiteRunRecord(
    Guid Id,
    Guid SuiteId,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string? Model,
    string? Provider,
    string? ParameterSummary,
    IReadOnlyList<TrialSuiteRunResultRecord> Results);

public sealed record TrialSuiteRunResultRecord(
    Guid SuiteItemId,
    bool Passed,
    string Output,
    IReadOnlyList<InquisitorVerdict> Verdicts,
    int? PromptTokens,
    int? CompletionTokens,
    int? TotalTokens,
    long? LatencyMs,
    string? Error);
