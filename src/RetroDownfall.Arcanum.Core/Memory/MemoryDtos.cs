using System.Text.Json.Serialization;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Lexicon;

namespace RetroDownfall.Arcanum.Core.Memory;

[JsonConverter(typeof(JsonStringEnumConverter<MemorySearchScope>))]

public enum MemorySearchScope
{

    Session,

    Attachments,

    Workspace,

    Saga,

    Lexicon,

    All,

}

public sealed record MemoryStoreStatusDto(
    string Name,
    bool Enabled,
    int Count,
    string Scope,
    string Retention);

/// <summary>
/// The aggregate memory picture, plus the content-free Covenant capability block.
/// </summary>
/// <remarks>
/// <paramref name="Covenant"/> is counts, ceilings, and closed health codes only — never a key, a
/// fragment, or a raw content hash — because this response is reachable wherever
/// <c>/api/memory/status</c> is and therefore without Covenant read authority. The exact per-entry
/// surfaces are the typed Covenant routes, which require authority of their own (§10.18).
///
/// <para>It is <see langword="null"/> when the Covenant tier is absent from this composition, which is
/// distinct from present-but-unavailable. A block that reported zeros for both would tell an operator
/// their Covenant is empty when in fact it could not be read.</para>
/// </remarks>
public sealed record MemoryStatusDto(
    Guid? SessionId,
    string? SessionTitle,
    MemoryStoreStatusDto[] Stores,
    CovenantStatusDto? Covenant = null,
    MemoryCampaignScopeDto? CampaignScope = null);

/// <summary>
/// Which Campaign scope a turn on this surface would draw memory from, stated before any turn runs.
/// </summary>
/// <remarks>
/// Resolved through the same seam retrieval uses, so this is the scope a turn would actually take
/// rather than a second description of it. <paramref name="Kind"/> is the closed name;
/// <paramref name="Detail"/> is the operator-facing sentence, which is what makes "narrowed, and by
/// what" legible without reading a configuration key.
/// </remarks>
public sealed record MemoryCampaignScopeDto(
    string Kind,
    Guid? CampaignId,
    string Detail);

public sealed record MemorySourceDto(
    string Name,
    string Scope,
    string Provenance,
    string Retention,
    bool Enabled,
    int Count);

public sealed record MemorySourcesDto(
    Guid? SessionId,
    MemorySourceDto[] Sources);

/// <param name="Limit">
/// Optional caller ceiling on the number of results. Omitted means the server's own budget, which is
/// also the maximum a caller may ask for; a value outside <c>[1, budget]</c> is refused rather than
/// silently clamped, so a caller never believes it paged when it did not.
/// </param>
public sealed record MemorySearchRequest(
    string Query,
    MemorySearchScope Scope = MemorySearchScope.All,
    Guid? SessionId = null,
    string? WorkspaceId = null,
    int? Limit = null);

public sealed record MemorySearchResultDto(
    MemorySearchScope Scope,
    string Title,
    string Content,
    string Provenance,
    string Retention,
    string SourceId,
    MemoryCampaignScopeDto? CampaignScope = null);

/// <summary>
/// What one scope contributed, and whether it had more to give. Scopes are consulted in order against
/// one shared budget, so a saturating early scope starves the later ones — without this a caller could
/// not tell an exhausted scope from a starved one.
/// </summary>
public sealed record MemorySearchScopeStatusDto(
    MemorySearchScope Scope,
    int Count,
    bool HasMore);

/// <param name="Scopes">One entry per scope actually consulted, in the order they were consulted.</param>
/// <param name="HasMore">True when any consulted scope was truncated; the caller may raise <c>limit</c>.</param>
public sealed record MemorySearchResponse(
    string Query,
    MemorySearchScope Scope,
    MemorySearchResultDto[] Results,
    MemorySearchScopeStatusDto[]? Scopes = null,
    bool HasMore = false);

public sealed record MemoryEligibilityDto(
    string Name,
    bool Eligible,
    string Reason,
    string Retention);

public sealed record MemoryExplainDto(
    Guid? SessionId,
    string? SessionTitle,
    MemoryEligibilityDto[] Sources,
    MemoryCampaignScopeDto? CampaignScope = null);

public sealed record LexiconListDto(LexiconEntryDto[] Entries);
