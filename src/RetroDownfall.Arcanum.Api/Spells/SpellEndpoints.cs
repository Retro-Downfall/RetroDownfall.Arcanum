using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Api.Spells;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells;

namespace RetroDownfall.Arcanum.Api.Spells;

internal static class SpellEndpoints
{

    public static RouteGroupBuilder MapSpellEndpoints(this RouteGroupBuilder apiGroup)
    {

        apiGroup.MapGet(
            "/spells",
            async (
                string? workspace,
                bool? paged,
                string? q,
                string? tag,
                string? tool,
                SpellSource? source,
                string? cursor,
                ISpellRepository repo,
                IArcanumSpellCatalog catalog,
                SpellWorkspaceResolver workspaceResolver,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                Result<string?> workspaceResult = workspaceResolver.Resolve(workspace);

                if (paged == true)
                {

                    IResult? pagedWorkspaceFailure = SpellApiResults.MapOptionalWorkspaceFailure<SpellCatalogPage>(
                        workspaceResult,
                        traceId,
                        ArcanumJsonContext.Default.ApiResponseSpellCatalogPage,
                        out string? pagedWorkspace);

                    if (pagedWorkspaceFailure is not null)
                    {

                        return pagedWorkspaceFailure;

                    }

                    Result<SpellCatalogPage> page = await catalog.PageAsync(
                        pagedWorkspace,
                        new SpellCatalogQuery(q, tag, tool, source, cursor),
                        ctx.RequestAborted).ConfigureAwait(false);

                    ApiResponse<SpellCatalogPage> envelope =
                        ApiResponse<SpellCatalogPage>.FromResult(page, traceId);

                    return Results.Json(
                        envelope,
                        ArcanumJsonContext.Default.ApiResponseSpellCatalogPage,
                        statusCode: page.IsSuccess
                            ? StatusCodes.Status200OK
                            : StatusCodes.Status400BadRequest);

                }

                IResult? workspaceFailure = SpellApiResults.MapOptionalWorkspaceFailure<SpellSummary[]>(
                    workspaceResult,
                    traceId,
                    ArcanumJsonContext.Default.ApiResponseSpellSummaryArray,
                    out string? resolvedWorkspace);

                if (workspaceFailure is not null)
                {
                    return workspaceFailure;
                }

                SpellSummary[] spells = await repo.ListAsync(resolvedWorkspace, ctx.RequestAborted).ConfigureAwait(false);

                return Results.Ok(ApiResponse<SpellSummary[]>.FromResult(Result<SpellSummary[]>.Success(spells), traceId));
            })
        .WithName("ListSpells");

        apiGroup.MapGet(
            "/spells/{name}",
            async (
                string name,
                string? workspace,
                ISpellRepository repo,
                SpellWorkspaceResolver workspaceResolver,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                Result<string?> workspaceResult = workspaceResolver.Resolve(workspace);

                IResult? workspaceFailure = SpellApiResults.MapOptionalWorkspaceFailure<SpellDetail>(
                    workspaceResult,
                    traceId,
                    ArcanumJsonContext.Default.ApiResponseSpellDetail,
                    out string? resolvedWorkspace);

                if (workspaceFailure is not null)
                {
                    return workspaceFailure;
                }

                SpellDetail? spell = await repo.GetAsync(name, resolvedWorkspace, ctx.RequestAborted).ConfigureAwait(false);

                if (spell is null)
                {
                    return Results.Json(
                        ApiResponse<SpellDetail>.FromResult(
                            Result<SpellDetail>.Failure(new Error(ErrorCodes.Spell.NotFound, "No spell exists with that name.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseSpellDetail,
                        statusCode: StatusCodes.Status404NotFound);
                }

                return Results.Ok(ApiResponse<SpellDetail>.FromResult(Result<SpellDetail>.Success(spell), traceId));
            })
        .WithName("GetSpell");

        apiGroup.MapPost(
            "/spells",
            async (
                string? workspace,
                ISpellRepository repo,
                SpellWorkspaceResolver workspaceResolver,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                CreateSpellRequest? request;

                IResult? jsonError;

                (request, jsonError) = await ApiRequestJson.ReadAsync(
                    ctx,
                    ArcanumJsonContext.Default.CreateSpellRequest,
                    static httpContext => ApiRequestJson.InvalidBodyResult(
                        httpContext,
                        ApiRequestJson.MalformedJsonMessage),
                    ctx.RequestAborted).ConfigureAwait(false);

                if (jsonError is not null)
                {
                    return jsonError;
                }

                if (request is null)
                {
                    return Results.BadRequest(
                        ApiResponse<bool>.FromResult(
                            Result<bool>.Failure(new Error(ErrorCodes.Validation.InvalidBody, "Request body is required.")),
                            traceId));
                }

                Result<string> workspaceRequired = workspaceResolver.ResolveRequired(workspace);

                IResult? workspaceFailure = SpellApiResults.MapRequiredWorkspaceFailure<bool>(
                    workspaceRequired,
                    traceId,
                    ArcanumJsonContext.Default.ApiResponseBoolean,
                    out string resolvedWorkspace);

                if (workspaceFailure is not null)
                {
                    return workspaceFailure;
                }

                Result result = await repo
                    .CreateAsync(resolvedWorkspace, request, ctx.RequestAborted)
                    .ConfigureAwait(false);

                return result.IsSuccess
                    ? Results.Ok(ApiResponse<bool>.FromResult(Result<bool>.Success(true), traceId))
                    : Results.BadRequest(ApiResponse<bool>.FromResult(Result<bool>.Failure(result.Error), traceId));
            })
        .WithName("CreateSpell")
        .WithLargeRequestBody();

        apiGroup.MapPut(
            "/spells/{name}",
            async (
                string name,
                string? workspace,
                ISpellRepository repo,
                SpellWorkspaceResolver workspaceResolver,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                UpdateSpellRequest? request;

                IResult? jsonError;

                (request, jsonError) = await ApiRequestJson.ReadAsync(
                    ctx,
                    ArcanumJsonContext.Default.UpdateSpellRequest,
                    static httpContext => ApiRequestJson.InvalidBodyResult(
                        httpContext,
                        ApiRequestJson.MalformedJsonMessage),
                    ctx.RequestAborted).ConfigureAwait(false);

                if (jsonError is not null)
                {
                    return jsonError;
                }

                if (request is null)
                {
                    return Results.BadRequest(
                        ApiResponse<bool>.FromResult(
                            Result<bool>.Failure(new Error(ErrorCodes.Validation.InvalidBody, "Request body is required.")),
                            traceId));
                }

                Result<string> workspaceRequired = workspaceResolver.ResolveRequired(workspace);

                IResult? workspaceFailure = SpellApiResults.MapRequiredWorkspaceFailure<bool>(
                    workspaceRequired,
                    traceId,
                    ArcanumJsonContext.Default.ApiResponseBoolean,
                    out string resolvedWorkspace);

                if (workspaceFailure is not null)
                {
                    return workspaceFailure;
                }

                Result result = await repo
                    .UpdateAsync(name, resolvedWorkspace, request, ctx.RequestAborted)
                    .ConfigureAwait(false);

                return result.IsSuccess
                    ? Results.Ok(ApiResponse<bool>.FromResult(Result<bool>.Success(true), traceId))
                    : SpellApiResults.MapFailure(
                        result.Error,
                        traceId,
                        ArcanumJsonContext.Default.ApiResponseBoolean);
            })
        .WithName("UpdateSpell")
        .WithLargeRequestBody();

        apiGroup.MapDelete(
            "/spells/{name}",
            async (
                string name,
                string? workspace,
                ISpellRepository repo,
                SpellWorkspaceResolver workspaceResolver,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                Result<string> workspaceRequired = workspaceResolver.ResolveRequired(workspace);

                IResult? workspaceFailure = SpellApiResults.MapRequiredWorkspaceFailure<bool>(
                    workspaceRequired,
                    traceId,
                    ArcanumJsonContext.Default.ApiResponseBoolean,
                    out string resolvedWorkspace);

                if (workspaceFailure is not null)
                {
                    return workspaceFailure;
                }

                Result result = await repo
                    .DeleteAsync(name, resolvedWorkspace, ctx.RequestAborted)
                    .ConfigureAwait(false);

                return result.IsSuccess
                    ? Results.NoContent()
                    : SpellApiResults.MapFailure(
                        result.Error,
                        traceId,
                        ArcanumJsonContext.Default.ApiResponseBoolean);
            })
        .WithName("DeleteSpell");

        return apiGroup;
    }

}
