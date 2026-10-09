using System.Diagnostics;

using Microsoft.AspNetCore.Builder;

using Microsoft.AspNetCore.Http;

using Microsoft.AspNetCore.Routing;

using RetroDownfall.Arcanum.Api.Primitives;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Core.LongRest;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Tower;

/// <summary>Authenticated exact Saga declarations and their immutable content-free evidence.</summary>
internal static class LongRestEndpoints
{
    public static RouteGroupBuilder MapLongRestEndpoints(this RouteGroupBuilder apiGroup)
    {
        apiGroup.MapPost(
            "/memory/saga/long-rest",
            async (ILongRestService longRest, HttpContext context) =>
            {
                (LongRestRequest? request, IResult? bodyError) = await ApiRequestJson.ReadAsync(
                    context,
                    ArcanumJsonContext.Default.LongRestRequest,
                    static ctx => ApiRequestJson.InvalidBodyResult(
                        ctx,
                        ApiRequestJson.MalformedJsonMessage,
                        ArcanumJsonContext.Default.ApiResponseLongRestReceipt),
                    context.RequestAborted).ConfigureAwait(false);

                if (bodyError is not null)
                {
                    return bodyError;
                }

                if (request is null)
                {
                    return ApiRequestJson.InvalidBodyResult(
                        context,
                        "A Long Rest declaration is required.",
                        ArcanumJsonContext.Default.ApiResponseLongRestReceipt);
                }

                Result<LongRestReceipt> result = await longRest
                    .ApplyAsync(request, context.RequestAborted)
                    .ConfigureAwait(false);

                return ReceiptResponse(result, context);
            })
            .WithName("ApplySagaLongRest");

        apiGroup.MapGet(
            "/memory/saga/long-rest/receipts/{receiptId}",
            async (string receiptId, ILongRestService longRest, HttpContext context) =>
            {
                Result<LongRestReceipt> result = await longRest
                    .GetReceiptAsync(receiptId, context.RequestAborted)
                    .ConfigureAwait(false);

                return ReceiptResponse(result, context);
            })
            .WithName("GetSagaLongRestReceipt");

        return apiGroup;
    }

    private static IResult ReceiptResponse(Result<LongRestReceipt> result, HttpContext context) =>
        Results.Json(
            ApiResponse<LongRestReceipt>.FromResult(result, Activity.Current?.Id ?? context.TraceIdentifier),
            ArcanumJsonContext.Default.ApiResponseLongRestReceipt,
            statusCode: result.IsSuccess
                ? StatusCodes.Status200OK
                : ArcanumErrorMapper.ResolveStatusCode(result.Error.Code));
}
