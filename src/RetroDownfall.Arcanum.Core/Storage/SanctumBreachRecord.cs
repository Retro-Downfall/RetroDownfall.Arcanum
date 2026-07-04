namespace RetroDownfall.Arcanum.Core.Storage;

/// <summary>
/// Grimoire-persisted Sanctum breach row. Distinct from the wire DTO
/// <see cref="Sanctum.SanctumBreach"/>, which remains unchanged for backward compatibility.
/// </summary>
public sealed record SanctumBreachRecord(
    string Id,
    string CampaignId,
    DateTimeOffset OccurredAt,
    string ToolName,
    string BreachType,
    string Description,
    SanctumBreachDetails? Details);
