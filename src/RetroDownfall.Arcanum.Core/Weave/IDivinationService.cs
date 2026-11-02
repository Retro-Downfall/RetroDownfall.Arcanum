using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Weave;

/// <summary>
/// Divination: semantic cosine search over each feature's durable embedding BLOB table.
/// Table and column identifiers are internal constants owned by the calling feature.
/// Every search scores the eligible rows through the managed SIMD scan.
/// </summary>
public interface IDivinationService
{

    /// <summary>
    /// Runs a KNN search for <paramref name="queryEmbedding"/> against <paramref name="tableName"/>,
    /// returning up to <paramref name="maxResults"/> hits at or above
    /// <paramref name="similarityThreshold"/>, ordered by descending similarity. Caller cancellation propagates. On
    /// failure (DB error, etc.) returns a failed
    /// <see cref="Result{T}"/> with a sanitized error; callers should treat that the same as an empty
    /// result set (graceful degradation).
    /// </summary>
    Task<Result<DivinationResult[]>> SearchAsync(
        string tableName,
        string primaryKeyColumn,
        string embeddingColumn,
        Embedding<float> queryEmbedding,
        int maxResults,
        float similarityThreshold,
        CancellationToken cancellationToken);

    /// <summary>
    /// Like <see cref="SearchAsync"/>, but restricted to rows whose <paramref name="primaryKeyColumn"/>
    /// value appears in <c>SELECT <paramref name="scopeJoinColumn"/> FROM <paramref name="scopeTableName"/>
    /// WHERE <paramref name="scopeFilterColumn"/> = <paramref name="scopeFilterValue"/></c> — for example
    /// restricting a codebase-chunk search to one workspace's chunks. Candidates are joined to the
    /// scope table in SQL before scoring, without an unbounded list of matching ids or a total row
    /// budget. Returns an empty result when the scope matches no rows.
    /// </summary>
    Task<Result<DivinationResult[]>> SearchScopedAsync(
        string tableName,
        string primaryKeyColumn,
        string embeddingColumn,
        string scopeTableName,
        string scopeJoinColumn,
        string scopeFilterColumn,
        string scopeFilterValue,
        Embedding<float> queryEmbedding,
        int maxResults,
        float similarityThreshold,
        CancellationToken cancellationToken);

    /// <summary>
    /// Like <see cref="SearchScopedAsync"/>, but with a two-tier ownership predicate instead of a single
    /// equality: a row is a candidate when its owner is marked installation-scoped, or when its owner
    /// names the Campaign in <paramref name="scope"/>. Every other ownership state — including one that
    /// has not been classified, and one whose classification is unresolved — is excluded from every
    /// scope.
    /// </summary>
    Task<Result<DivinationResult[]>> SearchCampaignScopedAsync(
        string tableName,
        string primaryKeyColumn,
        string embeddingColumn,
        DivinationCampaignScope scope,
        Embedding<float> queryEmbedding,
        int maxResults,
        float similarityThreshold,
        CancellationToken cancellationToken);

}

/// <summary>
/// Where a feature records who owns each embedded row, and which Campaign the search is being run for.
/// </summary>
/// <remarks>
/// The table and column names are internal constants owned by the calling feature's retrieval code —
/// <see cref="SagaStorageKeys.CampaignScope"/> is the only construction that ships — and reach the
/// query as interpolated identifiers. <paramref name="CampaignId"/> is the one value bound as a
/// parameter, and a null one means the caller resolved to no Campaign: the candidate set is then the
/// installation-scoped rows alone.
/// </remarks>
public sealed record DivinationCampaignScope(
    string OwnerTableName,
    string OwnerJoinColumn,
    string OwnerScopeKindColumn,
    string OwnerCampaignColumn,
    int GlobalScopeKindCode,
    int CampaignScopeKindCode,
    Guid? CampaignId);
