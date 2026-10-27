using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Tower;

/// <summary>Ranks eligible Session entries in one read snapshot, with exact bounded lookahead.</summary>
public interface ISessionDivinationService
{
    Task<Result<SemanticSearchResult>> SearchAsync(
        Embedding<float> queryEmbedding,
        Guid? campaignId,
        string? status,
        int limit,
        float similarityThreshold,
        CancellationToken cancellationToken);
}
