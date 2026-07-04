namespace RetroDownfall.Arcanum.Api.Models;

/// <summary>
/// Wire shape for a persisted Sanctum breach, mirroring <c>SanctumBreachRecord</c> minus
/// <c>CampaignId</c> (already present in the route). Path-shaped detail fields are redacted to
/// their filename component via <c>SanctumPathRedactor</c> before serialization.
/// </summary>
public sealed record SanctumBreachDto(
    string Id,
    DateTimeOffset OccurredAt,
    string ToolName,
    string BreachType,
    string Description,
    string? RequestedPath,
    string? ResolvedPath,
    string? WorkspaceRoot,
    string? RequestedUrl,
    string? ToolArguments,
    string? LimitValue,
    string? ActualValue);
