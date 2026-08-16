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
    CovenantStatusDto? Covenant = null);

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

public sealed record MemorySearchRequest(
    string Query,
    MemorySearchScope Scope = MemorySearchScope.All,
    Guid? SessionId = null,
    string? WorkspaceId = null);

public sealed record MemorySearchResultDto(
    MemorySearchScope Scope,
    string Title,
    string Content,
    string Provenance,
    string Retention,
    string SourceId);

public sealed record MemorySearchResponse(
    string Query,
    MemorySearchScope Scope,
    MemorySearchResultDto[] Results);

public sealed record MemoryEligibilityDto(
    string Name,
    bool Eligible,
    string Reason,
    string Retention);

public sealed record MemoryExplainDto(
    Guid? SessionId,
    string? SessionTitle,
    MemoryEligibilityDto[] Sources);

public sealed record LexiconListDto(LexiconEntryDto[] Entries);
