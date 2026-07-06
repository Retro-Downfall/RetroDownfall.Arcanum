using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Core.Intelligence.Models;

/// <summary>
/// Discriminator for <see cref="IntelligenceEvent"/> NDJSON frames. Serialized as camelCase
/// strings on the wire (for example <c>"status"</c>, <c>"sessionBound"</c>) via the
/// AOT-safe <see cref="JsonStringEnumConverter{TEnum}"/> with explicit member names.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<IntelligenceEventType>))]
public enum IntelligenceEventType
{
    [JsonStringEnumMemberName("status")]
    Status,

    [JsonStringEnumMemberName("sessionBound")]
    SessionBound,

    [JsonStringEnumMemberName("conversationBound")]
    ConversationBound,

    [JsonStringEnumMemberName("token")]
    Token,

    [JsonStringEnumMemberName("result")]
    Result,

    [JsonStringEnumMemberName("error")]
    Error,

    [JsonStringEnumMemberName("toolCall")]
    ToolCall,

    [JsonStringEnumMemberName("toolResult")]
    ToolResult,

    [JsonStringEnumMemberName("warded")]
    Warded,

    [JsonStringEnumMemberName("wardResolved")]
    WardResolved,

    /// <summary>
    /// Emitted alongside (immediately before) <see cref="ToolResult"/> on the streaming path when an
    /// unexpected tool exception was tolerated (<c>Arcanum:Intelligence:TolerateToolFailures</c>) —
    /// lets clients observe and surface the failure distinctly from a normal tool result, even though
    /// the turn continues. Native-NDJSON only; not surfaced on the OpenAI <c>/v1</c> streaming bridge
    /// (falls through its default/no-op case, matching how <c>toolResult</c> is also hidden there).
    /// </summary>
    [JsonStringEnumMemberName("toolError")]
    ToolError,
}
