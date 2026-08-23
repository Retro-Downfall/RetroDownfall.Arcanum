using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Wards;

namespace RetroDownfall.Arcanum.Api.Security;

internal static class WardEndpoints
{

    public static RouteGroupBuilder MapWardEndpoints(this RouteGroupBuilder apiGroup)
    {
        apiGroup.MapGet(
            "/wards",
            (IWard ward, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                IReadOnlyList<ActiveWard> active = ward.GetActiveWards();

                WardDto[] dtos = active.Select(ToDto).ToArray();

                return Results.Ok(
                    ApiResponse<WardDto[]>.FromResult(
                        Result<WardDto[]>.Success(dtos),
                        traceId));
            })
        .WithName("ListWards");

        apiGroup.MapGet(
            "/wards/{id}",
            (string id, IWard ward, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                ActiveWard? active = ward.GetActiveWards()
                    .FirstOrDefault(w => string.Equals(w.WardId, id, StringComparison.Ordinal));

                if (active is null)
                {
                    return Results.Json(
                        ApiResponse<WardDto>.FromResult(
                            Result<WardDto>.Failure(new Error(ErrorCodes.Ward.NotFound, "The ward was not found or has already been resolved.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseWardDto,
                        statusCode: StatusCodes.Status404NotFound);
                }

                return Results.Ok(
                    ApiResponse<WardDto>.FromResult(
                        Result<WardDto>.Success(ToDto(active)),
                        traceId));
            })
        .WithName("GetWard");

        apiGroup.MapPost(
            "/wards/{id}",
            async (string id, ResolveWardRequest? request, IWard ward, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                if (request is null)
                {

                    return Results.BadRequest(
                        ApiResponse<WardResolutionDto>.FromResult(
                            Result<WardResolutionDto>.Failure(
                                new Error(ErrorCodes.Validation.InvalidBody, ApiRequestJson.DefaultInvalidBodyMessage)),
                            traceId));

                }

                ResolveStatus status = ward.Resolve(id, request.Allow, request.Reason);

                if (status == ResolveStatus.NotFound)
                {
                    return Results.Json(
                        ApiResponse<WardResolutionDto>.FromResult(
                            Result<WardResolutionDto>.Failure(
                                new Error(ErrorCodes.Ward.NotFound, "The ward was not found or has already been resolved.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseWardResolutionDto,
                        statusCode: StatusCodes.Status404NotFound);
                }

                if (status == ResolveStatus.AlreadyResolved)
                {
                    return Results.Json(
                        ApiResponse<WardResolutionDto>.FromResult(
                            Result<WardResolutionDto>.Failure(
                                new Error(ErrorCodes.Ward.AlreadyResolved, "The ward has already been resolved.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseWardResolutionDto,
                        statusCode: StatusCodes.Status409Conflict);
                }

                WardResolutionDto dto = new(
                    id,
                    request.Allow,
                    request.Reason,
                    DateTimeOffset.UtcNow,
                    WardResolutionOrigin.Human);

                return Results.Ok(
                    ApiResponse<WardResolutionDto>.FromResult(
                        Result<WardResolutionDto>.Success(dto),
                        traceId));
            })
        .WithName("ResolveWard");

        return apiGroup;
    }

    private static WardDto ToDto(ActiveWard ward)
    {
        return new WardDto(
            ward.WardId,
            ward.ToolName,
            RedactedArguments,
            ward.SessionId,
            ward.PlacedAt,
            ward.ExpiresAt);
    }

    private static readonly JsonElement? RedactedArguments =
        JsonDocument.Parse("{\"redacted\":true}").RootElement.Clone();

}
