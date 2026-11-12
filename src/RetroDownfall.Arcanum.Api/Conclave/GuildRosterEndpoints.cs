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
    private static RouteGroupBuilder MapGuildRosterEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/guild/agents", async (string? source, string? skill, string? cursor, int? pageSize,
            GuildNativeHost host, GuildNativeRoster roster, HttpContext context) =>
        {
            GuildAgentSourceKind? kind = source switch
            {
                "declaredGlobal" => GuildAgentSourceKind.DeclaredGlobal,

                "declaredWorkspace" => GuildAgentSourceKind.DeclaredWorkspace,

                "derived" => GuildAgentSourceKind.Derived,

                "remote" => GuildAgentSourceKind.Remote,

                _ => null,
            };

            if (source is not null && kind is null)
                return Reply(Result<GuildAgentCardPageDto>.Failure(Invalid), context, ArcanumJsonContext.Default.ApiResponseGuildAgentCardPageDto);

            Result<GuildCallerContext> caller = await host.CreateCallerAsync(Guid.NewGuid(), context.RequestAborted).ConfigureAwait(false);

            Result<GuildAgentCardPageDto> result = caller.IsFailure ? caller.Error
                : await roster.ListAsync(caller.Value, new(kind, skill, cursor, pageSize ?? 100), context.RequestAborted).ConfigureAwait(false);

            return Reply(result, context, ArcanumJsonContext.Default.ApiResponseGuildAgentCardPageDto);
        }).WithName("GuildListAgents");

        api.MapGet("/guild/agents/{agentId:guid}", async (Guid agentId, long? cardRevision,
            GuildNativeHost host, GuildNativeRoster roster, HttpContext context) =>
        {
            Result<GuildCallerContext> caller = await host.CreateCallerAsync(Guid.NewGuid(), context.RequestAborted).ConfigureAwait(false);

            Result<GuildAgentCardDto> result = caller.IsFailure ? caller.Error
                : await roster.GetAsync(caller.Value, agentId, cardRevision, context.RequestAborted).ConfigureAwait(false);

            return Reply(result, context, ArcanumJsonContext.Default.ApiResponseGuildAgentCardDto);
        }).WithName("GuildGetAgent");

        api.MapPost("/guild/agents/refresh", async (GuildRefreshAgentsRequest? request,
            GuildNativeHost host, GuildNativeRoster roster, HttpContext context) =>
        {
            if (request is null || request.IdempotencyKey == Guid.Empty || request.AgentId == Guid.Empty
                || request.AgentId is not null && request.Cursor is not null)
                return Reply(Result<GuildRefreshReceipt>.Failure(Invalid), context, ArcanumJsonContext.Default.ApiResponseGuildRefreshReceipt);

            Result<GuildCallerContext> caller = await host.CreateCallerAsync(request.IdempotencyKey, context.RequestAborted).ConfigureAwait(false);

            Result<GuildRefreshReceipt> result = caller.IsFailure ? caller.Error
                : await roster.RefreshAsync(caller.Value, request, context.RequestAborted).ConfigureAwait(false);

            return Reply(result, context, ArcanumJsonContext.Default.ApiResponseGuildRefreshReceipt);
        }).WithName("GuildRefreshAgents");

        return api;
    }
}
