using System.Text.Json;

namespace RetroDownfall.TheForge.Core.Chronicle;

/// <summary>
/// A tolerant, Forge-local parse of one <c>GET /api/apprentices/{id}/chronicle</c> SSE frame.
///
/// Deliberately NOT a deserialization of <c>RetroDownfall.Arcanum.Core.TheForge.ApprenticeEvent</c>:
/// <c>ChronicleSseWriter.WritePassThroughEvent</c> flattens pass-through Wizard
/// (<c>IntelligenceEvent</c>) fields directly onto the frame (<c>message</c>, <c>data</c>,
/// <c>usage</c>, <c>toolCall</c>, <c>wardId</c>, <c>toolName</c>, <c>arguments</c>, <c>allowed</c>,
/// <c>reason</c>) with no nested <c>wizardEvent</c> object, and three lifecycle types
/// (<c>CastSent</c>, <c>SimulacrumStarted</c>, <c>SimulacrumCompleted</c>) are emitted PascalCase
/// while every other type is camelCase. <see cref="Type"/> is therefore kept as the raw wire string;
/// callers compare case-insensitively.
/// </summary>
public sealed record ChronicleFrame(
    string Type,
    Guid ApprenticeId,
    DateTimeOffset Timestamp,
    string? Name = null,
    string? Goal = null,
    string? Description = null,
    string? Result = null,
    long? DurationMs = null,
    string? Error = null,
    string? Summary = null,
    long? TotalDurationMs = null,
    int? AtStep = null,
    int? FromStep = null,
    int? Attempt = null,
    long? BackoffMs = null,
    int? StepIndex = null,
    // Pass-through Wizard (IntelligenceEvent) fields — present only on toolCall/toolResult/warded/wardResolved frames.
    string? Message = null,
    string? Data = null,
    JsonElement? Usage = null,
    JsonElement? ToolCall = null,
    string? WardId = null,
    string? ToolName = null,
    JsonElement? Arguments = null,
    bool? Allowed = null,
    string? Reason = null)
{

    /// <summary>Case-insensitive comparison against a known Chronicle event type name (e.g. <c>"toolCall"</c>, <c>"CastSent"</c>).</summary>
    public bool IsType(string typeName) => string.Equals(Type, typeName, StringComparison.OrdinalIgnoreCase);

}
