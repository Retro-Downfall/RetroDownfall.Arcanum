using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RetroDownfall.Arcanum.Api.Primitives;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Api.Conclave;

internal static class GuildSpendEndpoints
{
    internal static RouteGroupBuilder MapGuildSpendEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/guild/quests/{questId:guid}/spend/effects", async (Guid questId, string? cursor, int? pageSize,
            GuildNativeHost host, GuildSpendRefreshService service, HttpContext context) =>
        {
            if (questId == Guid.Empty || pageSize is < 1 or > 100)
                return Reply(Result<GuildSpendEffectPageDto>.Failure(Invalid), context, ArcanumJsonContext.Default.ApiResponseGuildSpendEffectPageDto);

            Result<GuildCallerContext> caller = await host.CreateCallerAsync(Guid.NewGuid(), context.RequestAborted).ConfigureAwait(false);

            Result<GuildSpendEffectPageDto> result = caller.IsFailure ? caller.Error
                : await service.ListEffectsAsync(caller.Value, questId, cursor, pageSize ?? 100, context.RequestAborted).ConfigureAwait(false);

            return Reply(result, context, ArcanumJsonContext.Default.ApiResponseGuildSpendEffectPageDto);
        }).WithName("GuildListSpendEffects");

        api.MapGet("/guild/quests/{questId:guid}/spend/effects/{effectId:guid}", async (Guid questId, Guid effectId,
            GuildNativeHost host, GuildSpendRefreshService service, HttpContext context) =>
        {
            if (questId == Guid.Empty || effectId == Guid.Empty)
                return Reply(Result<GuildSpendEffectDto>.Failure(Invalid), context, ArcanumJsonContext.Default.ApiResponseGuildSpendEffectDto);

            Result<GuildCallerContext> caller = await host.CreateCallerAsync(Guid.NewGuid(), context.RequestAborted).ConfigureAwait(false);

            Result<GuildSpendEffectDto> result = caller.IsFailure ? caller.Error
                : await service.ReadEffectAsync(caller.Value, questId, effectId, context.RequestAborted).ConfigureAwait(false);

            return Reply(result, context, ArcanumJsonContext.Default.ApiResponseGuildSpendEffectDto);
        }).WithName("GuildGetSpendEffect");

        api.MapPost("/guild/quests/{questId:guid}/spend/effects/{effectId:guid}/corrections", async (Guid questId, Guid effectId,
            GuildNativeHost host, GuildQuestStore store, HttpContext context) =>
        {
            GuildSpendCorrectionRequest? request;

            try
            {
                request = await JsonSerializer.DeserializeAsync(context.Request.Body, ArcanumJsonContext.Default.GuildSpendCorrectionRequest, context.RequestAborted).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                request = null;
            }

            if (questId == Guid.Empty || effectId == Guid.Empty || request is null || request.IdempotencyKey == Guid.Empty)
                return Reply(Result<GuildSpendCorrectionResultDto>.Failure(Invalid), context, ArcanumJsonContext.Default.ApiResponseGuildSpendCorrectionResultDto);

            Result<GuildCallerContext> caller = await host.CreateCallerAsync(request.IdempotencyKey, context.RequestAborted).ConfigureAwait(false);

            Result<GuildSpendCorrectionResultDto> result = caller.IsFailure ? caller.Error
                : await store.CorrectOperatorSpendAsync(caller.Value, questId, effectId, request, context.RequestAborted).ConfigureAwait(false);

            return Reply(result, context, ArcanumJsonContext.Default.ApiResponseGuildSpendCorrectionResultDto,
                result.IsSuccess && !result.Value.Replayed ? StatusCodes.Status201Created : StatusCodes.Status200OK);
        }).WithName("GuildCorrectSpendEffect");

        api.MapPost("/guild/quests/{questId:guid}/spend/effects/{effectId:guid}/refreshes", async (Guid questId, Guid effectId,
            GuildNativeHost host, GuildSpendRefreshService service, HttpContext context) =>
        {
            GuildSpendRefreshRequest? request;

            try
            {
                request = await JsonSerializer.DeserializeAsync(context.Request.Body, ArcanumJsonContext.Default.GuildSpendRefreshRequest, context.RequestAborted).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                request = null;
            }

            if (questId == Guid.Empty || effectId == Guid.Empty || request is null || request.IdempotencyKey == Guid.Empty)
                return Reply(Result<GuildSpendRefreshResultDto>.Failure(Invalid), context, ArcanumJsonContext.Default.ApiResponseGuildSpendRefreshResultDto);

            Result<GuildCallerContext> caller = await host.CreateCallerAsync(request.IdempotencyKey, context.RequestAborted).ConfigureAwait(false);

            Result<GuildSpendRefreshResult> refreshed = caller.IsFailure ? caller.Error
                : await service.RefreshAsync(caller.Value, questId, effectId, request.IdempotencyKey, context.RequestAborted).ConfigureAwait(false);

            Result<GuildSpendRefreshResultDto> result = refreshed.IsFailure ? refreshed.Error
                : new GuildSpendRefreshResultDto(refreshed.Value.OperationId, refreshed.Value.Receipt, refreshed.Value.Replayed,
                    refreshed.Value.InFlight, refreshed.Value.RequiresAttention,
                    refreshed.Value.ErrorCode ?? (refreshed.Value.Cancelled ? ErrorCodes.Guild.RefreshCancelled : refreshed.Value.RequiresAttention ? ErrorCodes.Guild.ReconciliationRequired : null),
                    refreshed.Value.Cancelled);

            return Reply(result, context, ArcanumJsonContext.Default.ApiResponseGuildSpendRefreshResultDto,
                result.IsSuccess && (result.Value.InFlight || result.Value.RequiresAttention) ? StatusCodes.Status202Accepted : StatusCodes.Status200OK);
        }).WithName("GuildRefreshSpendEffect");

        api.MapGet("/guild/quests/{questId:guid}/spend/effects/{effectId:guid}/receipts", async (Guid questId, Guid effectId,
            string? cursor, int? pageSize, GuildNativeHost host, GuildQuestStore store, HttpContext context) =>
        {
            if (questId == Guid.Empty || effectId == Guid.Empty || pageSize is < 1 or > 100)
                return Reply(Result<GuildSpendReceiptPageDto>.Failure(Invalid), context, ArcanumJsonContext.Default.ApiResponseGuildSpendReceiptPageDto);

            Result<GuildCallerContext> caller = await host.CreateCallerAsync(Guid.NewGuid(), context.RequestAborted).ConfigureAwait(false);

            Result<GuildSpendReceiptPageDto> result = caller.IsFailure ? caller.Error
                : await store.ListOperatorSpendReceiptsAsync(caller.Value, questId, effectId, cursor, pageSize ?? 100, context.RequestAborted).ConfigureAwait(false);

            return Reply(result, context, ArcanumJsonContext.Default.ApiResponseGuildSpendReceiptPageDto);
        }).WithName("GuildListSpendReceipts");

        api.MapGet("/guild/quests/{questId:guid}/spend/effects/{effectId:guid}/receipts/{receiptId:guid}", async (Guid questId, Guid effectId,
            Guid receiptId, GuildNativeHost host, GuildQuestStore store, HttpContext context) =>
        {
            if (questId == Guid.Empty || effectId == Guid.Empty || receiptId == Guid.Empty)
                return Reply(Result<GuildSpendReceiptDto>.Failure(Invalid), context, ArcanumJsonContext.Default.ApiResponseGuildSpendReceiptDto);

            Result<GuildCallerContext> caller = await host.CreateCallerAsync(Guid.NewGuid(), context.RequestAborted).ConfigureAwait(false);

            Result<GuildSpendReceiptDto> result = caller.IsFailure ? caller.Error
                : await store.ReadOperatorSpendReceiptAsync(caller.Value, questId, effectId, receiptId, context.RequestAborted).ConfigureAwait(false);

            return Reply(result, context, ArcanumJsonContext.Default.ApiResponseGuildSpendReceiptDto);
        }).WithName("GuildGetSpendReceipt");

        return api;
    }

    private static IResult Reply<T>(Result<T> result, HttpContext context, JsonTypeInfo<ApiResponse<T>> metadata,
        int status = StatusCodes.Status200OK) => Results.Json(ApiResponse<T>.FromResult(result,
            Activity.Current?.Id ?? context.TraceIdentifier), metadata,
            statusCode: result.IsSuccess ? status : ArcanumErrorMapper.ResolveStatusCodeDefaultBadRequest(result.Error.Code));

    private static Error Invalid => new(ErrorCodes.Guild.InvalidRequest, "An explicit valid spend request is required.");
}
