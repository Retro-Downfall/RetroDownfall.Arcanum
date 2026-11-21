using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Primitives;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Api.A2A;

internal static class GuildPortalPeerEndpoints
{
    private static readonly ArcanumJsonContext Wire = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        AllowDuplicateProperties = false,
    });

    internal static void MapGuildPortalPeerEndpoints(this RouteGroupBuilder api, string a2aPath)
    {
        api.MapPost(a2aPath + GuildPortalMessageProtocol.RouteSuffix, AcceptAsync)
            .WithName("A2AAcceptGuildPortalMessage")
            .WithMetadata(new A2APeerRouteMetadata(false), new RequestSizeLimitAttribute(GuildPortalMessageProtocol.MaximumRequestBytes));

        api.MapPost(a2aPath + GuildPortalMessageProtocol.ReceiptRouteSuffix, ReadReceiptAsync)
            .WithName("A2AReadGuildPortalMessageReceipt")
            .WithMetadata(new A2APeerRouteMetadata(false), new RequestSizeLimitAttribute(GuildPortalMessageProtocol.MaximumReceiptQueryBytes));
    }

    private static async Task<IResult> AcceptAsync(HttpContext context, GuildInboundHost host, IOptionsMonitor<ArcanumSettings> settings)
    {
        if (CurrentPeer(context, settings) is not { } peer) return Reply(context, Missing);

        if (context.Request.ContentLength > GuildPortalMessageProtocol.MaximumRequestBytes) return Reply(context, TooLarge);

        var read = await ApiRequestJson.ReadAsync(context, Wire.GuildPortalMessageRequest, InvalidBody, context.RequestAborted).ConfigureAwait(false);

        if (read.Error is not null) return read.Error;

        if (read.Body is not { } message) return Reply(context, Invalid);

        return Reply(context, await host.AcceptPortalMessageAsync(peer, message, context.RequestAborted).ConfigureAwait(false));
    }

    private static async Task<IResult> ReadReceiptAsync(HttpContext context, GuildInboundHost host, IOptionsMonitor<ArcanumSettings> settings)
    {
        if (CurrentPeer(context, settings) is not { } peer) return Reply(context, Missing);

        if (context.Request.ContentLength > GuildPortalMessageProtocol.MaximumReceiptQueryBytes) return Reply(context, TooLarge);

        var read = await ApiRequestJson.ReadAsync(context, Wire.GuildPortalMessageReceiptQuery, InvalidBody, context.RequestAborted).ConfigureAwait(false);

        if (read.Error is not null) return read.Error;

        if (read.Body is not { } query) return Reply(context, Invalid);

        return Reply(context, await host.ReadPortalMessageReceiptAsync(peer, query, context.RequestAborted).ConfigureAwait(false));
    }

    private static GuildInboundPeer? CurrentPeer(HttpContext context, IOptionsMonitor<ArcanumSettings> settings)
    {
        ArcanumSettings current = settings.CurrentValue;

        return current.Features.Guild && current.ResolveA2A().ServerEnabled ? A2APeerAuthenticator.GetPeer(context) : null;
    }

    private static IResult InvalidBody(HttpContext context, Error _) => Reply(context, Invalid);

    private static IResult Reply(HttpContext context, Result<GuildPortalMessageDeliveryReceipt> result)
    {
        context.Response.Headers.CacheControl = "no-store";

        return Results.Json(ApiResponse<GuildPortalMessageDeliveryReceipt>.FromResult(result, Activity.Current?.Id ?? context.TraceIdentifier),
            ArcanumJsonContext.Default.ApiResponseGuildPortalMessageDeliveryReceipt,
            statusCode: result.IsSuccess ? StatusCodes.Status200OK : ArcanumErrorMapper.ResolveStatusCodeDefaultBadRequest(result.Error.Code));
    }

    private static Error Missing => new(ErrorCodes.Guild.NotFound, "The peer task or receipt is unavailable.");

    private static Error Invalid => new(ErrorCodes.Guild.InvalidRequest, "The peer message payload is invalid.");

    private static Error TooLarge => new(ErrorCodes.Guild.PayloadTooLarge, "The peer message payload exceeds the transport limit.");
}
