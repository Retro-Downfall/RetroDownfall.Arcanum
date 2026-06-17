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
}
