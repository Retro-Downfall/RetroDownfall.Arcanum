using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Core.Intelligence.Spells;

public sealed record SpellExecuteRequest(
    string Prompt,
    string? Model = null,
    float? Temperature = null,
    float? TopP = null,
    int? MaxOutputTokens = null,
    IReadOnlyList<string>? Stop = null,
    long? Seed = null,
    string? ResponseFormat = null,
    float? PresencePenalty = null,
    float? FrequencyPenalty = null,
    string? Workspace = null,
    Guid? CampaignId = null,
    Guid? SessionId = null,
    ToolPolicy? ToolPolicy = null);
