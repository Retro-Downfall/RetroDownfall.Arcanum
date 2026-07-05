using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Weave;

/// <summary>
/// RAG Phase 1 — Divination: semantic search (cosine similarity) over The Weave. Every RAG feature has
/// its own vec0 acceleration table + BLOB source-of-truth table (see DESIGN.md §21); this service is
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
    /// cosine path (see the Infrastructure implementation) — bounded by the scope's row count, not the
    /// whole table, so it is not the "full managed scan" <see cref="SearchAsync"/> falls back to when
    /// vec0 is unavailable. Returns an empty result (not a failure) when the scope matches no rows.
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

}
