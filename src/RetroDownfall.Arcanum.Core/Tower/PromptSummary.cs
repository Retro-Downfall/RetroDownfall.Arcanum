namespace RetroDownfall.Arcanum.Core.Tower;

/// <summary>A prompt list projection without its template, parameter documents or inference settings.</summary>
public sealed record PromptSummary(
    Guid Id,
    Guid? CampaignId,
    string Name,
    string Version,
    string? Description,
    string Tags,
    DateTimeOffset UpdatedAt);
