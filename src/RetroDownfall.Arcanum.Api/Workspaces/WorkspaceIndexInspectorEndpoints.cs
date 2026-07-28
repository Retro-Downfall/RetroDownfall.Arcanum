using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Health;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Api.TheForge;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;

namespace RetroDownfall.Arcanum.Api.Workspaces;

/// <summary>
/// Phase 7 (RAG / The Weave inspector) — read-only inspection of a workspace's indexed chunks:
/// <c>GET /api/workspaces/{id}/files/index/status</c> and
/// <c>GET /api/workspaces/{id}/files/chunks?relativePath=&amp;limit=&amp;offset=</c>. Same security and
/// bounds as the other workspace file routes: API key required (inherited from the <c>/api</c> group),
/// workspace id resolved through the registry only (no arbitrary filesystem paths), <c>relativePath</c>
/// validated/canonicalized, <c>limit</c>/<c>offset</c> clamped, content preview capped. Returns
/// previews/snippets, never full file contents. No new tables/state, so no PERSISTENCE change.
/// </summary>
internal static class WorkspaceIndexInspectorEndpoints
{

    private const int DefaultChunkLimit = 50;

    private const int MaxChunkLimit = 200;

    private const int MaxRelativePathChars = 1_024;

    public static RouteGroupBuilder MapWorkspaceIndexInspectorEndpoints(this RouteGroupBuilder apiGroup)
    {

        apiGroup.MapGet("/workspaces/{id}/files/index/status", HandleStatusAsync)
            .WithName("GetWorkspaceIndexStatus");

        apiGroup.MapGet("/workspaces/{id}/files/chunks", HandleChunksAsync)
            .WithName("GetWorkspaceFileChunks");

        return apiGroup;

    }

    private static async Task<IResult> HandleStatusAsync(
        string id,
        IWorkspaceRegistry registry,
        IWorkspaceIndexInspectorService inspector,
        IOptionsMonitor<ArcanumSettings> options,
        WeaveIndexAvailability weaveIndexAvailability,
        HttpContext ctx)
    {

        string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

        WorkspaceInfo? workspace = await registry.GetAsync(id, ctx.RequestAborted).ConfigureAwait(false);

        if (workspace is null)
        {

            return Results.Json(
                ApiResponse<WorkspaceIndexStatusDto>.FromResult(
                    Result<WorkspaceIndexStatusDto>.Failure(new Error(ErrorCodes.Workspace.NotFound, "No workspace exists with that id.")),
                    traceId),
                ArcanumJsonContext.Default.ApiResponseWorkspaceIndexStatusDto,
                statusCode: ArcanumErrorMapper.ResolveStatusCode(ErrorCodes.Workspace.NotFound));

        }

        EmbeddingSettings embeddings = options.CurrentValue.ResolveEmbeddings();

        (bool _, string vectorMode, string vectorDiagnostic, int _) =
            EmbeddingsVectorStatus.Resolve(embeddings, weaveIndexAvailability);

        bool indexingEnabled = embeddings.Enabled && embeddings.CodebaseRetrievalEnabled;

        WorkspaceIndexStatusDto dto = await inspector
            .GetStatusAsync(workspace, vectorMode, vectorDiagnostic, indexingEnabled, ctx.RequestAborted)
            .ConfigureAwait(false);

        return Results.Ok(
            ApiResponse<WorkspaceIndexStatusDto>.FromResult(Result<WorkspaceIndexStatusDto>.Success(dto), traceId));

    }

    private static async Task<IResult> HandleChunksAsync(
        string id,
        string? relativePath,
        int? limit,
        int? offset,
        IWorkspaceRegistry registry,
        IWorkspaceIndexInspectorService inspector,
        HttpContext ctx)
    {

        string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

        WorkspaceInfo? workspace = await registry.GetAsync(id, ctx.RequestAborted).ConfigureAwait(false);

        if (workspace is null)
        {

            return ChunksFailure(traceId, new Error(ErrorCodes.Workspace.NotFound, "No workspace exists with that id."));

        }

        // Validate/canonicalize relativePath. It is only ever used as a SQL string equality against the
        // RelativePath column (never joined to the filesystem root), so there is no path-traversal risk;
        // the rooted/'..' rejection is defensive and keeps the parameter shape honest. Null = no filter.
        string? canonicalRelativePath = null;

        if (!string.IsNullOrWhiteSpace(relativePath))
        {

            string trimmed = relativePath.Trim();

            if (trimmed.Length > MaxRelativePathChars
                || Path.IsPathRooted(trimmed)
                || ContainsParentDirectorySegment(trimmed))
            {

                return ChunksFailure(
                    traceId,
                    new Error(
                        ErrorCodes.Workspace.PathTraversal,
                        "relativePath must be a relative path within the workspace (no rooted paths or '..' segments)."));

            }

            canonicalRelativePath = trimmed.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);

        }

        int clampedLimit = Math.Clamp(limit ?? DefaultChunkLimit, 1, MaxChunkLimit);

        int clampedOffset = Math.Max(0, offset ?? 0);

        WorkspaceFileChunkPage page = await inspector
            .GetChunksAsync(workspace, canonicalRelativePath, clampedLimit, clampedOffset, ctx.RequestAborted)
            .ConfigureAwait(false);

        return Results.Ok(
            ApiResponse<WorkspaceFileChunkPage>.FromResult(Result<WorkspaceFileChunkPage>.Success(page), traceId));

    }

    private static IResult ChunksFailure(string traceId, Error error) =>
        Results.Json(
            ApiResponse<WorkspaceFileChunkPage>.FromResult(Result<WorkspaceFileChunkPage>.Failure(error), traceId),
            ArcanumJsonContext.Default.ApiResponseWorkspaceFileChunkPage,
            statusCode: ArcanumErrorMapper.ResolveStatusCode(error.Code));

    private static bool ContainsParentDirectorySegment(string path)
    {

        string[] segments = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);

        foreach (string segment in segments)
        {

            if (segment == "..")
            {

                return true;

            }

        }

        return false;

    }

}
