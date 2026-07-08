namespace RetroDownfall.TheForge.Core.Models;

/// <summary>
/// Re-declared mirror of <c>RetroDownfall.Arcanum.Api.Models.GrimoireStatsDto</c> (wire shape of
/// <c>GET /api/grimoire/stats</c>). Kept in TheForge.Core to avoid referencing the Api project.
/// </summary>
public sealed record GrimoireStatsDto(
    long DatabaseBytes,
    long WalBytes,
    int SessionCount,
    int EntryCount,
    int CampaignCount);
