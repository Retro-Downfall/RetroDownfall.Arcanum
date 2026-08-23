using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Api.Primitives;
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
                                new Error(ErrorCodes.Validation.InvalidBody, ApiRequestJson.DefaultInvalidBodyMessage)),
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
        .WithName("RunProvingGroundsTrial")
        .WithLargeRequestBody();

        return apiGroup;
    }

    private static IResult MapTrialFailure(Result<TrialResult> result, string traceId)
    {

        return Results.Json(
            ApiResponse<TrialResult>.FromResult(result, traceId),
            ArcanumJsonContext.Default.ApiResponseTrialResult,
            statusCode: ArcanumErrorMapper.ResolveStatusCodeDefaultBadRequest(result.Error.Code));

    }

}
