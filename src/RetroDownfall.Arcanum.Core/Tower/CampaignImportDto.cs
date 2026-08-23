namespace RetroDownfall.Arcanum.Core.Tower;

public sealed record CampaignImportRequest(
    string Strategy,
    CampaignExportDto? Payload);

public sealed record CampaignImportResultDto(
    int CampaignsImported,
    int SpellsImported,
    int PromptsImported,
    IReadOnlyList<string> Warnings);
