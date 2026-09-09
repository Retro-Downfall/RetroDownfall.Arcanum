using System.Net;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;

namespace RetroDownfall.Arcanum.Api.Security;

/// <summary>
/// Requires both the raw transport peer and the post-forwarding effective peer to be local.
/// Short-lived process capabilities are local bearer credentials: a direct remote caller cannot
/// spoof loopback with a forwarded header, and a same-host proxy cannot relay one for a remote
/// caller. An absent, disposed, or non-IP peer fails closed.
/// </summary>
internal static class ArcanumTransportPeer
{
    internal static bool IsLoopback(HttpContext httpContext)
    {
        IConnectionSocketFeature? socketFeature =
            httpContext.Features.Get<IConnectionSocketFeature>();

        EndPoint? remoteEndPoint;
        IPAddress? effectiveRemoteAddress;

        try
        {
            remoteEndPoint = socketFeature is null
                ? httpContext.Features
                    .Get<IConnectionEndPointFeature>()?
                    .RemoteEndPoint
                : socketFeature.Socket.RemoteEndPoint;

            effectiveRemoteAddress = httpContext.Connection.RemoteIpAddress;
        }
        catch (Exception exception) when (
            exception is ObjectDisposedException
                or System.Net.Sockets.SocketException)
        {
            return false;
        }

        return IsLoopback(remoteEndPoint, effectiveRemoteAddress);
    }

    internal static bool IsLoopback(
        EndPoint? rawRemoteEndPoint,
        IPAddress? effectiveRemoteAddress) =>
        rawRemoteEndPoint is IPEndPoint peer
            && IsLoopbackAddress(peer.Address)
            && effectiveRemoteAddress is not null
            && IsLoopbackAddress(effectiveRemoteAddress);

    private static bool IsLoopbackAddress(IPAddress address) =>
        IPAddress.IsLoopback(address)
            || (address.IsIPv4MappedToIPv6
                && IPAddress.IsLoopback(address.MapToIPv4()));
}
