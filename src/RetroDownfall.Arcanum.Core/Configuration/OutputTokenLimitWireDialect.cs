using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>The provider-declared top-level output-token bound. Never inferred from an endpoint or model.</summary>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<OutputTokenLimitWireDialect>))]
public enum OutputTokenLimitWireDialect
{
    [JsonStringEnumMemberName("maxCompletionTokens")]
    MaxCompletionTokens,

    [JsonStringEnumMemberName("maxTokens")]
    MaxTokens,
}
