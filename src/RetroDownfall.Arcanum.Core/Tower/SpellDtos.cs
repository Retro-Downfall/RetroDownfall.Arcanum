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
/// <c>Metadata</c> is genuinely optional and stays as it is. <c>OmittedScripts</c> is what an export
/// answers with, never what an import needs: it names the files in the spell's <c>scripts</c> directory the
/// bundle does not carry (not a regular file, over the size limits, or past the script count cap; at most 64
/// names are listed), so a caller can tell a complete bundle from a partial one. An import ignores it.
/// </remarks>
public sealed record SpellExportDto(
    SkillMetadata? Metadata,
    [property: JsonRequired] string FullContent,
    [property: JsonRequired] IReadOnlyList<SpellExportScriptDto> Scripts,
    IReadOnlyList<string>? OmittedScripts = null);

public sealed record SpellExportScriptDto(
    [property: JsonRequired] string FileName,
    [property: JsonRequired] string Base64Content);

public sealed record SpellImportRequest(
    [property: JsonRequired] SpellExportDto Payload,
    string? Workspace,
    Guid? CampaignId);
