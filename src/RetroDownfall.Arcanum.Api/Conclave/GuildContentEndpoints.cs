using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Api.Conclave;

internal static partial class GuildEndpoints
{
    private static RouteGroupBuilder MapGuildContentEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/guild/quests/{questId:guid}/messages", async (Guid questId, GuildSendOperatorMessageRequest? request,
            GuildNativeHost host, GuildNativeMessageService messages, HttpContext context) =>
        {
            if (request is null || request.IdempotencyKey == Guid.Empty || request.ExpectedRevision < 1)
                return Reply(Result<GuildMessageReceipt>.Failure(Invalid), context, ArcanumJsonContext.Default.ApiResponseGuildMessageReceipt);

            Result<GuildCallerContext> caller = await host.CreateCallerAsync(request.IdempotencyKey, context.RequestAborted).ConfigureAwait(false);

            Result<GuildMessageReceipt> result = caller.IsFailure ? caller.Error
                : await messages.SendAsync(caller.Value, questId, request, context.RequestAborted).ConfigureAwait(false);

            return Reply(result, context, ArcanumJsonContext.Default.ApiResponseGuildMessageReceipt,
                result.IsSuccess && result.Value.Receipt?.Replayed != true ? StatusCodes.Status202Accepted : StatusCodes.Status200OK);
        }).WithName("GuildSendOperatorMessage");

        api.MapPost("/guild/content/resolve", async (GuildResolveContentRequest? request, GuildNativeHost host,
            GuildLocalHostPolicy policy, GuildStore store, GuildQuestStore quests, HttpContext context) =>
        {
            if (request is null)
                return Reply(Result<GuildContentResolutionDto>.Failure(Invalid), context, ArcanumJsonContext.Default.ApiResponseGuildContentResolutionDto);

            Result<GuildCallerContext> caller = await host.CreateCallerAsync(Guid.NewGuid(), context.RequestAborted).ConfigureAwait(false);

            Result<GuildContentResolutionDto> result = caller.IsFailure ? caller.Error
                : await store.ResolveOperatorContentAsync(policy, quests, caller.Value, request.Prefix, context.RequestAborted).ConfigureAwait(false);

            return Reply(result, context, ArcanumJsonContext.Default.ApiResponseGuildContentResolutionDto);
        }).WithName("GuildResolveContent");

        api.MapGet("/guild/content/{contentId:guid}", ReadOperatorContentAsync).WithName("GuildGetContent");

        return api;
    }

    internal static Task<IResult> ReadOperatorContentAsync(Guid contentId, long? revision, GuildNativeHost host,
        GuildLocalHostPolicy policy, GuildStore store, GuildQuestStore quests, HttpContext context)
    {
        IResult result = revision is not > 0
            ? Reply(Result<GuildContentDto>.Failure(Invalid), context, ArcanumJsonContext.Default.ApiResponseGuildContentDto)
            : new OperatorContentResult(contentId, revision.Value, host, policy, store, quests);

        return Task.FromResult(result);
    }

    internal static Task<IResult> ReadQuestInputAsync(Guid questId, GuildNativeHost host, GuildQuestStore store, HttpContext context) =>
        Task.FromResult<IResult>(new OperatorQuestBodyResult(questId, host, store, true));

    internal static IResult ReadQuestResultContent(Guid questId, GuildNativeHost host, GuildQuestStore store) =>
        new OperatorQuestBodyResult(questId, host, store, false);

    private sealed class OperatorQuestBodyResult(Guid questId, GuildNativeHost host, GuildQuestStore store, bool question) : IResult
    {
        private int _executed;

        public async Task ExecuteAsync(HttpContext context)
        {
            if (Interlocked.Exchange(ref _executed, 1) != 0)
                throw new InvalidOperationException("A Guild content response can only be executed once.");

            context.Response.Headers.CacheControl = "no-store";

            Result<GuildCallerContext> caller = await host.CreateCallerAsync(Guid.NewGuid(), context.RequestAborted).ConfigureAwait(false);

            if (question)
            {
                Result<GuildQuestInputDto> input = caller.IsFailure ? caller.Error
                    : await store.ReadLocalQuestionAsync(caller.Value, questId, context.RequestAborted).ConfigureAwait(false);

                await Reply(input, context, ArcanumJsonContext.Default.ApiResponseGuildQuestInputDto).ExecuteAsync(context).ConfigureAwait(false);
            }
            else
            {
                Result<GuildLocalResultObservation> found = caller.IsFailure ? caller.Error
                    : await store.ReadLocalResultAsync(caller.Value, questId, true, context.RequestAborted).ConfigureAwait(false);

                Result<GuildContentDto> result = found.IsFailure ? found.Error
                    : found.Value.Receipt?.Content is { } reference && found.Value.Body is { } body
                        ? new GuildContentDto(reference, body) : MissingResult;

                await Reply(result, context, ArcanumJsonContext.Default.ApiResponseGuildContentDto).ExecuteAsync(context).ConfigureAwait(false);
            }
        }
    }

    // A queued HTTP result retains only the immutable lookup, never a disclosed body. Resolve
    // current owner, revision and sensitivity immediately before generated serialization.
    private sealed class OperatorContentResult(Guid contentId, long revision, GuildNativeHost host,
        GuildLocalHostPolicy policy, GuildStore store, GuildQuestStore quests) : IResult
    {
        private int _executed;

        public async Task ExecuteAsync(HttpContext context)
        {
            if (Interlocked.Exchange(ref _executed, 1) != 0)
                throw new InvalidOperationException("A Guild content response can only be executed once.");

            context.Response.Headers.CacheControl = "no-store";

            Result<GuildCallerContext> caller = await host.CreateCallerAsync(Guid.NewGuid(), context.RequestAborted).ConfigureAwait(false);

            Result<GuildContentDto> result = caller.IsFailure ? caller.Error
                : await store.ReadOperatorContentAsync(policy, quests, caller.Value, contentId, revision, context.RequestAborted).ConfigureAwait(false);

            await Reply(result, context, ArcanumJsonContext.Default.ApiResponseGuildContentDto).ExecuteAsync(context).ConfigureAwait(false);
        }
    }
}
