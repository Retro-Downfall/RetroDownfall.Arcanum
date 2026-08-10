using System.Text.Json;
using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Core.Intelligence.Models;

public sealed record IntelligenceEvent(
    IntelligenceEventType Type,
    string Message,
    string? Data = null,
    ChatCompletionUsage? Usage = null,
    IntelligenceToolCallEvent? ToolCall = null,
    [property: JsonPropertyName("wardId")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? WardId = null,
    [property: JsonPropertyName("toolName")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? WardToolName = null,
    [property: JsonPropertyName("arguments")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    JsonElement? WardArguments = null,
    [property: JsonPropertyName("allowed")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? WardAllowed = null,
    [property: JsonPropertyName("reason")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? WardReason = null,
    [property: JsonPropertyName("timestamp")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTimeOffset? Timestamp = null,
    [property: JsonPropertyName("finishReason")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? FinishReason = null,
    [property: JsonPropertyName("reasoning")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ReasoningContentSegment? Reasoning = null,
    [property: JsonPropertyName("contextBreakdown")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ContextTokenBreakdown? ContextBreakdown = null,
    [property: JsonPropertyName("attachmentRefresh")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    AttachmentRefreshEvent? AttachmentRefresh = null,
    [property: JsonIgnore]
    bool ToolDenied = false,
    /// <summary>
    /// Who supplied a <c>warded</c> / <c>wardResolved</c> outcome (issue #53). Additive: omitted when
    /// null, so clients that ignore it keep their existing behavior. Clients use it to tell an
    /// operator-resolved ward from one the host resolved on its own — an auto-approved ward must be
    /// reported, not prompted for.
    /// </summary>
    [property: JsonPropertyName("origin")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    WardResolutionOrigin? WardOrigin = null)
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
