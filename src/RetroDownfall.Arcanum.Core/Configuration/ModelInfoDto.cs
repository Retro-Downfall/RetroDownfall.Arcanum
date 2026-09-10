using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record ModelInfoDto(
    string Model,
    string ProviderName,
    string ProviderType,
    string Endpoint,
    int ContextWindowLimit,
    bool SupportsVision = false,
    bool SupportsTools = true,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ReasoningWireDialect? WireDialect = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? MaxBudgetTokens = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    PromptCachingProfile? PromptCaching = null);
