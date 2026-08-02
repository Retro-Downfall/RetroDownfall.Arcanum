using System.Text.Json.Serialization;

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

public sealed record MemoryStatusDto(
    Guid? SessionId,
    string? SessionTitle,
    MemoryStoreStatusDto[] Stores);

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
