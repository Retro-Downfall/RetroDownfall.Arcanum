using System.Text.Json.Serialization;

using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Weave;

namespace RetroDownfall.Arcanum.Core.Memory;

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<MemoryRecallScope>))]
public enum MemoryRecallScope
{
    All,
    Saga,
    Lexicon,
    Covenant,
}

public sealed record MemoryRecallRequest(
    string Query,
    MemoryRecallScope Scope = MemoryRecallScope.All,
    int Limit = 10,
    string? Cursor = null);

public sealed record MemoryRecallFragment(
    MemoryRecallScope Source,
    string Id,
    string Content,
    float? Similarity = null,
    Guid? CampaignId = null,
    Guid? VersionId = null,
    string Provenance = "");

public sealed record MemoryRecallCandidates(
    IReadOnlyList<MemoryRecallFragment> Items,
    bool CandidateLimitReached = false);

public sealed record MemoryRecallPage(
    IReadOnlyList<MemoryRecallFragment> Items,
    string? NextCursor,
    bool CandidateLimitReached = false);

public interface IMemoryRecallCandidateReader
{
    Task<Result<MemoryRecallCandidates>> ReadAsync(
        MemoryRecallRequest request,
        CanonicalCampaignContext canonical,
        MemoryScope memoryScope,
        CancellationToken cancellationToken);
}
