using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Core.Storage;

/// <summary>
/// Immutable payload frozen by the owning invocation before filesystem commit classification.
/// The receipt and timestamp are reused by every internal retry.
/// </summary>
public sealed record MandatoryToolInteraction(
    Guid SessionId,
    ToolInteractionReceipt Receipt,
    string? ToolCallId,
    string ToolName,
    string Arguments,
    string Result,
    string ModelUsed,
    DateTimeOffset CreatedAt);

public sealed record MandatoryToolInteractionProbe(
    Guid SessionId,
    ToolInteractionReceipt Receipt,
    string? ToolCallId,
    string ToolName,
    string Arguments,
    string ModelUsed,
    DateTimeOffset CreatedAt);

public enum MandatoryToolInteractionProbeOutcome
{
    NotFound,
    Replayed,
    Mismatched,
    Unavailable,
}

public readonly record struct MandatoryToolInteractionProbeResult(
    MandatoryToolInteractionProbeOutcome Outcome,
    string? Result);

public enum MandatoryToolInteractionPreflightOutcome
{
    Admitted,
    Replayed,
    Rejected,
    Mismatched,
    Unavailable,
}

public readonly record struct MandatoryToolInteractionPreflightResult(
    MandatoryToolInteractionPreflightOutcome Outcome,
    string? Result);

public enum MandatoryToolInteractionAppendOutcome
{
    NewlyCommitted,
    RecoveredCommitted,
    Failed,
    Ambiguous,
}

public readonly record struct MandatoryToolInteractionAppendResult(
    MandatoryToolInteractionAppendOutcome Outcome,
    ToolInteractionReceipt Receipt);
