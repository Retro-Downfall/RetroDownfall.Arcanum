using System.Text.Json;

namespace RetroDownfall.Arcanum.Core.TheForge;

public sealed record SpellValidationResultDto(
    bool IsValid,
    string[] Errors,
    string[] Warnings);

public sealed record SpellExportDto(
    SkillMetadata? Metadata,
    string FullContent,
    IReadOnlyList<SpellExportScriptDto> Scripts);

public sealed record SpellExportScriptDto(
    string FileName,
    string Base64Content);

public sealed record SpellImportRequest(
    SpellExportDto Payload,
    string? Workspace,
    Guid? CampaignId);
