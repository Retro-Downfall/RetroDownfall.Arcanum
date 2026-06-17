namespace RetroDownfall.Arcanum.Core.TheForge;

public sealed record CampaignExportDto(
    CampaignDto Campaign,
    IReadOnlyList<CampaignExportSpellDto> Spells,
    IReadOnlyList<PromptExportDto> Prompts);

public sealed record CampaignExportSpellDto(
    string Name,
    string? SkillJson,
    string FullContent,
    IReadOnlyList<CampaignExportScriptDto> Scripts);

public sealed record CampaignExportScriptDto(
    string FileName,
    string Base64Content);
