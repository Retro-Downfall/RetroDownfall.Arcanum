using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Api.Primitives;

namespace RetroDownfall.Arcanum.Api.Spells;

internal static class SpellApiResults
{

    public static IResult? MapOptionalWorkspaceFailure<T>(
        Result<string?> workspaceResult,
        string traceId,
        JsonTypeInfo<ApiResponse<T>> responseTypeInfo,
        out string? workspace)
    {
        workspace = null;

        if (!workspaceResult.IsFailure)
        {
            workspace = workspaceResult.Value;

            return null;
        }

        return MapFailure(workspaceResult.Error, traceId, responseTypeInfo);
    }

    public static IResult? MapRequiredWorkspaceFailure<T>(
        Result<string> workspaceResult,
        string traceId,
        JsonTypeInfo<ApiResponse<T>> responseTypeInfo,
        out string workspace)
    {
        workspace = string.Empty;

        if (!workspaceResult.IsFailure)
        {
            workspace = workspaceResult.Value;

            return null;
        }

        return MapFailure(workspaceResult.Error, traceId, responseTypeInfo);
    }

    public static IResult MapFailure<T>(
        Error error,
        string traceId,
        JsonTypeInfo<ApiResponse<T>> responseTypeInfo)
    {
        ApiResponse<T> response = ApiResponse<T>.FromResult(Result<T>.Failure(error), traceId);

        return Results.Json(
            response,
            responseTypeInfo,
            statusCode: ArcanumErrorMapper.ResolveStatusCodeDefaultBadRequest(error.Code));

    }

}
