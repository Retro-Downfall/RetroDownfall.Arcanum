using System.Diagnostics;
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

internal static partial class GuildEndpoints
{
    internal static RouteGroupBuilder MapGuildEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/guild/status", async (GuildNativeHost host, HttpContext context) =>
            Reply(await host.GetStatusAsync(context.RequestAborted).ConfigureAwait(false), context,
                ArcanumJsonContext.Default.ApiResponseGuildStatusDto))
            .WithName("GuildGetStatus");

        api.MapPost("/guild/agents/remote/resolve", async (GuildResolveRemoteAgentRequest? request,
            GuildNativeHost host, GuildConfiguredRemoteResolver resolver, HttpContext context) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.Agent))
                return Reply(Result<GuildAgentCard>.Failure(Invalid), context, ArcanumJsonContext.Default.ApiResponseGuildAgentCard);

            Result<GuildCallerContext> caller = await host.CreateCallerAsync(Guid.NewGuid(), context.RequestAborted).ConfigureAwait(false);

            Result<GuildAgentCard> result = caller.IsFailure ? caller.Error
                : await resolver.ResolveAsync(caller.Value, request.Agent, context.RequestAborted).ConfigureAwait(false);

            return Reply(result, context, ArcanumJsonContext.Default.ApiResponseGuildAgentCard);
        }).WithName("GuildResolveRemoteAgent");

        api.MapPost("/guild/quests", async (GuildIssueRootRequest? request, GuildNativeHost host,
            IGuildQuestCoordinator coordinator, HttpContext context) =>
        {
            if (request is null || request.IdempotencyKey == Guid.Empty)
                return Reply(Result<GuildQuestDto>.Failure(Invalid), context, ArcanumJsonContext.Default.ApiResponseGuildQuestDto);

            Result<string> intent = GuildQuestIntent.Compute(request);

            if (intent.IsFailure)
                return Reply(Result<GuildQuestDto>.Failure(intent.Error), context, ArcanumJsonContext.Default.ApiResponseGuildQuestDto);

            Result ready = host.RequireExecutionReady();

            if (ready.IsFailure)
                return Reply(Result<GuildQuestDto>.Failure(ready.Error), context, ArcanumJsonContext.Default.ApiResponseGuildQuestDto);

            Result<GuildCallerContext> caller = await host.CreateCallerAsync(request.IdempotencyKey, context.RequestAborted).ConfigureAwait(false);

            Result<GuildQuestDto> result = caller.IsFailure ? caller.Error
                : await coordinator.IssueAsync(caller.Value, new(request.IdempotencyKey, intent.Value), request, context.RequestAborted).ConfigureAwait(false);

            return Reply(result, context, ArcanumJsonContext.Default.ApiResponseGuildQuestDto,
                result.IsSuccess && result.Value.Receipt?.Replayed != true ? StatusCodes.Status201Created : StatusCodes.Status200OK);
        }).WithName("GuildIssueRoot");

        api.MapGet("/guild/quests/{questId:guid}", async (Guid questId, GuildNativeHost host, GuildQuestStore store, HttpContext context) =>
        {
            Result<GuildCallerContext> caller = await host.CreateCallerAsync(Guid.NewGuid(), context.RequestAborted).ConfigureAwait(false);

            Result<GuildQuestStored> found = caller.IsFailure ? caller.Error
                : await store.GetAsync(caller.Value, questId, context.RequestAborted).ConfigureAwait(false);

            return Reply(found.IsFailure ? Result<GuildQuestDto>.Failure(found.Error) : found.Value.Quest with { Receipt = null },
                context, ArcanumJsonContext.Default.ApiResponseGuildQuestDto);
        }).WithName("GuildGetQuest");

        api.MapGet("/guild/quests/{questId:guid}/input", ReadQuestInputAsync).WithName("GuildGetQuestInput");

        api.MapPost("/guild/quests/{questId:guid}/cancel", async (Guid questId, GuildCancelQuestRequest? request,
            GuildNativeHost host, IGuildQuestCoordinator coordinator, HttpContext context) =>
        {
            if (request is null || request.IdempotencyKey == Guid.Empty || request.ReasonCode != GuildCancellationReason.OperatorRequested)
                return Reply(Result<GuildCancellationDto>.Failure(Invalid), context, ArcanumJsonContext.Default.ApiResponseGuildCancellationDto);

            Result<string> intent = GuildQuestIntent.Compute(questId, request);

            if (intent.IsFailure)
                return Reply(Result<GuildCancellationDto>.Failure(intent.Error), context, ArcanumJsonContext.Default.ApiResponseGuildCancellationDto);

            Result<GuildCallerContext> caller = await host.CreateCallerAsync(request.IdempotencyKey, context.RequestAborted).ConfigureAwait(false);

            Result<GuildCancellationDto> result = caller.IsFailure ? caller.Error
                : await coordinator.RequestCancellationAsync(caller.Value, new(request.IdempotencyKey, intent.Value), questId, request, context.RequestAborted).ConfigureAwait(false);

            return Reply(result, context, ArcanumJsonContext.Default.ApiResponseGuildCancellationDto,
                result.IsSuccess && result.Value.Receipt?.Replayed == true ? StatusCodes.Status200OK : StatusCodes.Status202Accepted);
        }).WithName("GuildCancelQuest");

        api.MapPost("/guild/quests/{questId:guid}/continue", async (Guid questId, GuildContinueRequest? request,
            GuildNativeHost host, IGuildQuestCoordinator coordinator, HttpContext context) =>
        {
            if (request is null || request.IdempotencyKey == Guid.Empty)
                return Reply(Result<GuildQuestDto>.Failure(Invalid), context, ArcanumJsonContext.Default.ApiResponseGuildQuestDto);

            Result<string> intent = GuildQuestIntent.Compute(questId, request);

            if (intent.IsFailure)
                return Reply(Result<GuildQuestDto>.Failure(intent.Error), context, ArcanumJsonContext.Default.ApiResponseGuildQuestDto);

            Result ready = host.RequireExecutionReady();

            if (ready.IsFailure)
                return Reply(Result<GuildQuestDto>.Failure(ready.Error), context, ArcanumJsonContext.Default.ApiResponseGuildQuestDto);

            Result<GuildCallerContext> caller = await host.CreateCallerAsync(request.IdempotencyKey, context.RequestAborted).ConfigureAwait(false);

            Result<GuildQuestDto> result = caller.IsFailure ? caller.Error
                : await coordinator.ContinueAsync(caller.Value, new(request.IdempotencyKey, intent.Value), questId, request, context.RequestAborted).ConfigureAwait(false);

            return Reply(result, context, ArcanumJsonContext.Default.ApiResponseGuildQuestDto,
                result.IsSuccess && result.Value.Receipt?.Replayed == true ? StatusCodes.Status200OK : StatusCodes.Status202Accepted);
        }).WithName("GuildContinueQuest");

        api.MapGet("/guild/quests/{questId:guid}/result", async (Guid questId, GuildNativeHost host, GuildQuestStore store, HttpContext context) =>
        {
            Result<GuildCallerContext> caller = await host.CreateCallerAsync(Guid.NewGuid(), context.RequestAborted).ConfigureAwait(false);

            Result<GuildLocalResultObservation> found = caller.IsFailure ? caller.Error
                : await store.ReadLocalResultAsync(caller.Value, questId, false, context.RequestAborted).ConfigureAwait(false);

            Result<GuildResultReceipt> result = found.IsFailure ? found.Error
                : found.Value.Receipt is { } receipt ? receipt : MissingResult;

            return Reply(result, context, ArcanumJsonContext.Default.ApiResponseGuildResultReceipt);
        }).WithName("GuildGetQuestResult");

        api.MapGet("/guild/quests/{questId:guid}/result/content", ReadQuestResultContent).WithName("GuildGetQuestResultContent");

        api.MapGuildOperatorEndpoints();

        api.MapGuildContentEndpoints();

        api.MapGuildStreamEndpoints();

        api.MapGuildRosterEndpoints();

        api.MapGuildLeaseEndpoints();

        return api;
    }

    private static IResult Reply<T>(Result<T> result, HttpContext context, JsonTypeInfo<ApiResponse<T>> metadata,
        int successStatus = StatusCodes.Status200OK) => Results.Json(
            ApiResponse<T>.FromResult(result, Activity.Current?.Id ?? context.TraceIdentifier), metadata,
            statusCode: result.IsSuccess ? successStatus : ArcanumErrorMapper.ResolveStatusCodeDefaultBadRequest(result.Error.Code));

    private static Error Invalid => new(ErrorCodes.Guild.InvalidRequest, "A valid explicit Guild mutation is required.");

    private static Error MissingResult => new(ErrorCodes.Guild.NotFound, "The Quest has no committed result body or receipt.");
}
