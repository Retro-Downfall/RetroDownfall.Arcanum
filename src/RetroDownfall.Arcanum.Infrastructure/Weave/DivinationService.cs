using System.Data;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Infrastructure.Weave;

// Divination searches the named durable BLOB table through the scoped Grimoire connection.
// Table and column identifiers are code-owned constants, never user input.
//
// Managed-path mechanics: every matching row is scored, results are tracked in a streaming top-K
// heap (no full scored list + OrderBy), and scoped searches join the scope table in SQL instead of an
// unbounded IN (...) of every chunk id.
internal sealed class DivinationService(
    ArcanumDbContext db,
    ILogger<DivinationService> logger,
    IOptionsMonitor<ArcanumSettings> options,
    TimeProvider? timeProvider = null) : IDivinationService
{
    private bool RankingEnabled => options.CurrentValue.Features.LongRest;

    private DateTimeOffset RankingInstant => (timeProvider ?? TimeProvider.System).GetUtcNow();

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

            string blobTableName = tableName;

            if (IsSaga(blobTableName))
            {
                // Eligibility is part of candidate selection, before the heap ranks and takes K.
                return await SagaRankingEvidenceReader.InSnapshotAsync(connection,
                    () => SearchManagedSagaAsync(connection, primaryKeyColumn, embeddingColumn,
                        queryVector, maxResults, similarityThreshold, cancellationToken), cancellationToken, enabled: RankingEnabled)
                    .ConfigureAwait(false);
            }

            return await SearchManagedAsync(
                    connection,
                    blobTableName,
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

            string blobTableName = tableName;

            if (IsSaga(blobTableName))
            {
                return await SagaRankingEvidenceReader.InSnapshotAsync(connection,
                    () => SearchManagedScopedAsync(connection, blobTableName, primaryKeyColumn, embeddingColumn,
                        scopeTableName, scopeJoinColumn, scopeFilterColumn, scopeFilterValue,
                        queryVector, maxResults, similarityThreshold, cancellationToken), cancellationToken, enabled: RankingEnabled)
                    .ConfigureAwait(false);
            }

            // Scope filtering happens in SQL before scoring and top-K selection.
            return await SearchManagedScopedAsync(
                connection,
                blobTableName,
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

            string blobTableName = tableName;

            if (IsSaga(blobTableName))
            {
                return await SagaRankingEvidenceReader.InSnapshotAsync(connection,
                    () => SearchManagedCampaignScopedAsync(connection, blobTableName, primaryKeyColumn,
                        embeddingColumn, scope, queryEmbedding.Vector.ToArray(), maxResults,
                        similarityThreshold, cancellationToken), cancellationToken, enabled: RankingEnabled).ConfigureAwait(false);
            }

            return await SearchManagedCampaignScopedAsync(
                connection,
                blobTableName,
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

        bool saga = IsSaga(blobTableName);

        bool longRest = saga && await LongRestRecallSql.AvailableAsync(connection, cancellationToken).ConfigureAwait(false);

        string sagaJoin = saga ? $"INNER JOIN saga_memories m ON m.\"Id\" = e.\"{primaryKeyColumn}\" {SagaRankingReader.Join(longRest)}" : "";

        SagaRankingReader? ranking = saga ? await SagaRankingReader.CreateAsync(connection, RankingEnabled, RankingInstant, cancellationToken).ConfigureAwait(false) : null;

        string eligible = saga ? $"m.\"RetiredAtUtc\" IS NULL AND {LongRestRecallSql.Eligible("m", longRest)}" : "1 = 1";

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
            {(saga ? SagaRankingReader.Projection(longRest) : "")}
            FROM "{blobTableName}" e
            INNER JOIN "{scope.OwnerTableName}" o ON e."{primaryKeyColumn}" = o."{scope.OwnerJoinColumn}"
            {sagaJoin}
            WHERE ({ownership}) AND {eligible}
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
            cancellationToken, ranking).ConfigureAwait(false);
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

        bool saga = IsSaga(blobTableName);

        bool longRest = saga && await LongRestRecallSql.AvailableAsync(connection, cancellationToken).ConfigureAwait(false);

        string sagaJoin = saga ? $"INNER JOIN saga_memories m ON m.\"Id\" = e.\"{primaryKeyColumn}\" {SagaRankingReader.Join(longRest)}" : "";

        SagaRankingReader? ranking = saga ? await SagaRankingReader.CreateAsync(connection, RankingEnabled, RankingInstant, cancellationToken).ConfigureAwait(false) : null;

        string eligible = saga ? $"m.\"RetiredAtUtc\" IS NULL AND {LongRestRecallSql.Eligible("m", longRest)}" : "1 = 1";

        // Apply the opt-out before scoring/top-K so stored document chunks cannot displace ordinary
        // text after Comprehension is disabled. Attachment chunks carry their immutable source MIME.
        if (blobTableName == "session_attachment_embeddings"
            && scopeTableName == "session_attachment_chunks"
            && !SessionAttachmentComprehensionPolicy.IsEnabled(options.CurrentValue))
        {
            eligible =
                $"{SessionAttachmentDocumentMimeSql.NormalizedExpression("s.MimeType")} NOT IN ({SessionAttachmentDocumentMimeSql.ParameterList})";

            foreach (KeyValuePair<string, string> documentMime in SessionAttachmentDocumentMimeSql.Parameters)
            {
                AddParameter(cmd, documentMime.Key, documentMime.Value);
            }
        }

        // Table/column names are internal constants owned by the calling feature's retrieval code
        // (never user input); scope values and document MIME are bound parameters.
        cmd.CommandText =
            $"""
            SELECT e."{primaryKeyColumn}", e."{embeddingColumn}"
            {(saga ? SagaRankingReader.Projection(longRest) : "")}
            FROM "{blobTableName}" e
            INNER JOIN "{scopeTableName}" s ON e."{primaryKeyColumn}" = s."{scopeJoinColumn}"
            {sagaJoin}
            WHERE s."{scopeFilterColumn}" = @scopeFilterValue AND {eligible}
            """;

        AddParameter(cmd, "@scopeFilterValue", scopeFilterValue);

        return await ScoreManagedRowsAsync(
            cmd,
            queryVector,
            maxResults,
            similarityThreshold,
            cancellationToken, ranking).ConfigureAwait(false);
    }

    private async Task<Result<DivinationResult[]>> SearchManagedSagaAsync(
        DbConnection connection,
        string primaryKeyColumn,
        string embeddingColumn,
        float[] queryVector,
        int maxResults,
        float similarityThreshold,
        CancellationToken cancellationToken)
    {
        bool longRest = await LongRestRecallSql.AvailableAsync(connection, cancellationToken).ConfigureAwait(false);

        SagaRankingReader ranking = await SagaRankingReader.CreateAsync(connection, RankingEnabled, RankingInstant, cancellationToken).ConfigureAwait(false);

        await using DbCommand cmd = connection.CreateCommand();

        cmd.CommandText =
            $"""
            SELECT e."{primaryKeyColumn}", e."{embeddingColumn}"
            {SagaRankingReader.Projection(longRest)}
            FROM saga_memory_embeddings e
            INNER JOIN saga_memories m ON m."Id" = e."{primaryKeyColumn}"
            {SagaRankingReader.Join(longRest)}
            WHERE m."RetiredAtUtc" IS NULL AND {LongRestRecallSql.Eligible("m", longRest)}
            """;

        return await ScoreManagedRowsAsync(cmd, queryVector, maxResults, similarityThreshold, cancellationToken, ranking).ConfigureAwait(false);
    }

    private static bool IsSaga(string blobTableName) =>
        string.Equals(blobTableName, "saga_memory_embeddings", StringComparison.Ordinal);

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
        CancellationToken cancellationToken,
        SagaRankingReader? ranking = null)
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

        await using SqliteDataReader reader = (SqliteDataReader)await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string id = reader.GetString(0);

            switch (TryScoreRow(reader, 1, queryVector, queryNormSquared, out float similarity))
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

            SagaRankingComponents? components = ranking?.Enabled == true ? ranking.Evaluate(reader, similarity) : null;

            if (ranking?.Enabled == true && !ranking.Eligible(reader, components!))
            {
                continue;
            }

            RankedHit hit = new(similarity, id, components?.Score, components is null ? null : components with { VersionId = null });

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

            results[i] = new DivinationResult(id, ranked.Similarity, EmptyMetadata, ranked.Ranking);
        }

        return Result<DivinationResult[]>.Success(results);
    }

    internal enum RowScore
    {
        Scored,

        DimensionMismatch,

        Unreadable,

        NoSignal,
    }

    internal static RowScore TryScoreRow(
        SqliteDataReader reader,
        int embeddingOrdinal,
        float[] queryVector,
        double queryNormSquared,
        out float similarity)
    {
        similarity = 0f;

        SQLitePCL.sqlite3_stmt statement = reader.Handle!;

        // Check the storage class before requesting a BLOB: SQLite otherwise coerces text/numbers
        // into bytes. NULL and non-BLOB values remain unreadable, rather than another vector width.
        if (SQLitePCL.raw.sqlite3_column_type(statement, embeddingOrdinal) != SQLitePCL.raw.SQLITE_BLOB)
        {
            return RowScore.Unreadable;
        }

        // The native bytes belong to this reader's current row. Finish scoring synchronously here,
        // before another read/step/reset/dispose can invalidate them; no span escapes this method.
        // Reading through the object indexer instead would allocate a full managed byte[] per row.
        ReadOnlySpan<byte> blob = SQLitePCL.raw.sqlite3_column_blob(statement, embeddingOrdinal);

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

    private readonly record struct RankedHit(float Similarity, string Id, float? Score = null, SagaRankingComponents? Ranking = null);

    /// <summary>
    /// Orders the weakest hit first: lower similarity, then, for equal similarity, the higher id (so the
    /// lowest id is the last to be displaced and ranks first in the answer).
    /// </summary>
    private sealed class WeakestFirst : IComparer<RankedHit>
    {
        public static WeakestFirst Instance { get; } = new();

        public int Compare(RankedHit left, RankedHit right)
        {
            int bySimilarity = (left.Score ?? left.Similarity).CompareTo(right.Score ?? right.Similarity);

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
}
