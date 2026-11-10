using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Api.A2A;

/// <summary>Only explicitly mounted A2A and Card endpoints may authenticate peer credentials.</summary>
internal sealed record A2APeerRouteMetadata(bool Discovery);

internal sealed class A2APeerAuthenticator(IOptionsMonitor<ArcanumSettings> settings, GuildInboundPeerRegistry peers)
{
    private static readonly object PeerKey = new();

    private static readonly object GrantRequiredKey = new();

    internal static GuildInboundPeer? GetPeer(HttpContext context) => context.Items.TryGetValue(PeerKey, out object? peer)
        ? peer as GuildInboundPeer : null;

    internal static bool RequiresPeerGrant(HttpContext context) => context.Items.ContainsKey(GrantRequiredKey);

    internal static IDisposable? EnterScope(HttpContext context) => GetPeer(context) is { } peer
        ? context.RequestServices.GetRequiredService<GuildInboundRequestContext>().Enter(peer) : null;

    internal async ValueTask<bool> IsAuthorizedAsync(ApiKeyAuthenticator master, HttpContext context, A2APeerRouteMetadata route)
    {
        context.Items.Remove(PeerKey);

        context.Items.Remove(GrantRequiredKey);

        IHeaderDictionary headers = context.Request.Headers;

        bool key = headers.ContainsKey(ArcanumApiHeaders.ApiKey);

        bool bearer = headers.Authorization.Count != 0;

        bool capability = headers.ContainsKey(ArcanumApiHeaders.ProcessCapability);

        if (key && bearer || capability && (key || bearer)
            || key && headers[ArcanumApiHeaders.ApiKey].Count != 1 || bearer && headers.Authorization.Count != 1) return false;

        bool isOperator = await master.IsOperatorAuthorizedAsync(context).ConfigureAwait(false);

        if (!settings.CurrentValue.Features.Guild) return isOperator;

        if (isOperator)
        {
            if (route.Discovery) return true;

            context.Items[GrantRequiredKey] = true;

            return false;
        }

        if (capability) return false;

        StringValues values = key ? headers[ArcanumApiHeaders.ApiKey] : headers.Authorization;

        string? presentation = values.Count == 1 ? values[0] : null;

        if (!key)
        {
            if (presentation?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) != true) return false;

            presentation = presentation[7..];
        }

        if (presentation is null || presentation.Length > ArcanumSettingClamps.MaxApiKeyHeaderUtf16Chars(
            ArcanumRuntimeDefaults.SecurityMaxApiKeyHeaderUtf16Chars)) return false;

        GuildInboundPeer? peer = peers.Resolve(presentation);

        if (peer is null) return false;

        context.Items[PeerKey] = peer;

        return true;
    }
}
