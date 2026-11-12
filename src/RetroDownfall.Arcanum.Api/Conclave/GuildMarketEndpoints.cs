using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Api.Conclave;

internal static partial class GuildEndpoints
{
    private static RouteGroupBuilder MapGuildMarketEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/guild/cohorts/{cohortId:guid}/offers", async (Guid cohortId, PostWorkOfferRequest? request,
            GuildNativeHost host, GuildCohortStore cohorts, HttpContext context) =>
        {
            if (request is null || request.IdempotencyKey == Guid.Empty)
                return Reply(Result<WorkOfferDto>.Failure(Invalid), context, ArcanumJsonContext.Default.ApiResponseWorkOfferDto);
            Result<GuildCallerContext> caller = await host.CreateCallerAsync(request.IdempotencyKey, context.RequestAborted).ConfigureAwait(false);
            Result<WorkOfferDto> result = caller.IsFailure ? caller.Error : await cohorts.PostOfferAsync(caller.Value,
                MarketMutation(request.IdempotencyKey, request, GuildJsonContext.Default.PostWorkOfferRequest), cohortId, request, context.RequestAborted).ConfigureAwait(false);
            return Reply(result, context, ArcanumJsonContext.Default.ApiResponseWorkOfferDto,
                result.IsSuccess && result.Value.Receipt?.Replayed != true ? StatusCodes.Status201Created : StatusCodes.Status200OK);
        }).WithName("GuildPostWorkOffer");

        api.MapGet("/guild/cohorts/{cohortId:guid}/offers", async (Guid cohortId, string? state, string? cursor, int? pageSize,
            GuildNativeHost host, GuildCohortStore cohorts, HttpContext context) =>
        {
            GuildWorkOfferState? filter = state switch { null => null, "open" => GuildWorkOfferState.Open,
                "awarded" => GuildWorkOfferState.Awarded, "withdrawn" => GuildWorkOfferState.Withdrawn,
                "closed" => GuildWorkOfferState.Closed, _ => (GuildWorkOfferState)(-1) };
            Result<GuildCallerContext> caller = await host.CreateCallerAsync(Guid.NewGuid(), context.RequestAborted).ConfigureAwait(false);
            Result<WorkOfferPageDto> result = caller.IsFailure ? caller.Error : await cohorts.ListOffersAsync(caller.Value,
                cohortId, filter, new(cursor, pageSize ?? 100), context.RequestAborted).ConfigureAwait(false);
            return Reply(result, context, ArcanumJsonContext.Default.ApiResponseWorkOfferPageDto);
        }).WithName("GuildListWorkOffers");

        api.MapGet("/guild/offers/{offerId:guid}", async (Guid offerId, GuildNativeHost host, GuildCohortStore cohorts, HttpContext context) =>
        {
            Result<GuildCallerContext> caller = await host.CreateCallerAsync(Guid.NewGuid(), context.RequestAborted).ConfigureAwait(false);
            Result<WorkOfferDto> result = caller.IsFailure ? caller.Error : await cohorts.GetOfferAsync(caller.Value, offerId, context.RequestAborted).ConfigureAwait(false);
            return Reply(result, context, ArcanumJsonContext.Default.ApiResponseWorkOfferDto);
        }).WithName("GuildGetWorkOffer");

        api.MapPost("/guild/offers/{offerId:guid}/withdraw", async (Guid offerId, GuildRevisionRequest? request,
            GuildNativeHost host, GuildCohortStore cohorts, HttpContext context) =>
        {
            if (request is null || request.IdempotencyKey == Guid.Empty)
                return Reply(Result<WorkOfferDto>.Failure(Invalid), context, ArcanumJsonContext.Default.ApiResponseWorkOfferDto);
            Result<GuildCallerContext> caller = await host.CreateCallerAsync(request.IdempotencyKey, context.RequestAborted).ConfigureAwait(false);
            Result<WorkOfferDto> result = caller.IsFailure ? caller.Error : await cohorts.WithdrawOfferAsync(caller.Value,
                MarketMutation(request.IdempotencyKey, request, GuildJsonContext.Default.GuildRevisionRequest), offerId, request, context.RequestAborted).ConfigureAwait(false);
            return Reply(result, context, ArcanumJsonContext.Default.ApiResponseWorkOfferDto);
        }).WithName("GuildWithdrawWorkOffer");

        api.MapGet("/guild/offers/{offerId:guid}/bids", async (Guid offerId, string? cursor, int? pageSize,
            GuildNativeHost host, GuildCohortStore cohorts, HttpContext context) =>
        {
            Result<GuildCallerContext> caller = await host.CreateCallerAsync(Guid.NewGuid(), context.RequestAborted).ConfigureAwait(false);
            Result<WorkBidPageDto> result = caller.IsFailure ? caller.Error : await cohorts.ListBidsAsync(caller.Value,
                offerId, new(cursor, pageSize ?? 100), context.RequestAborted).ConfigureAwait(false);
            return Reply(result, context, ArcanumJsonContext.Default.ApiResponseWorkBidPageDto);
        }).WithName("GuildListWorkBids");

        api.MapPost("/guild/offers/{offerId:guid}/award", async (Guid offerId, AwardWorkRequest? request,
            GuildNativeHost host, GuildCohortStore cohorts, HttpContext context) =>
        {
            if (request is null || request.IdempotencyKey == Guid.Empty)
                return Reply(Result<WorkAwardDto>.Failure(Invalid), context, ArcanumJsonContext.Default.ApiResponseWorkAwardDto);
            Result<GuildCallerContext> caller = await host.CreateCallerAsync(request.IdempotencyKey, context.RequestAborted).ConfigureAwait(false);
            Result<WorkAwardDto> result = caller.IsFailure ? caller.Error : await cohorts.AwardAsync(caller.Value,
                MarketMutation(request.IdempotencyKey, request, GuildJsonContext.Default.AwardWorkRequest), offerId, request, context.RequestAborted).ConfigureAwait(false);
            return Reply(result, context, ArcanumJsonContext.Default.ApiResponseWorkAwardDto,
                result.IsSuccess && result.Value.Receipt?.Replayed != true ? StatusCodes.Status202Accepted : StatusCodes.Status200OK);
        }).WithName("GuildAwardWork");

        api.MapPost("/guild/cohorts/{cohortId:guid}/notes", async (Guid cohortId, AppendSharedNoteRequest? request,
            GuildNativeHost host, GuildCohortStore cohorts, HttpContext context) =>
        {
            if (request is null || request.IdempotencyKey == Guid.Empty)
                return Reply(Result<SharedNoteDto>.Failure(Invalid), context, ArcanumJsonContext.Default.ApiResponseSharedNoteDto);
            Result<GuildCallerContext> caller = await host.CreateCallerAsync(request.IdempotencyKey, context.RequestAborted).ConfigureAwait(false);
            Result<SharedNoteDto> result = caller.IsFailure ? caller.Error : await cohorts.AppendNoteAsync(caller.Value,
                MarketMutation(request.IdempotencyKey, request, GuildJsonContext.Default.AppendSharedNoteRequest), cohortId, request, context.RequestAborted).ConfigureAwait(false);
            return Reply(result, context, ArcanumJsonContext.Default.ApiResponseSharedNoteDto,
                result.IsSuccess && result.Value.Receipt?.Replayed != true ? StatusCodes.Status201Created : StatusCodes.Status200OK);
        }).WithName("GuildAppendSharedNote");

        api.MapGet("/guild/cohorts/{cohortId:guid}/notes", async (Guid cohortId, string? cursor, int? pageSize,
            GuildNativeHost host, GuildCohortStore cohorts, HttpContext context) =>
        {
            Result<GuildCallerContext> caller = await host.CreateCallerAsync(Guid.NewGuid(), context.RequestAborted).ConfigureAwait(false);
            Result<SharedNotePageDto> result = caller.IsFailure ? caller.Error : await cohorts.ReadNotesAsync(caller.Value,
                cohortId, new(cursor, pageSize ?? 100), context.RequestAborted).ConfigureAwait(false);
            return Reply(result, context, ArcanumJsonContext.Default.ApiResponseSharedNotePageDto);
        }).WithName("GuildReadSharedNotes");

        return api;
    }

    private static GuildMutationIdentity MarketMutation<T>(Guid key, T request, JsonTypeInfo<T> json) =>
        new(key, Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, json))));
}
