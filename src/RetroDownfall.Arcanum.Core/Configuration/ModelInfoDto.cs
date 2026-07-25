using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record ModelInfoDto(
    string Model,
    string ProviderName,
    string ProviderType,
    string Endpoint,
    int ContextWindowLimit,
    bool SupportsVision = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ReasoningCapabilities? Reasoning = null);
