using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
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
// EmbeddingBlobCodec. Column names are shared between both tables by convention (see
// WeaveSchemaInitializer) so the same primaryKeyColumn/embeddingColumn pair works either way.
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
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            }

            float[] queryVector = queryEmbedding.Vector.ToArray();

            DivinationResult[] results = availability.IsVecAvailable
                ? await SearchVecAsync(
                    connection,
                    tableName,
                    primaryKeyColumn,
                    embeddingColumn,
                    queryVector,
                    maxResults,
                    similarityThreshold,
                    cancellationToken).ConfigureAwait(false)
                : await SearchManagedAsync(
                    connection,
                    DeriveBlobTableName(tableName),
                    primaryKeyColumn,
                    embeddingColumn,
                    queryVector,
                    maxResults,
                    similarityThreshold,
                    cancellationToken).ConfigureAwait(false);

            return Result<DivinationResult[]>.Success(results);

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
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            }

            HashSet<string> scopeIds = await LoadScopeIdsAsync(
                connection,
                scopeTableName,
                scopeJoinColumn,
                scopeFilterColumn,
                scopeFilterValue,
                cancellationToken).ConfigureAwait(false);

            if (scopeIds.Count == 0)
            {

                return Result<DivinationResult[]>.Success([]);

            }

            float[] queryVector = queryEmbedding.Vector.ToArray();

            // The vec0 KNN path (used when IsVecAvailable) has no per-row partition key in its current
            // schema, so a scoped search always ranks via the managed brute-force path below instead —
            // but bounded to just the scoped candidate rows fetched above, not a full-table scan.
            DivinationResult[] results = await SearchManagedScopedAsync(
                connection,
                DeriveBlobTableName(tableName),
                primaryKeyColumn,
                embeddingColumn,
                scopeIds,
                queryVector,
                maxResults,
                similarityThreshold,
                cancellationToken).ConfigureAwait(false);

            return Result<DivinationResult[]>.Success(results);

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

    private static async Task<HashSet<string>> LoadScopeIdsAsync(
        DbConnection connection,
        string scopeTableName,
        string scopeJoinColumn,
        string scopeFilterColumn,
        string scopeFilterValue,
        CancellationToken cancellationToken)
    {

        await using DbCommand cmd = connection.CreateCommand();

        // scopeTableName/scopeJoinColumn/scopeFilterColumn are internal constants owned by the calling
        // feature's retrieval code (never user input), same as tableName/primaryKeyColumn/embeddingColumn
        // in SearchVecAsync — only scopeFilterValue is a bound parameter.
        cmd.CommandText =
            $"""
            SELECT DISTINCT "{scopeJoinColumn}"
            FROM "{scopeTableName}"
            WHERE "{scopeFilterColumn}" = @scopeFilterValue
            """;

        AddParameter(cmd, "@scopeFilterValue", scopeFilterValue);

        HashSet<string> ids = new(StringComparer.Ordinal);

        await using DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            ids.Add(reader.GetString(0));

        }

        return ids;

    }

    private static async Task<DivinationResult[]> SearchManagedScopedAsync(
        DbConnection connection,
        string blobTableName,
        string primaryKeyColumn,
        string embeddingColumn,
        HashSet<string> scopeIds,
        float[] queryVector,
        int maxResults,
        float similarityThreshold,
        CancellationToken cancellationToken)
    {

        await using DbCommand cmd = connection.CreateCommand();

        StringBuilder sql = new(
            $"""
            SELECT "{primaryKeyColumn}", "{embeddingColumn}"
            FROM "{blobTableName}"
            WHERE "{primaryKeyColumn}" IN (
            """);

        int i = 0;

        foreach (string id in scopeIds)
        {

            if (i > 0)
            {
                sql.Append(", ");

            }

            string paramName = $"@scopeId{i.ToString(CultureInfo.InvariantCulture)}";

            sql.Append(paramName);

            AddParameter(cmd, paramName, id);

            i++;

        }

        sql.Append(')');

        cmd.CommandText = sql.ToString();

        List<(string Id, float Similarity)> scored = [];

        await using DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            string id = reader.GetString(0);

            byte[] blob = (byte[])reader[1];

            float[] candidate = EmbeddingBlobCodec.Decode(blob);

            float similarity = EmbeddingBlobCodec.CosineSimilarity(queryVector, candidate);

            if (similarity >= similarityThreshold)
            {

                scored.Add((id, similarity));

            }

        }

        return scored
            .OrderByDescending(static s => s.Similarity)
            .Take(maxResults)
            .Select(static s => new DivinationResult(s.Id, s.Similarity, EmptyMetadata))
            .ToArray();

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
        // input — interpolated the same way GrimoireSqlSchemaMigrator interpolates its own fixed
        // migration identifiers into SQL. The query vector is a bound blob parameter.
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

            // Guaranteed by the explicit `distance_metric=cosine` on the vec0 column declaration (see
            // WeaveSchemaInitializer) — no version-specific distance-formula guessing.
            float similarity = (float)(1.0 - distance);

            if (similarity >= similarityThreshold)
            {

                results.Add(new DivinationResult(id, similarity, EmptyMetadata));

            }

        }

        return [.. results];

    }

    private static async Task<DivinationResult[]> SearchManagedAsync(
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

        List<(string Id, float Similarity)> scored = [];

        await using DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            string id = reader.GetString(0);

            byte[] blob = (byte[])reader[1];

            float[] candidate = EmbeddingBlobCodec.Decode(blob);

            float similarity = EmbeddingBlobCodec.CosineSimilarity(queryVector, candidate);

            if (similarity >= similarityThreshold)
            {

                scored.Add((id, similarity));

            }

        }

        return scored
            .OrderByDescending(static s => s.Similarity)
            .Take(maxResults)
            .Select(static s => new DivinationResult(s.Id, s.Similarity, EmptyMetadata))
            .ToArray();

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
