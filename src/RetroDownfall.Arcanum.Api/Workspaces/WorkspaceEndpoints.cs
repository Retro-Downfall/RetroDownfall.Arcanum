using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;

namespace RetroDownfall.Arcanum.Api.Workspaces;

internal static class WorkspaceEndpoints
{

    public static RouteGroupBuilder MapWorkspaceEndpoints(this RouteGroupBuilder apiGroup)
    {

        apiGroup.MapGet(
            "/workspaces",
            async (IWorkspaceRegistry registry, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                WorkspaceInfo[] workspaces = await registry
                    .GetAllAsync(ctx.RequestAborted)
                    .ConfigureAwait(false);

                return Results.Ok(
                    ApiResponse<WorkspaceInfo[]>.FromResult(
                        Result<WorkspaceInfo[]>.Success(workspaces),
                        traceId));
            })
        .WithName("ListWorkspaces");

        apiGroup.MapGet(
            "/workspaces/{id}",
            async (string id, IWorkspaceRegistry registry, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                WorkspaceInfo? workspace = await registry
                    .GetAsync(id, ctx.RequestAborted)
                    .ConfigureAwait(false);

                return workspace is null
                    ? Results.Json(
                        ApiResponse<WorkspaceInfo>.FromResult(
                            Result<WorkspaceInfo>.Failure(new Error("Workspace.NotFound", "No workspace exists with that id.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseWorkspaceInfo,
                        statusCode: StatusCodes.Status404NotFound)
                    : Results.Ok(
                        ApiResponse<WorkspaceInfo>.FromResult(
                            Result<WorkspaceInfo>.Success(workspace),
                            traceId));
            })
        .WithName("GetWorkspace");

        apiGroup.MapPost(
            "/workspaces",
            async (IWorkspaceRegistry registry, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                CreateWorkspaceRequest? request;

                IResult? jsonError;

                (request, jsonError) = await ApiRequestJson.ReadAsync(
                    ctx,
                    ArcanumJsonContext.Default.CreateWorkspaceRequest,
                    static httpContext => ApiRequestJson.InvalidBodyResult<WorkspaceInfo>(
                        httpContext,
                        ApiRequestJson.MalformedJsonMessage,
                        ArcanumJsonContext.Default.ApiResponseWorkspaceInfo),
                    ctx.RequestAborted).ConfigureAwait(false);

                if (jsonError is not null)
                {
                    return jsonError;
                }

                if (request is null || string.IsNullOrWhiteSpace(request.Name))
                {
                    return Results.BadRequest(
                        ApiResponse<WorkspaceInfo>.FromResult(
                            Result<WorkspaceInfo>.Failure(
                                new Error("Workspace.NameEmpty", "Workspace name cannot be empty.")),
                            traceId));
                }

                Result<WorkspaceInfo> result = await registry
                    .RegisterAsync(request, ctx.RequestAborted)
                    .ConfigureAwait(false);

                if (result.IsSuccess)
                {
                    return Results.Created(
                        $"/api/workspaces/{result.Value.Id}",
                        ApiResponse<WorkspaceInfo>.FromResult(result, traceId));
                }

                if (string.Equals(result.Error.Code, "Workspace.PathNotAllowed", StringComparison.Ordinal))
                {
                    return Results.Json(
                        ApiResponse<WorkspaceInfo>.FromResult(
                            Result<WorkspaceInfo>.Failure(result.Error),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseWorkspaceInfo,
                        statusCode: StatusCodes.Status403Forbidden);
                }

                return Results.BadRequest(
                    ApiResponse<WorkspaceInfo>.FromResult(
                        Result<WorkspaceInfo>.Failure(result.Error),
                        traceId));
            })
        .WithName("RegisterWorkspace")
        .WithLargeRequestBody();

        apiGroup.MapPut(
            "/workspaces/{id}",
            async (string id, IWorkspaceRegistry registry, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                UpdateWorkspaceRequest? request;

                IResult? jsonError;

                (request, jsonError) = await ApiRequestJson.ReadAsync(
                    ctx,
                    ArcanumJsonContext.Default.UpdateWorkspaceRequest,
                    static httpContext => ApiRequestJson.InvalidBodyResult<WorkspaceInfo>(
                        httpContext,
                        ApiRequestJson.MalformedJsonMessage,
                        ArcanumJsonContext.Default.ApiResponseWorkspaceInfo),
                    ctx.RequestAborted).ConfigureAwait(false);

                if (jsonError is not null)
                {
                    return jsonError;
                }

                if (request is null)
                {
                    return Results.BadRequest(
                        ApiResponse<WorkspaceInfo>.FromResult(
                            Result<WorkspaceInfo>.Failure(
                                new Error("Validation.InvalidBody", "Request body is required.")),
                            traceId));
                }

                Result<WorkspaceInfo> result = await registry
                    .UpdateAsync(id, request, ctx.RequestAborted)
                    .ConfigureAwait(false);

                if (result.IsFailure && result.Error.Code == "Workspace.NotFound")
                {
                    return Results.Json(
                        ApiResponse<WorkspaceInfo>.FromResult(result, traceId),
                        ArcanumJsonContext.Default.ApiResponseWorkspaceInfo,
                        statusCode: StatusCodes.Status404NotFound);
                }

                return result.IsSuccess
                    ? Results.Ok(ApiResponse<WorkspaceInfo>.FromResult(result, traceId))
                    : Results.BadRequest(
                        ApiResponse<WorkspaceInfo>.FromResult(
                            Result<WorkspaceInfo>.Failure(result.Error),
                            traceId));
            })
        .WithName("UpdateWorkspace")
        .WithLargeRequestBody();

        apiGroup.MapDelete(
            "/workspaces/{id}",
            async (string id, IWorkspaceRegistry registry, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                Result<bool> result = await registry
                    .UnregisterAsync(id, ctx.RequestAborted)
                    .ConfigureAwait(false);

                if (result.IsFailure && result.Error.Code == "Workspace.NotFound")
                {
                    return Results.Json(
                        ApiResponse<bool>.FromResult(result, traceId),
                        ArcanumJsonContext.Default.ApiResponseBoolean,
                        statusCode: StatusCodes.Status404NotFound);
                }

                return result.IsSuccess
                    ? Results.NoContent()
                    : Results.BadRequest(
                        ApiResponse<bool>.FromResult(
                            Result<bool>.Failure(result.Error),
                            traceId));
            })
        .WithName("UnregisterWorkspace");

        apiGroup.MapGet(
            "/workspaces/{id}/files",
            async (
                string id,
                string? relativePath,
                bool recursive,
                string? searchPattern,
                IWorkspaceRegistry registry,
                IFileSystemBrowser browser,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                WorkspaceInfo? workspace = await registry
                    .GetAsync(id, ctx.RequestAborted)
                    .ConfigureAwait(false);

                if (workspace is null)
                {
                    return Results.Json(
                        ApiResponse<FileListResult>.FromResult(
                            Result<FileListResult>.Failure(new Error("Workspace.NotFound", "No workspace exists with that id.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseFileListResult,
                        statusCode: StatusCodes.Status404NotFound);
                }

                Result<FileListResult> result = await browser
                    .ListAsync(workspace, relativePath, recursive, searchPattern, ctx.RequestAborted)
                    .ConfigureAwait(false);

                return result.IsSuccess
                    ? Results.Ok(ApiResponse<FileListResult>.FromResult(result, traceId))
                    : Results.BadRequest(
                        ApiResponse<FileListResult>.FromResult(
                            Result<FileListResult>.Failure(result.Error),
                            traceId));
            })
        .WithName("ListWorkspaceFiles");

        apiGroup.MapGet(
            "/workspaces/{id}/files/info",
            async (
                string id,
                string? relativePath,
                IWorkspaceRegistry registry,
                IFileSystemBrowser browser,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                WorkspaceInfo? workspace = await registry
                    .GetAsync(id, ctx.RequestAborted)
                    .ConfigureAwait(false);

                if (workspace is null)
                {
                    return Results.Json(
                        ApiResponse<FileEntry>.FromResult(
                            Result<FileEntry>.Failure(new Error("Workspace.NotFound", "No workspace exists with that id.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseFileEntry,
                        statusCode: StatusCodes.Status404NotFound);
                }

                Result<FileEntry> result = await browser
                    .GetInfoAsync(workspace, relativePath, ctx.RequestAborted)
                    .ConfigureAwait(false);

                return result.IsSuccess
                    ? Results.Ok(ApiResponse<FileEntry>.FromResult(result, traceId))
                    : Results.BadRequest(
                        ApiResponse<FileEntry>.FromResult(
                            Result<FileEntry>.Failure(result.Error),
                            traceId));
            })
        .WithName("GetWorkspaceFileInfo");

        apiGroup.MapGet(
            "/workspaces/{id}/files/contents",
            async (
                string id,
                string relativePath,
                IWorkspaceRegistry registry,
                IFileSystemBrowser browser,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                WorkspaceInfo? workspace = await registry
                    .GetAsync(id, ctx.RequestAborted)
                    .ConfigureAwait(false);

                if (workspace is null)
                {
                    return Results.Json(
                        ApiResponse<FileReadResult>.FromResult(
                            Result<FileReadResult>.Failure(new Error("Workspace.NotFound", "No workspace exists with that id.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseFileReadResult,
                        statusCode: StatusCodes.Status404NotFound);
                }

                Result<FileReadResult> result = await browser
                    .ReadAsync(workspace, relativePath, ctx.RequestAborted)
                    .ConfigureAwait(false);

                return result.IsSuccess
                    ? Results.Ok(ApiResponse<FileReadResult>.FromResult(result, traceId))
                    : Results.BadRequest(
                        ApiResponse<FileReadResult>.FromResult(
                            Result<FileReadResult>.Failure(result.Error),
                            traceId));
            })
        .WithName("ReadWorkspaceFileContents");

        return apiGroup;
    }

}
