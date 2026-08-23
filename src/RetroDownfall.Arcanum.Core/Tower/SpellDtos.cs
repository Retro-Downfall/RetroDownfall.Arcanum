using System.Text.Json;
using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Core.Tower;

public sealed record SpellValidationResultDto(
    bool IsValid,
    string[] Errors,
    string[] Warnings);

/// <remarks>
/// <c>FullContent</c> and <c>Scripts</c> carry <c>[JsonRequired]</c> because they are declared
/// non-nullable. Without it a body that omits them binds null into both and the import succeeds,
/// writing a spell with no content at all; STJ refusing the body is what makes the declared shape true.
/// <c>Metadata</c> is genuinely optional and stays as it is.
/// </remarks>
public sealed record SpellExportDto(
    SkillMetadata? Metadata,
    [property: JsonRequired] string FullContent,
    [property: JsonRequired] IReadOnlyList<SpellExportScriptDto> Scripts);

public sealed record SpellExportScriptDto(
    [property: JsonRequired] string FileName,
    [property: JsonRequired] string Base64Content);

public sealed record SpellImportRequest(
    [property: JsonRequired] SpellExportDto Payload,
    string? Workspace,
    Guid? CampaignId);
