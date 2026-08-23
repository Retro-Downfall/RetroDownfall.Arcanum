using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Repositories;

namespace RetroDownfall.Arcanum.Api.Tower;

internal static class LoreEndpoints
{

    public static RouteGroupBuilder MapLoreEndpoints(this RouteGroupBuilder apiGroup)
    {

        apiGroup.MapGet(
            "/lore",
            async (
                int? limit,
                int? offset,
                IGrimoireRepository grimoire,
                HttpContext httpContext,
                CancellationToken cancellationToken) =>
            {
                ListPageResult<LoreDto> page = await grimoire
                    .ListLoreAsync(limit, offset ?? 0, cancellationToken)
                    .ConfigureAwait(false);

                string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                Result<ListPageResult<LoreDto>> ok = Result<ListPageResult<LoreDto>>.Success(page);

                return Results.Ok(ApiResponse<ListPageResult<LoreDto>>.FromResult(ok, traceId));
            })
        .WithName("GetLore");

        apiGroup.MapGet(
            "/lore/{key}",
            async (string key, IGrimoireRepository grimoire, HttpContext httpContext, CancellationToken cancellationToken) =>
            {
                string normalizedKey = key.Trim();

                if (normalizedKey.Length == 0 || normalizedKey.Length > 256)
                {
                    string badTraceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                    Result<LoreDto> invalid = Result<LoreDto>.Failure(
                        new Error(ErrorCodes.Validation.InvalidKey, "Key must be between 1 and 256 characters."));

                    return Results.BadRequest(ApiResponse<LoreDto>.FromResult(invalid, badTraceId));
                }

                LoreDto? lore = await grimoire.GetLoreAsync(normalizedKey, cancellationToken).ConfigureAwait(false);

                if (lore is null)
                {
                    string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                    Result<LoreDto> notFound = Result<LoreDto>.Failure(
                        new Error(ErrorCodes.Grimoire.LoreNotFound, "No lore exists with that key."));

                    return Results.NotFound(ApiResponse<LoreDto>.FromResult(notFound, traceId));
                }

                string okTraceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                Result<LoreDto> ok = Result<LoreDto>.Success(lore);

                return Results.Ok(ApiResponse<LoreDto>.FromResult(ok, okTraceId));
            })
        .WithName("GetLoreByKey");

        apiGroup.MapPost(
            "/lore",
            async (HttpContext httpContext, IGrimoireRepository grimoire, CancellationToken cancellationToken) =>
            {
                UpsertLoreRequest? body;

                IResult? jsonError;

                (body, jsonError) = await ApiRequestJson.ReadAsync(
                    httpContext,
                    ArcanumJsonContext.Default.UpsertLoreRequest,
                    static ctx => ApiRequestJson.InvalidBodyResult<LoreDto>(
                        ctx,
                        ApiRequestJson.MalformedJsonMessage,
                        ArcanumJsonContext.Default.ApiResponseLoreDto),
                    cancellationToken).ConfigureAwait(false);

                string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                if (jsonError is not null)
                {
                    return jsonError;
                }

                if (body is null)
                {

                    Result<LoreDto> invalid = Result<LoreDto>.Failure(
                        new Error(ErrorCodes.Validation.InvalidBody, ApiRequestJson.DefaultInvalidBodyMessage));

                    return Results.BadRequest(ApiResponse<LoreDto>.FromResult(invalid, traceId));

                }

                if (string.IsNullOrWhiteSpace(body.Key)
                    || string.IsNullOrWhiteSpace(body.Value))
                {
                    Result<LoreDto> invalid = Result<LoreDto>.Failure(
                        new Error(ErrorCodes.Validation.InvalidLore, "Key and value are required."));

                    return Results.BadRequest(ApiResponse<LoreDto>.FromResult(invalid, traceId));
                }

                string trimmedKey = body.Key.Trim();

                if (trimmedKey.Length > 256)
                {
                    Result<LoreDto> invalid = Result<LoreDto>.Failure(
                        new Error(ErrorCodes.Validation.InvalidKey, "Key must not exceed 256 characters."));

                    return Results.BadRequest(ApiResponse<LoreDto>.FromResult(invalid, traceId));
                }

                LoreDto saved = await grimoire
                    .ScribeLoreAsync(trimmedKey, body.Value, cancellationToken)
                    .ConfigureAwait(false);

                Result<LoreDto> ok = Result<LoreDto>.Success(saved);

                return Results.Ok(ApiResponse<LoreDto>.FromResult(ok, traceId));
            })
        .WithName("UpsertLore")
        .WithLargeRequestBody();

        apiGroup.MapDelete(
            "/lore/{key}",
            async (string key, IGrimoireRepository grimoire, HttpContext httpContext, CancellationToken cancellationToken) =>
            {
                string normalizedKey = key.Trim();

                if (normalizedKey.Length == 0 || normalizedKey.Length > 256)
                {
                    string badTraceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                    Result<bool> invalid = Result<bool>.Failure(
                        new Error(ErrorCodes.Validation.InvalidKey, "Key must be between 1 and 256 characters."));

                    return Results.BadRequest(ApiResponse<bool>.FromResult(invalid, badTraceId));
                }

                bool removed = await grimoire.DeleteLoreAsync(normalizedKey, cancellationToken).ConfigureAwait(false);

                string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                if (!removed)
                {
                    Result<bool> notFound = Result<bool>.Failure(
                        new Error(ErrorCodes.Grimoire.LoreNotFound, "No lore exists with that key."));

                    return Results.NotFound(ApiResponse<bool>.FromResult(notFound, traceId));
                }

                Result<bool> ok = Result<bool>.Success(true);

                return Results.Ok(ApiResponse<bool>.FromResult(ok, traceId));
            })
        .WithName("DeleteLore");

        return apiGroup;
    }

}
