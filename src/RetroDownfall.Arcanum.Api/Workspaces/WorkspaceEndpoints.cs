using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Api.TheForge;
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
                            Result<WorkspaceInfo>.Failure(new Error(ErrorCodes.Workspace.NotFound, "No workspace exists with that id.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseWorkspaceInfo,
                        statusCode: ArcanumErrorMapper.ResolveStatusCode(ErrorCodes.Workspace.NotFound))
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
                                new Error(ErrorCodes.Workspace.NameEmpty, "Workspace name cannot be empty.")),
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

                if (string.Equals(result.Error.Code, ErrorCodes.Workspace.PathNotAllowed, StringComparison.Ordinal))
                {

                    return Results.Json(
                        ApiResponse<WorkspaceInfo>.FromResult(
                            Result<WorkspaceInfo>.Failure(result.Error),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseWorkspaceInfo,
                        statusCode: ArcanumErrorMapper.ResolveStatusCode(result.Error.Code));

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
                                new Error(ErrorCodes.Validation.InvalidBody, "Request body is required.")),
                            traceId));
                }

                Result<WorkspaceInfo> result = await registry
                    .UpdateAsync(id, request, ctx.RequestAborted)
                    .ConfigureAwait(false);

                if (result.IsFailure && result.Error.Code == ErrorCodes.Workspace.NotFound)
                {
                    return Results.Json(
                        ApiResponse<WorkspaceInfo>.FromResult(result, traceId),
                        ArcanumJsonContext.Default.ApiResponseWorkspaceInfo,
                        statusCode: ArcanumErrorMapper.ResolveStatusCode(ErrorCodes.Workspace.NotFound));
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

                if (result.IsFailure && result.Error.Code == ErrorCodes.Workspace.NotFound)
                {
                    return Results.Json(
                        ApiResponse<bool>.FromResult(result, traceId),
                        ArcanumJsonContext.Default.ApiResponseBoolean,
                        statusCode: ArcanumErrorMapper.ResolveStatusCode(ErrorCodes.Workspace.NotFound));
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
                bool? recursive,
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
                            Result<FileListResult>.Failure(new Error(ErrorCodes.Workspace.NotFound, "No workspace exists with that id.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseFileListResult,
                        statusCode: ArcanumErrorMapper.ResolveStatusCode(ErrorCodes.Workspace.NotFound));
                }

                Result<FileListResult> result = await browser
                    .ListAsync(workspace, relativePath, recursive ?? false, searchPattern, ctx.RequestAborted)
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
                            Result<FileEntry>.Failure(new Error(ErrorCodes.Workspace.NotFound, "No workspace exists with that id.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseFileEntry,
                        statusCode: ArcanumErrorMapper.ResolveStatusCode(ErrorCodes.Workspace.NotFound));
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
                            Result<FileReadResult>.Failure(new Error(ErrorCodes.Workspace.NotFound, "No workspace exists with that id.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseFileReadResult,
                        statusCode: ArcanumErrorMapper.ResolveStatusCode(ErrorCodes.Workspace.NotFound));
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

        apiGroup.MapPut(
            "/workspaces/{id}/files/contents",
            async (
                string id,
                string relativePath,
                IWorkspaceRegistry registry,
                IFileSystemWriter writer,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                // Read the body before any early return: the request body must be fully consumed
                // before writing a response, or the connection cannot safely complete the response
                // (mirrors the existing UpdateWorkspace PUT, which reads its body before checking
                // whether the target resource exists).
                FileWriteRequest? request;

                IResult? jsonError;

                (request, jsonError) = await ApiRequestJson.ReadAsync(
                    ctx,
                    ArcanumJsonContext.Default.FileWriteRequest,
                    static httpContext => ApiRequestJson.InvalidBodyResult<FileWriteResult>(
                        httpContext,
                        ApiRequestJson.MalformedJsonMessage,
                        ArcanumJsonContext.Default.ApiResponseFileWriteResult),
                    ctx.RequestAborted).ConfigureAwait(false);

                if (jsonError is not null)
                {
                    return jsonError;
                }

                if (request is null)
                {
                    return Results.BadRequest(
                        ApiResponse<FileWriteResult>.FromResult(
                            Result<FileWriteResult>.Failure(
                                new Error(ErrorCodes.Validation.InvalidBody, ApiRequestJson.DefaultInvalidBodyMessage)),
                            traceId));
                }

                WorkspaceInfo? workspace = await registry
                    .GetAsync(id, ctx.RequestAborted)
                    .ConfigureAwait(false);

                if (workspace is null)
                {
                    return Results.Json(
                        ApiResponse<FileWriteResult>.FromResult(
                            Result<FileWriteResult>.Failure(new Error(ErrorCodes.Workspace.NotFound, "No workspace exists with that id.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseFileWriteResult,
                        statusCode: ArcanumErrorMapper.ResolveStatusCode(ErrorCodes.Workspace.NotFound));
                }

                Result<FileWriteResult> result = await writer
                    .WriteFileAsync(workspace, relativePath, request.Content, ctx.RequestAborted)
                    .ConfigureAwait(false);

                return result.IsSuccess
                    ? Results.Ok(ApiResponse<FileWriteResult>.FromResult(result, traceId))
                    : Results.Json(
                        ApiResponse<FileWriteResult>.FromResult(
                            Result<FileWriteResult>.Failure(result.Error),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseFileWriteResult,
                        statusCode: ArcanumErrorMapper.ResolveStatusCode(result.Error.Code));
            })
        .WithName("WriteWorkspaceFileContents")
        .WithLargeRequestBody();

        apiGroup.MapPatch(
            "/workspaces/{id}/files/contents",
            async (
                string id,
                string relativePath,
                IWorkspaceRegistry registry,
                IFileSystemWriter writer,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                // Read the body before any early return — see the matching comment on the PUT endpoint above.
                TextBlockReplaceRequest? request;

                IResult? jsonError;

                (request, jsonError) = await ApiRequestJson.ReadAsync(
                    ctx,
                    ArcanumJsonContext.Default.TextBlockReplaceRequest,
                    static httpContext => ApiRequestJson.InvalidBodyResult<TextBlockReplaceResult>(
                        httpContext,
                        ApiRequestJson.MalformedJsonMessage,
                        ArcanumJsonContext.Default.ApiResponseTextBlockReplaceResult),
                    ctx.RequestAborted).ConfigureAwait(false);

                if (jsonError is not null)
                {
                    return jsonError;
                }

                if (request is null)
                {
                    return Results.BadRequest(
                        ApiResponse<TextBlockReplaceResult>.FromResult(
                            Result<TextBlockReplaceResult>.Failure(
                                new Error(ErrorCodes.Validation.InvalidBody, ApiRequestJson.DefaultInvalidBodyMessage)),
                            traceId));
                }

                WorkspaceInfo? workspace = await registry
                    .GetAsync(id, ctx.RequestAborted)
                    .ConfigureAwait(false);

                if (workspace is null)
                {
                    return Results.Json(
                        ApiResponse<TextBlockReplaceResult>.FromResult(
                            Result<TextBlockReplaceResult>.Failure(new Error(ErrorCodes.Workspace.NotFound, "No workspace exists with that id.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseTextBlockReplaceResult,
                        statusCode: ArcanumErrorMapper.ResolveStatusCode(ErrorCodes.Workspace.NotFound));
                }

                Result<TextBlockReplaceResult> result = await writer
                    .ReplaceTextBlockAsync(
                        workspace,
                        relativePath,
                        request.OldString,
                        request.NewString,
                        request.ExpectedReplacements,
                        ctx.RequestAborted)
                    .ConfigureAwait(false);

                return result.IsSuccess
                    ? Results.Ok(ApiResponse<TextBlockReplaceResult>.FromResult(result, traceId))
                    : Results.Json(
                        ApiResponse<TextBlockReplaceResult>.FromResult(
                            Result<TextBlockReplaceResult>.Failure(result.Error),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseTextBlockReplaceResult,
                        statusCode: ArcanumErrorMapper.ResolveStatusCode(result.Error.Code));
            })
        .WithName("ReplaceWorkspaceFileTextBlock")
        .WithLargeRequestBody();

        apiGroup.MapDelete(
            "/workspaces/{id}/files",
            async (
                string id,
                string relativePath,
                bool? recursive,
                IWorkspaceRegistry registry,
                IFileSystemWriter writer,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                WorkspaceInfo? workspace = await registry
                    .GetAsync(id, ctx.RequestAborted)
                    .ConfigureAwait(false);

                if (workspace is null)
                {
                    return Results.Json(
                        ApiResponse<FileDeleteResult>.FromResult(
                            Result<FileDeleteResult>.Failure(new Error(ErrorCodes.Workspace.NotFound, "No workspace exists with that id.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseFileDeleteResult,
                        statusCode: ArcanumErrorMapper.ResolveStatusCode(ErrorCodes.Workspace.NotFound));
                }

                Result<FileDeleteResult> result = await writer
                    .DeleteAsync(workspace, relativePath, recursive ?? false, ctx.RequestAborted)
                    .ConfigureAwait(false);

                return result.IsSuccess
                    ? Results.Ok(ApiResponse<FileDeleteResult>.FromResult(result, traceId))
                    : Results.Json(
                        ApiResponse<FileDeleteResult>.FromResult(
                            Result<FileDeleteResult>.Failure(result.Error),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseFileDeleteResult,
                        statusCode: ArcanumErrorMapper.ResolveStatusCode(result.Error.Code));
            })
        .WithName("DeleteWorkspaceFile");

        apiGroup.MapPost(
            "/workspaces/{id}/files/directory",
            async (
                string id,
                string relativePath,
                IWorkspaceRegistry registry,
                IFileSystemWriter writer,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                WorkspaceInfo? workspace = await registry
                    .GetAsync(id, ctx.RequestAborted)
                    .ConfigureAwait(false);

                if (workspace is null)
                {
                    return Results.Json(
                        ApiResponse<DirectoryCreateResult>.FromResult(
                            Result<DirectoryCreateResult>.Failure(new Error(ErrorCodes.Workspace.NotFound, "No workspace exists with that id.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseDirectoryCreateResult,
                        statusCode: ArcanumErrorMapper.ResolveStatusCode(ErrorCodes.Workspace.NotFound));
                }

                Result<DirectoryCreateResult> result = await writer
                    .CreateDirectoryAsync(workspace, relativePath, ctx.RequestAborted)
                    .ConfigureAwait(false);

                return result.IsSuccess
                    ? Results.Json(
                        ApiResponse<DirectoryCreateResult>.FromResult(result, traceId),
                        ArcanumJsonContext.Default.ApiResponseDirectoryCreateResult,
                        statusCode: StatusCodes.Status201Created)
                    : Results.Json(
                        ApiResponse<DirectoryCreateResult>.FromResult(
                            Result<DirectoryCreateResult>.Failure(result.Error),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseDirectoryCreateResult,
                        statusCode: ArcanumErrorMapper.ResolveStatusCode(result.Error.Code));
            })
        .WithName("CreateWorkspaceDirectory");

        return apiGroup;
    }

}
