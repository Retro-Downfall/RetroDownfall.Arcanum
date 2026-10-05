using System.Net;
using System.Net.Sockets;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.TheForge.Ux.Markdown;

/// <summary>
/// Rejects hosts/addresses that would allow SSRF or local-network probing via markdown images.
/// Mirrors Arcanum <c>OutboundUrlGuard</c> posture: no auto-redirect at the handler layer and
/// connect-time IP pinning via <see cref="ConnectCallbackAsync"/>.
/// </summary>
/// <remarks>
/// The address policy is the guard's own (<see cref="OutboundUrlGuard.IsBlockedForUntrustedEgress"/>)
/// rather than a copy of its range list, so an IPv6 address carrying a private IPv4 destination, or a
/// range the guard learns to refuse later, is refused here too. The documentation ranges are refused on
/// top of it: an image URL naming one is never a real image host.
/// </remarks>
public static class MarkdownImageSsrfPolicy
{
    public const int MaxRedirectHops = 3;

    public static bool IsHostAllowed(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        string h = host.Trim().TrimEnd('.');

        if (h.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || h.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || h.Equals("metadata.google.internal", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (IPAddress.TryParse(h, out IPAddress? literal))
        {
            return IsPublicAddress(literal);
        }

        return true;
    }

    public static bool IsPublicAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (OutboundUrlGuard.IsBlockedForUntrustedEgress(address))
        {
            return false;
        }

        IPAddress candidate = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        if (candidate.AddressFamily != AddressFamily.InterNetwork)
        {
            return true;
        }

        byte[] bytes = candidate.GetAddressBytes();

        // TEST-NET-1, TEST-NET-2 and TEST-NET-3 (RFC 5737): documentation only, never an image host.
        return !((bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 2)
            || (bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100)
            || (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113));
    }

    public static async Task<bool> AreResolvedAddressesAllowedAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<IPAddress> addresses = await ResolvePublicAddressesAsync(host, cancellationToken)
                .ConfigureAwait(false);

            return addresses.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Connect-time IP pinning: resolve, re-validate, and connect only to public addresses so DNS
    /// rebinding between a preflight check and the TCP connect cannot reach private/metadata targets.
    /// </summary>
    public static async ValueTask<Stream> ConnectCallbackAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        string host = context.DnsEndPoint.Host;

        int port = context.DnsEndPoint.Port;

        IReadOnlyList<IPAddress> addresses = await ResolvePublicAddressesAsync(host, cancellationToken)
            .ConfigureAwait(false);

        if (addresses.Count == 0)
        {
            throw new HttpRequestException($"Remote host '{host}' is blocked (local/private/metadata).");
        }

        List<Exception>? connectErrors = null;

        foreach (IPAddress address in addresses)
        {
            if (!IsPublicAddress(address))
            {
                throw new HttpRequestException($"Remote host '{host}' is blocked (local/private/metadata).");
            }

            Socket socket = CreatePinnedSocket(address.AddressFamily);

            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken).ConfigureAwait(false);

                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException or TimeoutException)
            {
                connectErrors ??= [];

                connectErrors.Add(ex);

                socket.Dispose();
            }
        }

        throw new HttpRequestException(
            $"Could not connect to '{host}' on port {port}.",
            connectErrors is null ? null : new AggregateException(connectErrors));
    }

    /// <summary>
    /// The socket a pinned connect uses. <c>NoDelay</c> matches the outbound guard and
    /// <c>SocketsHttpHandler</c>'s own connect path: owning the socket must not put small request frames
    /// behind Nagle's algorithm.
    /// </summary>
    internal static Socket CreatePinnedSocket(AddressFamily addressFamily) =>
        new(addressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
        };

    private static async Task<IReadOnlyList<IPAddress>> ResolvePublicAddressesAsync(
        string host,
        CancellationToken cancellationToken)
    {
        if (!IsHostAllowed(host))
        {
            return Array.Empty<IPAddress>();
        }

        if (IPAddress.TryParse(host, out IPAddress? literal))
        {
            return IsPublicAddress(literal) ? [literal] : Array.Empty<IPAddress>();
        }

        IPAddress[] addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);

        if (addresses.Length == 0)
        {
            return Array.Empty<IPAddress>();
        }

        List<IPAddress> allowed = new(addresses.Length);

        foreach (IPAddress address in addresses)
        {
            if (!IsPublicAddress(address))
            {
                return Array.Empty<IPAddress>();
            }

            allowed.Add(address);
        }

        return allowed;
    }
}
