using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Weave;

// RAG Phase 1 — Divination: semantic search over The Weave. Scoped service reusing the scoped
// ArcanumDbContext's connection via raw DbCommand, the same pattern GrimoireRepository.SearchArchivesAsync
// and UnseenServantWatermarkStore already use for tables outside the compiled EF model (entry_embeddings /
// entry_embeddings_vec are raw-SQL-only, like UnseenServantWatermarks and SanctumBreaches).
//
// Table resolution (see IDivinationService remarks): callers pass the vec0 virtual table name (e.g.
// "entry_embeddings_vec"). When WeaveIndexAvailability.IsVecAvailable is true, this queries that table
// directly with vec0 KNN. When false (Phase 1's default — see WeaveIndexAvailability), the vec0 table
// name is never even touched: the companion BLOB table name is derived by stripping the "_vec" suffix
// (-> "entry_embeddings") and the search runs as a managed, brute-force cosine scan in C# via
// EmbeddingBlobCodec. Column names are shared between both tables by convention (the BLOB table and
// its Data/Schema/Accelerators/ companion declare identical column names) so the same
// primaryKeyColumn/embeddingColumn pair works either way.
//
// Managed-path mechanics: every matching row is scored, results are tracked in a streaming top-K
// heap (no full scored list + OrderBy), and scoped searches join the scope table in SQL instead of an
// unbounded IN (...) of every chunk id.
internal sealed class DivinationService(
    ArcanumDbContext db,
    WeaveIndexAvailability availability,
    ILogger<DivinationService> logger) : IDivinationService
{
    private const string VecTableSuffix = "_vec";

    private static readonly IReadOnlyDictionary<string, string> EmptyMetadata =
        new Dictionary<string, string>(0);

    public async Task<Result<DivinationResult[]>> SearchAsync(
        string tableName,
        string primaryKeyColumn,
        string embeddingColumn,
        Embedding<float> queryEmbedding,
        int maxResults,
        float similarityThreshold,
        CancellationToken cancellationToken)
    {
        try
        {
            DbConnection connection = db.Database.GetDbConnection();

            if (connection.State != ConnectionState.Open)
            {
                await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            }

            float[] queryVector = queryEmbedding.Vector.ToArray();

            return availability.IsVecAvailable
                ? Result<DivinationResult[]>.Success(
                    await SearchVecAsync(
                        connection,
                        tableName,
                        primaryKeyColumn,
                        embeddingColumn,
                        queryVector,
                        maxResults,
                        similarityThreshold,
                        cancellationToken).ConfigureAwait(false))
                : await SearchManagedAsync(
                    connection,
                    DeriveBlobTableName(tableName),
                    primaryKeyColumn,
                    embeddingColumn,
                    queryVector,
                    maxResults,
                    similarityThreshold,
                    cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Divination search against {TableName} failed; treating as no results.", tableName);

            return Result<DivinationResult[]>.Failure(new Error(
                ErrorCodes.Embeddings.ProviderUnavailable,
                "Semantic search is temporarily unavailable. See server logs for detail."));
        }
    }

    public async Task<Result<DivinationResult[]>> SearchScopedAsync(
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
        CancellationToken cancellationToken)
    {
        try
        {
            DbConnection connection = db.Database.GetDbConnection();

            if (connection.State != ConnectionState.Open)
            {
                await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            }

            float[] queryVector = queryEmbedding.Vector.ToArray();

            // The vec0 KNN path (used when IsVecAvailable) has no per-row partition key in its current
            // schema, so a scoped search always ranks via the managed brute-force path below instead —
            // but SQL-joined to the scope table so only in-scope rows are read (no unbounded IN of
            // every matching chunk id).
            return await SearchManagedScopedAsync(
                connection,
                DeriveBlobTableName(tableName),
                primaryKeyColumn,
                embeddingColumn,
                scopeTableName,
                scopeJoinColumn,
                scopeFilterColumn,
                scopeFilterValue,
                queryVector,
                maxResults,
                similarityThreshold,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Scoped divination search against {TableName} failed; treating as no results.", tableName);

            return Result<DivinationResult[]>.Failure(new Error(
                ErrorCodes.Embeddings.ProviderUnavailable,
                "Semantic search is temporarily unavailable. See server logs for detail."));
        }
    }

    public async Task<Result<DivinationResult[]>> SearchCampaignScopedAsync(
        string tableName,
        string primaryKeyColumn,
        string embeddingColumn,
        DivinationCampaignScope scope,
        Embedding<float> queryEmbedding,
        int maxResults,
        float similarityThreshold,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);

        try
        {
            DbConnection connection = db.Database.GetDbConnection();

            if (connection.State != ConnectionState.Open)
            {
                await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            }

            // Unconditionally the managed path, and not because vec0 happens to be absent today. The
            // vec0 table has no per-row ownership column, so an accelerated scoped search could only
            // rank first and filter afterwards - a different candidate set, reached by whether an
            // optional native asset shipped. Routing both cases here is what makes the answer one answer.
            return await SearchManagedCampaignScopedAsync(
                connection,
                DeriveBlobTableName(tableName),
                primaryKeyColumn,
                embeddingColumn,
                scope,
                queryEmbedding.Vector.ToArray(),
                maxResults,
                similarityThreshold,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Campaign-scoped divination search against {TableName} failed; treating as no results.",
                tableName);

            return Result<DivinationResult[]>.Failure(new Error(
                ErrorCodes.Embeddings.ProviderUnavailable,
                "Semantic search is temporarily unavailable. See server logs for detail."));
        }
    }

    /// <summary>
    /// Scores only the rows whose owner is installation-scoped or names the resolved Campaign.
    /// </summary>
    /// <remarks>
    /// The Campaign arm is omitted from the SQL entirely when nothing resolved, rather than bound to a
    /// null that no row could match. A <c>= NULL</c> comparison is never true in SQL, so both spellings
    /// select the same rows - but only one of them says what it means to a reader, and only one of them
    /// stays correct if the ownership column ever gains a sentinel.
    /// </remarks>
    private async Task<Result<DivinationResult[]>> SearchManagedCampaignScopedAsync(
        DbConnection connection,
        string blobTableName,
        string primaryKeyColumn,
        string embeddingColumn,
        DivinationCampaignScope scope,
        float[] queryVector,
        int maxResults,
        float similarityThreshold,
        CancellationToken cancellationToken)
    {
        await using DbCommand cmd = connection.CreateCommand();

        string ownership = scope.CampaignId is null
            ? $"""o."{scope.OwnerScopeKindColumn}" = @globalScopeKind"""
            : $"""
              o."{scope.OwnerScopeKindColumn}" = @globalScopeKind
                   OR (o."{scope.OwnerScopeKindColumn}" = @campaignScopeKind
                       AND o."{scope.OwnerCampaignColumn}" = @campaignId)
              """;

        cmd.CommandText =
            $"""
            SELECT e."{primaryKeyColumn}", e."{embeddingColumn}"
            FROM "{blobTableName}" e
            INNER JOIN "{scope.OwnerTableName}" o ON e."{primaryKeyColumn}" = o."{scope.OwnerJoinColumn}"
            WHERE {ownership}
            """;

        AddParameter(cmd, "@globalScopeKind", scope.GlobalScopeKindCode);

        if (scope.CampaignId is { } campaignId)
        {
            AddParameter(cmd, "@campaignScopeKind", scope.CampaignScopeKindCode);

            // saga_memories.CampaignId - the only column SagaStorageKeys.CampaignScope ever names here -
            // holds the canonical uppercase dashed form. SagaMemoryScopeClassifier reads the Campaign out
            // of session_campaign_bindings.CampaignId and renders it canonically, so this column is
            // canonical whatever spelling the binding beside it holds; the version-5 sweep settles the
            // rows written before that was true. Under BINARY collation a bare ToString() here matched
            // only the half of the table whose binding came from the turn-begin repository, so
            // Campaign-scoped recall returned about half of a Campaign's memories and reported nothing
            // about the rest.
            AddParameter(cmd, "@campaignId", campaignId.ToString("D").ToUpperInvariant());
        }

        return await ScoreManagedRowsAsync(
            cmd,
            queryVector,
            maxResults,
            similarityThreshold,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<DivinationResult[]>> SearchManagedScopedAsync(
        DbConnection connection,
        string blobTableName,
        string primaryKeyColumn,
        string embeddingColumn,
        string scopeTableName,
        string scopeJoinColumn,
        string scopeFilterColumn,
        string scopeFilterValue,
        float[] queryVector,
        int maxResults,
        float similarityThreshold,
        CancellationToken cancellationToken)
    {
        await using DbCommand cmd = connection.CreateCommand();

        // Table/column names are internal constants owned by the calling feature's retrieval code
        // (never user input); only scopeFilterValue is a bound parameter.
        cmd.CommandText =
            $"""
            SELECT e."{primaryKeyColumn}", e."{embeddingColumn}"
            FROM "{blobTableName}" e
            INNER JOIN "{scopeTableName}" s ON e."{primaryKeyColumn}" = s."{scopeJoinColumn}"
            WHERE s."{scopeFilterColumn}" = @scopeFilterValue
            """;

        AddParameter(cmd, "@scopeFilterValue", scopeFilterValue);

        return await ScoreManagedRowsAsync(
            cmd,
            queryVector,
            maxResults,
            similarityThreshold,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<DivinationResult[]> SearchVecAsync(
        DbConnection connection,
        string tableName,
        string primaryKeyColumn,
        string embeddingColumn,
        float[] queryVector,
        int maxResults,
        float similarityThreshold,
        CancellationToken cancellationToken)
    {
        await using DbCommand cmd = connection.CreateCommand();

        // tableName/primaryKeyColumn/embeddingColumn are internal constants owned by the calling
        // feature's retrieval code (e.g. "entry_embeddings_vec", "EntryId", "Embedding") — never user
        // input — the same trust model the Data/Schema/ object files themselves rely on. The query
        // vector is a bound blob parameter.
        cmd.CommandText =
            $"""
            SELECT "{primaryKeyColumn}", distance
            FROM "{tableName}"
            WHERE "{embeddingColumn}" MATCH @queryVector
            ORDER BY distance
            LIMIT @maxResults
            """;

        AddParameter(cmd, "@queryVector", EmbeddingBlobCodec.Encode(queryVector));

        AddParameter(cmd, "@maxResults", maxResults);

        List<DivinationResult> results = [];

        await using DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string id = reader.GetString(0);

            double distance = reader.GetDouble(1);

            // Guaranteed by the explicit `distance_metric=cosine` on the vec0 column declaration in
            // Data/Schema/Accelerators/ — no version-specific distance-formula guessing.
            float similarity = (float)(1.0 - distance);

            if (similarity >= similarityThreshold)
            {
                results.Add(new DivinationResult(id, similarity, EmptyMetadata));
            }
        }

        return [.. results];
    }

    private async Task<Result<DivinationResult[]>> SearchManagedAsync(
        DbConnection connection,
        string blobTableName,
        string primaryKeyColumn,
        string embeddingColumn,
        float[] queryVector,
        int maxResults,
        float similarityThreshold,
        CancellationToken cancellationToken)
    {
        await using DbCommand cmd = connection.CreateCommand();

        cmd.CommandText =
            $"""
            SELECT "{primaryKeyColumn}", "{embeddingColumn}"
            FROM "{blobTableName}"
            """;

        return await ScoreManagedRowsAsync(
            cmd,
            queryVector,
            maxResults,
            similarityThreshold,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Scores every row from an already-configured command with a streaming top-K heap and never
    /// materializes a full scored list for <c>OrderBy</c>/<c>Take</c>.
    /// </summary>
    /// <remarks>
    /// A row is compared only when it can be: a vector of another width than the query's, a blob that is not
    /// a whole number of floats, and a vector with no direction (zero norm, or not finite) are each skipped
    /// and counted rather than scored as similarity <c>0</c>, which cleared a threshold of zero and let a
    /// corpus embedded at another width answer a query with arbitrary rows, and rather than letting one
    /// corrupt row fail the whole scan. The skips are logged once per scan, not once per row. When rows of
    /// another width were met and nothing at all could be scored, the corpus was embedded at a different
    /// width than the query and the answer is <see cref="ErrorCodes.Embeddings.DimensionMismatch"/>, not an
    /// empty result that looks like "nothing relevant". Equal similarity is ranked by id, lowest first, so
    /// which rows a full heap keeps does not depend on the order the database returns them in.
    /// </remarks>
    private async Task<Result<DivinationResult[]>> ScoreManagedRowsAsync(
        DbCommand cmd,
        float[] queryVector,
        int maxResults,
        float similarityThreshold,
        CancellationToken cancellationToken)
    {
        int take = Math.Max(0, maxResults);

        // The query vector is invariant across the whole scan, so its norm is computed once here rather
        // than re-derived inside the SIMD loop for every candidate row.
        double queryNormSquared = EmbeddingBlobCodec.NormSquared(queryVector);

        // Min-heap by (similarity, then id descending): when full, the peek is the weakest of the current
        // top-K, and among equal similarities that is the highest id.
        PriorityQueue<string, RankedHit> topK = new(WeakestFirst.Instance);

        int scored = 0;

        int mismatched = 0;

        int unreadable = 0;

        int noSignal = 0;

        await using DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string id = reader.GetString(0);

            // Scored straight off the BLOB. Decoding into a fresh float[] would double the per-row
            // allocation of a scan that reads every in-scope row on every retrieval, for a copy nothing
            // outlives this iteration.
            switch (TryScoreRow(reader[1], queryVector, queryNormSquared, out float similarity))
            {
                case RowScore.DimensionMismatch:

                    mismatched++;

                    continue;

                case RowScore.Unreadable:

                    unreadable++;

                    continue;

                case RowScore.NoSignal:

                    noSignal++;

                    continue;
            }

            scored++;

            if (similarity < similarityThreshold || take == 0)
            {
                continue;
            }

            RankedHit hit = new(similarity, id);

            if (topK.Count < take)
            {
                topK.Enqueue(id, hit);
            }
            else if (topK.TryPeek(out _, out RankedHit weakest) && WeakestFirst.Instance.Compare(hit, weakest) > 0)
            {
                _ = topK.Dequeue();

                topK.Enqueue(id, hit);
            }
        }

        int skipped = mismatched + unreadable + noSignal;

        if (skipped > 0)
        {
            logger.LogWarning(
                "Divination skipped {Skipped} stored embedding(s) it could not compare with a {QueryWidth}-dimension query: {Mismatched} of another width, {Unreadable} unreadable, {NoSignal} with no direction. A corpus embedded at another width needs an embeddings reset (POST /api/embeddings/reset?confirm=true) and a re-index.",
                skipped,
                queryVector.Length,
                mismatched,
                unreadable,
                noSignal);
        }

        if (scored == 0 && mismatched > 0)
        {
            return Result<DivinationResult[]>.Failure(new Error(
                ErrorCodes.Embeddings.DimensionMismatch,
                $"The stored embeddings are not {queryVector.Length}-dimension vectors, so nothing could be compared with this query. Run an embeddings reset (POST /api/embeddings/reset?confirm=true) and re-index."));
        }

        if (topK.Count == 0)
        {
            return Result<DivinationResult[]>.Success([]);
        }

        DivinationResult[] results = new DivinationResult[topK.Count];

        for (int i = results.Length - 1; i >= 0; i--)
        {
            if (!topK.TryDequeue(out string? id, out RankedHit ranked))
            {
                break;
            }

            results[i] = new DivinationResult(id, ranked.Similarity, EmptyMetadata);
        }

        return Result<DivinationResult[]>.Success(results);
    }

    private enum RowScore
    {
        Scored,

        DimensionMismatch,

        Unreadable,

        NoSignal,
    }

    private static RowScore TryScoreRow(
        object? embeddingColumn,
        float[] queryVector,
        double queryNormSquared,
        out float similarity)
    {
        similarity = 0f;

        // SQL NULL arrives as DBNull and is not a blob, so it is unreadable here rather than an exception per
        // row; a null reference would otherwise view as an empty vector and be counted as another width.
        if (embeddingColumn is not byte[] blob)
        {
            return RowScore.Unreadable;
        }

        ReadOnlySpan<float> candidate;

        try
        {
            candidate = EmbeddingBlobCodec.AsVector(blob);
        }
        catch (InvalidOperationException)
        {
            return RowScore.Unreadable;
        }

        if (candidate.Length != queryVector.Length)
        {
            return RowScore.DimensionMismatch;
        }

        return EmbeddingBlobCodec.TryCosineSimilarity(queryVector, queryNormSquared, candidate, out similarity)
            ? RowScore.Scored
            : RowScore.NoSignal;
    }

    private readonly record struct RankedHit(float Similarity, string Id);

    /// <summary>
    /// Orders the weakest hit first: lower similarity, then, for equal similarity, the higher id (so the
    /// lowest id is the last to be displaced and ranks first in the answer).
    /// </summary>
    private sealed class WeakestFirst : IComparer<RankedHit>
    {
        public static WeakestFirst Instance { get; } = new();

        public int Compare(RankedHit left, RankedHit right)
        {
            int bySimilarity = left.Similarity.CompareTo(right.Similarity);

            return bySimilarity != 0
                ? bySimilarity
                : string.CompareOrdinal(right.Id, left.Id);
        }
    }

    private static void AddParameter(DbCommand cmd, string name, object value)
    {
        DbParameter parameter = cmd.CreateParameter();

        parameter.ParameterName = name;

        parameter.Value = value;

        cmd.Parameters.Add(parameter);
    }

    private static string DeriveBlobTableName(string vecTableName) =>
        vecTableName.EndsWith(VecTableSuffix, StringComparison.Ordinal)
            ? vecTableName[..^VecTableSuffix.Length]
            : vecTableName;
}
