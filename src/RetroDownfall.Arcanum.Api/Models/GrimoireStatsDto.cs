namespace RetroDownfall.Arcanum.Api.Models;

public sealed record GrimoireStatsDto(
    long DatabaseBytes,
    long WalBytes,
    int SessionCount,
    int EntryCount,
    int CampaignCount);
