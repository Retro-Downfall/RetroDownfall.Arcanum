using System.Diagnostics;

using Microsoft.AspNetCore.Builder;

using Microsoft.AspNetCore.Http;

using Microsoft.AspNetCore.Routing;

using RetroDownfall.Arcanum.Api.Primitives;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Core.LongRest;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Core.Tower;

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

        apiGroup.MapPost(
            "/memory/saga/long-rest/sweeps",
            async (ILongRestSweepCoordinator sweeps, ICanonicalCampaignContextResolver resolver, ICanonicalCampaignRootEnricher enricher, HttpContext context) =>
            {
                (LongRestSweepRequest? request, IResult? bodyError) = await ApiRequestJson.ReadAsync(
                    context,
                    ArcanumJsonContext.Default.LongRestSweepRequest,
                    static ctx => ApiRequestJson.InvalidBodyResult(
                        ctx,
                        ApiRequestJson.MalformedJsonMessage,
                        ArcanumJsonContext.Default.ApiResponseLongRunningOperationDto),
                    context.RequestAborted).ConfigureAwait(false);

                if (bodyError is not null)
                {
                    return bodyError;
                }

                if (request is null)
                {
                    return ApiRequestJson.InvalidBodyResult(context, "A Long Rest sweep request is required.",
                        ArcanumJsonContext.Default.ApiResponseLongRunningOperationDto);
                }

                Result<LongRestSweepScope> validated = LongRestSweepScope.Create(request);

                if (validated.IsFailure)
                {
                    return SweepResponse(validated.Error, context);
                }

                Result<CanonicalCampaignContext> canonical = await resolver.ResolveAsync(
                    new(null, request.CampaignId, null), context.RequestAborted).ConfigureAwait(false);

                if (canonical.IsFailure)
                {
                    return SweepResponse(canonical.Error, context);
                }

                // A sweep request is a Long Rest request: the Campaign it names carries its registered root, which the fence compares.
                // With Long Rest off the context stays exactly what the resolver returned and the registry is not read.
                Result<CanonicalCampaignContext> enriched = await enricher.EnrichAsync(
                    canonical.Value, context.RequestAborted).ConfigureAwait(false);

                if (enriched.IsFailure)
                {
                    return SweepResponse(enriched.Error, context);
                }

                Result<LongRunningOperationDto> result = await sweeps.RequestAsync(
                    enriched.Value, context.RequestAborted).ConfigureAwait(false);

                return SweepResponse(result, context);
            })
            .WithName("RequestSagaLongRestSweep");

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

    private static IResult SweepResponse(Result<LongRunningOperationDto> result, HttpContext context) =>
        Results.Json(
            ApiResponse<LongRunningOperationDto>.FromResult(result, Activity.Current?.Id ?? context.TraceIdentifier),
            ArcanumJsonContext.Default.ApiResponseLongRunningOperationDto,
            statusCode: result.IsSuccess
                ? StatusCodes.Status200OK
                : ArcanumErrorMapper.ResolveStatusCode(result.Error.Code));

    private static IResult ReceiptResponse(Result<LongRestReceipt> result, HttpContext context) =>
        Results.Json(
            ApiResponse<LongRestReceipt>.FromResult(result, Activity.Current?.Id ?? context.TraceIdentifier),
            ArcanumJsonContext.Default.ApiResponseLongRestReceipt,
            statusCode: result.IsSuccess
                ? StatusCodes.Status200OK
                : ArcanumErrorMapper.ResolveStatusCode(result.Error.Code));
}
