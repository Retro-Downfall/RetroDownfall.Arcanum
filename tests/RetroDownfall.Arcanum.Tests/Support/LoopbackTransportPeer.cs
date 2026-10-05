using System.Net;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// Gives a TestServer request the two facts a local caller has: a loopback raw transport peer and a loopback
/// effective peer.
/// </summary>
/// <remarks>
/// <c>ArcanumTransportPeer.IsLoopback</c> reads the raw endpoint of the connection and the effective address
/// the forwarded-headers middleware may have rewritten, and requires both to be local. A TestServer
/// connection carries only the second, so a test that stands in for a local caller sets both here.
/// </remarks>
internal static class LoopbackTransportPeer
{
    public static void Apply(HttpContext context) => Apply(context, IPAddress.Loopback, IPAddress.Loopback);

    /// <param name="rawPeer">The address of the socket the request arrived on.</param>
    /// <param name="effectivePeer">The address after any forwarded-headers rewriting.</param>
    public static void Apply(HttpContext context, IPAddress rawPeer, IPAddress effectivePeer)
    {
        context.Connection.RemoteIpAddress = effectivePeer;

        context.Features.Set<IConnectionEndPointFeature>(
            new Feature(new IPEndPoint(rawPeer, 43123)));
    }

    private sealed class Feature(EndPoint remoteEndPoint) : IConnectionEndPointFeature
    {
        public EndPoint? LocalEndPoint { get; set; }

        public EndPoint? RemoteEndPoint { get; set; } = remoteEndPoint;
    }
}
