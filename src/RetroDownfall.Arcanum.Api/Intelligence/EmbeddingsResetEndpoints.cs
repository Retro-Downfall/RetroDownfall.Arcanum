using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Weave;

namespace RetroDownfall.Arcanum.Api.Intelligence;

internal static class EmbeddingsResetEndpoints
{

    public static RouteGroupBuilder MapEmbeddingsResetEndpoints(this RouteGroupBuilder apiGroup)
    {

        apiGroup.MapPost(
                "/embeddings/reset",
                async (
                    string? scope,
                    bool? confirm,
                    EmbeddingsResetService resetService,
                    HttpContext ctx) =>
                {
                    string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                if (confirm is not true)
                {
                    return Results.Json(
                        ApiResponse<EmbeddingsResetResult>.FromResult(
                            Result<EmbeddingsResetResult>.Failure(
                                new Error(
                                    ErrorCodes.Validation.InvalidBody,
                                    "Resetting embeddings is destructive. Pass ?confirm=true to acknowledge.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseEmbeddingsResetResult,
                        statusCode: StatusCodes.Status400BadRequest);
                }

                EmbeddingsResetScope? parsedScope = ParseScope(scope);

                if (parsedScope is null)
                {
                    return Results.Json(
                        ApiResponse<EmbeddingsResetResult>.FromResult(
                            Result<EmbeddingsResetResult>.Failure(
                                new Error(
                                    ErrorCodes.Validation.InvalidBody,
                                    $"Unknown embeddings scope '{scope}'. Allowed: all, entry, workspace_file, saga.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseEmbeddingsResetResult,
                        statusCode: StatusCodes.Status400BadRequest);
                }

                EmbeddingsResetResult result = await resetService
                    .ResetAsync(parsedScope.Value, ctx.RequestAborted)
                    .ConfigureAwait(false);

                return Results.Ok(
                    ApiResponse<EmbeddingsResetResult>.FromResult(
                        Result<EmbeddingsResetResult>.Success(result),
                        traceId));
            })
            .WithName("EmbeddingsReset");

        return apiGroup;

    }

    internal static EmbeddingsResetScope? ParseScope(string? value)
    {

        if (string.IsNullOrEmpty(value))
        {

            return EmbeddingsResetScope.All;

        }

        return value.ToLowerInvariant() switch
        {

            "all" => EmbeddingsResetScope.All,
            "entry" => EmbeddingsResetScope.Entry,
            "workspacefile" => EmbeddingsResetScope.WorkspaceFile,
            "workspace_file" => EmbeddingsResetScope.WorkspaceFile,
            "saga" => EmbeddingsResetScope.Saga,
            _ => null,

        };

    }

}
