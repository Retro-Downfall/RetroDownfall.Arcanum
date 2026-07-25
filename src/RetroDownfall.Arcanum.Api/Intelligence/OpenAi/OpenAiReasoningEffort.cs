using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Api.Intelligence.OpenAi;

/// <summary>
/// OpenAI-compatible wire values for <c>reasoning_effort</c>.
/// </summary>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<OpenAiReasoningEffort>))]
public enum OpenAiReasoningEffort
{
    [JsonStringEnumMemberName("none")]
    None = 0,

    [JsonStringEnumMemberName("minimal")]
    Minimal = 1,

    [JsonStringEnumMemberName("low")]
    Low = 2,

    [JsonStringEnumMemberName("medium")]
    Medium = 3,

    [JsonStringEnumMemberName("high")]
    High = 4,

    [JsonStringEnumMemberName("xhigh")]
    XHigh = 5,
}

