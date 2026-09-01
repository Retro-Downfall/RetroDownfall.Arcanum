using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Api.Primitives;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;

namespace RetroDownfall.Arcanum.Api.Workspaces;

/// <summary>
/// Semantic codebase retrieval: <c>POST /api/workspaces/{id}/files/divine</c> (semantic search over a workspace's
/// indexed files) and <c>POST /api/workspaces/{id}/files/index</c> (manual immediate re-index). Every
/// step degrades gracefully when semantic codebase retrieval is disabled or unavailable — see the
/// graceful-degradation matrix in <c>docs/Arcanum.DESIGN.md</c> §21.4.
/// </summary>
internal static class WorkspaceDivinationEndpoints
{

    private const int ContentPreviewChars = 500;

    // Bounds the free-text query sent to the embedding provider — matches
    // ArcanumSettingClamps.ArchiveSearchMaxQueryLength's upper bound, the repo's existing cap for
    // similarly-purposed free-text search queries. Without this, an oversized query is forwarded
    // straight to the provider on every request (cost/latency DoS vector).
    private const int MaxQueryChars = 4_096;

    public static RouteGroupBuilder MapWorkspaceDivinationEndpoints(this RouteGroupBuilder apiGroup)
    {

        apiGroup.MapPost(
            "/workspaces/{id}/files/divine",
            async (
                string id,
                WorkspaceSemanticSearchRequest? request,
                IWorkspaceRegistry registry,
                IWeaveService weaveService,
                IDivinationService divinationService,
                ArcanumDbContext db,
                IGrimoireOrdinaryConnectionFactory connections,
                IOptionsMonitor<ArcanumSettings> options,
                HttpContext ctx) =>
            {

                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                EmbeddingSettings embeddings = options.CurrentValue.ResolveEmbeddings();

                if (!embeddings.Enabled || !embeddings.CodebaseRetrievalEnabled)
                {

                    return DivinationFailureResult(
                        traceId,
                        new Error(
                            ErrorCodes.Embeddings.FeatureDisabled,
                            "Semantic codebase retrieval is disabled (Arcanum:Features:Embeddings and Arcanum:Features:CodebaseRetrieval must both be true)."));

                }

                WorkspaceInfo? workspace = await registry.GetAsync(id, ctx.RequestAborted).ConfigureAwait(false);

                if (workspace is null)
                {

                    return DivinationFailureResult(
                        traceId,
                        new Error(ErrorCodes.Workspace.NotFound, "No workspace exists with that id."));

                }

                if (request is null || string.IsNullOrWhiteSpace(request.Query))
                {

                    return DivinationFailureResult(traceId, new Error(ErrorCodes.Validation.InvalidBody, "Query is required."));

                }

                if (request.Query.Length > MaxQueryChars)
                {

                    return DivinationFailureResult(
                        traceId,
                        new Error(
                            ErrorCodes.Validation.InvalidBody,
                            $"Query must not exceed {MaxQueryChars} characters."));

                }

                if (!weaveService.IsAvailable)
                {

                    return DivinationFailureResult(
                        traceId,
                        new Error(ErrorCodes.Embeddings.ProviderUnavailable, "The embedding provider is unavailable."));

                }

                Result<Embedding<float>> embedResult = await weaveService
                    .EmbedAsync(request.Query.Trim(), ctx.RequestAborted)
                    .ConfigureAwait(false);

                if (embedResult.IsFailure)
                {

                    return DivinationFailureResult(traceId, embedResult.Error);

                }

                CodebaseEmbeddingSettings codebase = embeddings.Codebase ?? new CodebaseEmbeddingSettings();

                int limit = ArcanumSettingClamps.EmbeddingsCodebaseMaxRetrievedChunks(request.Limit ?? codebase.MaxRetrievedChunks);

                float similarityThreshold = ArcanumSettingClamps.EmbeddingsSimilarityThreshold(embeddings.SimilarityThreshold);

                // Scoped to this workspace's chunks before ranking (not just at the display join
                // below): an unscoped global KNN capped at `limit` could be dominated by another
                // workspace's chunks, starving out this workspace's genuinely-closest matches before
                // they ever reach the join.
                Result<DivinationResult[]> searchResult = await divinationService
                    .SearchScopedAsync(
                        "workspace_file_embeddings_vec",
                        "ChunkId",
                        "Embedding",
                        "workspace_file_chunks",
                        "ChunkId",
                        "WorkspacePath",
                        workspace.Path,
                        embedResult.Value,
                        limit,
                        similarityThreshold,
                        ctx.RequestAborted)
                    .ConfigureAwait(false);

                if (searchResult.IsFailure)
                {

                    return DivinationFailureResult(traceId, searchResult.Error);

                }

                WorkspaceSearchResult[] joined = await JoinWorkspaceChunksAsync(
                    db,
                    connections,
                    searchResult.Value,
                    workspace.Path,
                    limit,
                    ctx.RequestAborted).ConfigureAwait(false);

                return Results.Ok(
                    ApiResponse<WorkspaceSearchResult[]>.FromResult(Result<WorkspaceSearchResult[]>.Success(joined), traceId));

            })
        .WithName("WorkspaceFileDivination");

        apiGroup.MapPost(
            "/workspaces/{id}/files/index",
            async (
                string id,
                IWorkspaceRegistry registry,
                IWorkspaceIndexingService indexingService,
                IOptionsMonitor<ArcanumSettings> options,
                IHostApplicationLifetime lifetime,
                ILoggerFactory loggerFactory,
                HttpContext ctx) =>
            {

                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                EmbeddingSettings embeddings = options.CurrentValue.ResolveEmbeddings();

                if (!embeddings.Enabled || !embeddings.CodebaseRetrievalEnabled)
                {

                    return Results.Json(
                        ApiResponse<bool>.FromResult(
                            Result<bool>.Failure(new Error(
                                ErrorCodes.Embeddings.FeatureDisabled,
                                "Semantic codebase retrieval is disabled (Arcanum:Features:Embeddings and Arcanum:Features:CodebaseRetrieval must both be true).")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseBoolean,
                        statusCode: ArcanumErrorMapper.ResolveStatusCode(ErrorCodes.Embeddings.FeatureDisabled));

                }

                WorkspaceInfo? workspace = await registry.GetAsync(id, ctx.RequestAborted).ConfigureAwait(false);

                if (workspace is null)
                {

                    return Results.Json(
                        ApiResponse<bool>.FromResult(
                            Result<bool>.Failure(new Error(ErrorCodes.Workspace.NotFound, "No workspace exists with that id.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseBoolean,
                        statusCode: ArcanumErrorMapper.ResolveStatusCode(ErrorCodes.Workspace.NotFound));

                }

                // Runs on IHostApplicationLifetime.ApplicationStopping rather than ctx.RequestAborted: a
                // full re-index of a large workspace can take far longer than callers want to hold a
                // connection open for, and this must not be silently killed just because the client gave
                // up waiting or a proxy timed out the request. IndexNowAsync opens its own DI scope, so
                // it does not depend on this request's scope surviving past the response.
                ILogger logger = loggerFactory.CreateLogger("WorkspaceFileIndex");

                _ = Task.Run(
                    async () =>
                    {

                        try
                        {

                            await indexingService.IndexNowAsync(workspace.Path, lifetime.ApplicationStopping).ConfigureAwait(false);

                        }
                        catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested)
                        {

                            // Host shutting down — nothing to log, IndexNowAsync itself already treats
                            // this as a normal cancellation.

                        }
                        catch (Exception ex)
                        {

                            logger.LogWarning(ex, "Background re-index requested via POST /files/index failed for workspace {WorkspaceId}.", id);

                        }

                    },
                    lifetime.ApplicationStopping);

                return Results.Json(
                    ApiResponse<bool>.FromResult(Result<bool>.Success(true), traceId),
                    ArcanumJsonContext.Default.ApiResponseBoolean,
                    statusCode: StatusCodes.Status202Accepted);

            })
        .WithName("WorkspaceFileIndex");

        return apiGroup;

    }

    private static IResult DivinationFailureResult(string traceId, Error error) =>
        Results.Json(
            ApiResponse<WorkspaceSearchResult[]>.FromResult(Result<WorkspaceSearchResult[]>.Failure(error), traceId),
            ArcanumJsonContext.Default.ApiResponseWorkspaceSearchResultArray,
            statusCode: ArcanumErrorMapper.ResolveStatusCode(error.Code));

    /// <summary>
    /// Joins Divination hits (ChunkId + similarity) against <c>workspace_file_chunks</c>, scoped to
    /// <paramref name="workspacePath"/>, computing <see cref="WorkspaceSearchResult.TotalChunks"/> per
    /// file and truncating content to a preview.
    /// </summary>
    private static async Task<WorkspaceSearchResult[]> JoinWorkspaceChunksAsync(
        ArcanumDbContext db,
        IGrimoireOrdinaryConnectionFactory connections,
        DivinationResult[] hits,
        string workspacePath,
        int limit,
        CancellationToken cancellationToken)
    {

        if (hits.Length == 0)
        {

            return [];

        }

        Dictionary<string, float> similarityByChunkId = new(StringComparer.Ordinal);

        foreach (DivinationResult hit in hits)
        {

            similarityByChunkId[hit.Id] = hit.Similarity;

        }

        if (db.Database.GetDbConnection() is not SqliteConnection scopedConnection)
        {
            throw new InvalidOperationException("The Grimoire requires a SQLCipher connection.");

        }

        Result<IGrimoireOrdinaryConnectionLease> acquired = await connections
            .AcquireScopedAsync(
                scopedConnection,
                CovenantSqliteConnectionMode.ReadOnly,
                cancellationToken)
            .ConfigureAwait(false);

        if (acquired.IsFailure)
        {

            throw new GrimoireMaintenanceUnavailableException();

        }

        await using IGrimoireOrdinaryConnectionLease lease = acquired.Value;

        DbConnection connection = lease.Connection;

        List<(string ChunkId, string RelativePath, int ChunkIndex, string Content)> rows = [];

        await using (DbCommand cmd = connection.CreateCommand())
        {

            StringBuilder sql = new(
                """
                SELECT "ChunkId", "RelativePath", "ChunkIndex", "Content"
                FROM "workspace_file_chunks"
                WHERE "WorkspacePath" = @workspacePath AND "ChunkId" IN (
                """);

            AddParameter(cmd, "@workspacePath", workspacePath);

            for (int i = 0; i < hits.Length; i++)
            {

                if (i > 0)
                {

                    sql.Append(", ");

                }

                string paramName = $"@id{i.ToString(CultureInfo.InvariantCulture)}";

                sql.Append(paramName);

                AddParameter(cmd, paramName, hits[i].Id);

            }

            sql.Append(')');

            cmd.CommandText = sql.ToString();

            await using DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3)));

            }

        }

        if (rows.Count == 0)
        {

            return [];

        }

        Dictionary<string, int> totalChunksByPath = await GetTotalChunksByPathAsync(
            connection,
            workspacePath,
            rows.Select(static r => r.RelativePath).Distinct(StringComparer.Ordinal),
            cancellationToken).ConfigureAwait(false);

        return rows
            .Select(r => new WorkspaceSearchResult(
                r.RelativePath,
                r.ChunkIndex,
                totalChunksByPath.GetValueOrDefault(r.RelativePath, 1),
                similarityByChunkId.GetValueOrDefault(r.ChunkId, 0f),
                r.Content[..Utf8Truncation.SafeCharSliceLength(r.Content, ContentPreviewChars)]))
            .OrderByDescending(static r => r.Similarity)
            .Take(limit)
            .ToArray();

    }

    private static async Task<Dictionary<string, int>> GetTotalChunksByPathAsync(
        DbConnection connection,
        string workspacePath,
        IEnumerable<string> relativePaths,
        CancellationToken cancellationToken)
    {

        List<string> paths = [.. relativePaths];

        Dictionary<string, int> result = new(StringComparer.Ordinal);

        if (paths.Count == 0)
        {

            return result;

        }

        await using DbCommand cmd = connection.CreateCommand();

        StringBuilder sql = new(
            """
            SELECT "RelativePath", COUNT(*)
            FROM "workspace_file_chunks"
            WHERE "WorkspacePath" = @workspacePath AND "RelativePath" IN (
            """);

        AddParameter(cmd, "@workspacePath", workspacePath);

        for (int i = 0; i < paths.Count; i++)
        {

            if (i > 0)
            {

                sql.Append(", ");

            }

            string paramName = $"@path{i.ToString(CultureInfo.InvariantCulture)}";

            sql.Append(paramName);

            AddParameter(cmd, paramName, paths[i]);

        }

        sql.Append(") GROUP BY \"RelativePath\"");

        cmd.CommandText = sql.ToString();

        await using DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        await GrimoireScopedConsumerTestSeam
            .PauseAsync("WorkspaceDivinationEndpoints.GetTotalChunksByPathAsync", cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            result[reader.GetString(0)] = reader.GetInt32(1);

        }

        return result;

    }

    private static void AddParameter(DbCommand cmd, string name, object value)
    {

        DbParameter parameter = cmd.CreateParameter();

        parameter.ParameterName = name;

        parameter.Value = value;

        cmd.Parameters.Add(parameter);

    }

}
