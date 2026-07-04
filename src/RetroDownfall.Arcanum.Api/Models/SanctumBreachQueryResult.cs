namespace RetroDownfall.Arcanum.Api.Models;

/// <summary>
/// Paginated response for <c>GET /campaigns/{campaignId}/sanctum/breaches</c>.
/// </summary>
public sealed record SanctumBreachQueryResult(
    SanctumBreachDto[] Items,
    bool HasMore);
