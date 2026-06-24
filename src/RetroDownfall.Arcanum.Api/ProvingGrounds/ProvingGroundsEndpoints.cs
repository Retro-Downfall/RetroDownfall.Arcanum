using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.ProvingGrounds;

namespace RetroDownfall.Arcanum.Api.ProvingGrounds;

internal static class ProvingGroundsEndpoints
{

    public static RouteGroupBuilder MapProvingGroundsEndpoints(this RouteGroupBuilder apiGroup)
    {
        apiGroup.MapPost(
            "/proving-grounds/trials/run",
            async (Trial? trial, ProvingGroundsRunner runner, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                if (trial is null)
                {

                    return Results.BadRequest(
                        ApiResponse<TrialResult>.FromResult(
                            Result<TrialResult>.Failure(
                                new Error("Validation.InvalidBody", ApiRequestJson.DefaultInvalidBodyMessage)),
                            traceId));

                }

                Result<TrialResult> result = await runner
                    .RunAsync(trial, ctx.RequestAborted)
                    .ConfigureAwait(false);

                if (result.IsSuccess)
                {
                    return Results.Ok(ApiResponse<TrialResult>.FromResult(result, traceId));
                }

                return MapTrialFailure(result, traceId);
            })
        .WithName("RunProvingGroundsTrial");

        return apiGroup;
    }

    private static IResult MapTrialFailure(Result<TrialResult> result, string traceId)
    {
        ApiResponse<TrialResult> response = ApiResponse<TrialResult>.FromResult(result, traceId);

        return result.Error.Code switch
        {
            "ProvingGrounds.SpellNotFound" or "ProvingGrounds.PromptNotFound" => Results.Json(
                response,
                ArcanumJsonContext.Default.ApiResponseTrialResult,
                statusCode: StatusCodes.Status404NotFound),
            "ProvingGrounds.InferenceFailed" => Results.Json(
                response,
                ArcanumJsonContext.Default.ApiResponseTrialResult,
                statusCode: StatusCodes.Status500InternalServerError),
            _ => Results.Json(
                response,
                ArcanumJsonContext.Default.ApiResponseTrialResult,
                statusCode: StatusCodes.Status400BadRequest),
        };
    }

}
