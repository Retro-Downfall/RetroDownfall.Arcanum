using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Api.Serialization;

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

    private static IResult MapFailure<T>(
        Error error,
        string traceId,
        JsonTypeInfo<ApiResponse<T>> responseTypeInfo)
    {
        ApiResponse<T> response = ApiResponse<T>.FromResult(Result<T>.Failure(error), traceId);

        if (string.Equals(error.Code, ErrorCodes.Spell.PathNotAllowed, StringComparison.Ordinal))
        {
            return Results.Json(response, responseTypeInfo, statusCode: StatusCodes.Status403Forbidden);
        }

        return Results.Json(response, responseTypeInfo, statusCode: StatusCodes.Status400BadRequest);
    }

}
