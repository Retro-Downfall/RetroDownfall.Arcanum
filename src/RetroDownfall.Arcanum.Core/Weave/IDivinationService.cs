using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Weave;

/// <summary>
/// Divination: semantic search (cosine similarity) over The Weave. Every RAG feature has
/// its own vec0 acceleration table + BLOB source-of-truth table (see
/// <c>docs/Arcanum.DESIGN.md</c> §21); this service is
/// the single, generic KNN entry point every feature's retrieval code calls into.
///
/// Table resolution (see the Infrastructure implementation): <paramref name="tableName"/> on
/// <see cref="SearchAsync"/> names the <b>vec0 virtual table</b> (for example
/// <c>"entry_embeddings_vec"</c>). When sqlite-vec is unavailable, the implementation derives the
/// companion BLOB table by stripping the <c>_vec</c> suffix (for example <c>"entry_embeddings"</c>)
/// and performs the same search with a managed C# cosine computation instead — callers do not need to
/// know or care which path ran.
/// </summary>
public interface IDivinationService
{

    /// <summary>
    /// Runs a KNN search for <paramref name="queryEmbedding"/> against <paramref name="tableName"/>,
    /// returning up to <paramref name="maxResults"/> hits at or above
    /// <paramref name="similarityThreshold"/>, ordered by descending similarity. Never throws — on
    /// failure (vec0 unavailable and managed fallback also fails, DB error, etc.) returns a failed
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
    /// restricting a codebase-chunk search to one workspace's chunks. The vec0 KNN path has no
    /// per-row partition key in its current schema, so this always ranks via the managed brute-force
    /// cosine path (see the Infrastructure implementation) — SQL-joined to the scope table (no
    /// unbounded <c>IN (...)</c> of every matching id) and capped by an internal managed-search row
    /// budget. Returns an empty result (not a failure) when the scope matches no rows.
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
    /// <remarks>
    /// This always ranks through the managed cosine path, exactly as <see cref="SearchScopedAsync"/>
    /// does, and deliberately so rather than incidentally. The vec0 KNN path carries no per-row partition
    /// key, so a scoped search that used it would have to rank first and filter afterwards; the ownership
    /// predicate would then apply on one path and not the other, and what a turn recalled would change
    /// with whether an optional native asset happened to ship. One path means one answer.
    /// </remarks>
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
