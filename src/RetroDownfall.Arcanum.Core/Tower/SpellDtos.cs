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
/// <c>FullContent</c> is the whole <c>SPELL.md</c>, frontmatter included; an import parses its frontmatter
/// back into the spell's fields rather than writing it into the new spell's body.
/// <c>Metadata</c> is genuinely optional and stays as it is. <c>OmittedScripts</c> and
/// <c>OmittedScriptCount</c> are what an export answers with, never what an import needs:
/// <c>OmittedScripts</c> names the files in the spell's <c>scripts</c> directory the bundle does not carry
/// (not a regular file, over the size limits, or past the script count cap; at most 64 names are listed),
/// and <c>OmittedScriptCount</c> counts all of them, so a caller can tell a complete bundle from a partial
/// one and a complete list of omissions from a truncated one. An import ignores both.
/// </remarks>
public sealed record SpellExportDto(
    SkillMetadata? Metadata,
    [property: JsonRequired] string FullContent,
    [property: JsonRequired] IReadOnlyList<SpellExportScriptDto> Scripts,
    IReadOnlyList<string>? OmittedScripts = null,
    int OmittedScriptCount = 0);

public sealed record SpellExportScriptDto(
    [property: JsonRequired] string FileName,
    [property: JsonRequired] string Base64Content);

public sealed record SpellImportRequest(
    [property: JsonRequired] SpellExportDto Payload,
    string? Workspace,
    Guid? CampaignId);
