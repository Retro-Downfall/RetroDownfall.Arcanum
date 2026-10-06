using System.Text.Json;
using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Core.Intelligence.Models;

/// <summary>
/// One frame of the native inference stream (<c>application/x-ndjson</c>) and of its buffered projections.
/// </summary>
/// <remarks>
/// The wire names are the camelCase of these member names, with no renames (AGENTS.md rule 4), so the
/// members a Ward frame carries are named for what they are on the wire: <c>ToolName</c> (the tool a
/// <c>warded</c> or <c>wardResolved</c> frame is about), <c>Arguments</c> (the call's arguments, on
/// <c>warded</c>), <c>Allowed</c> and <c>Reason</c> (the resolution, on <c>wardResolved</c>) and
/// <c>Origin</c> (how that resolution was produced).
/// </remarks>
public sealed record IntelligenceEvent(
    IntelligenceEventType Type,
    string Message,
    string? Data = null,
    ChatCompletionUsage? Usage = null,
    IntelligenceToolCallEvent? ToolCall = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? WardId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ToolName = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    JsonElement? Arguments = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? Allowed = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Reason = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTimeOffset? Timestamp = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? FinishReason = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ReasoningContentSegment? Reasoning = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ContextTokenBreakdown? ContextBreakdown = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    AttachmentRefreshEvent? AttachmentRefresh = null,
    [property: JsonIgnore]
    bool ToolDenied = false,
    /// <summary>
    /// How a <c>warded</c> / <c>wardResolved</c> outcome was produced (issues #53 and #216).
    /// Additive: omitted when null, so clients that ignore it keep their existing behavior. Clients
    /// use it to describe retained historical outcomes. Every Ward frame is informational; clients
    /// report it and never open an approval prompt.
    /// </summary>
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    WardResolutionOrigin? Origin = null)
{
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// Structured payload for <see cref="IntelligenceEventType.ToolCall"/> and
/// <see cref="IntelligenceEventType.ToolResult"/> frames. Lets OpenAI-compatible bridges
/// emit <c>delta.tool_calls</c> chunks with the same id used to correlate the result, while
/// the legacy <c>Message</c> + <c>Data</c> fields stay populated for human-readable transcripts.
/// </summary>
public sealed record IntelligenceToolCallEvent(
    string CallId,
    string Name,
    string ArgumentsJson,
    int Index = 0,
    [property: JsonIgnore] bool PreserveProviderCallId = false);

/// <summary>Sanitized native event payload for <c>refresh_session_file</c>.</summary>
public sealed record AttachmentRefreshEvent(
    Guid AttachmentId,
    string LogicalKey,
    int Version,
    bool NewVersionCreated,
    bool QueuedForInjection,
    string SanitizedSourcePath,
    string ContentSha256,
    long ByteLength,
    DateTimeOffset SourceFreshnessTimestamp);
