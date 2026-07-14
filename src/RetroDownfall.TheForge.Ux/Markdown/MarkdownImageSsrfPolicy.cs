using System.Net;
using System.Net.Sockets;

namespace RetroDownfall.TheForge.Ux.Markdown;

/// <summary>
/// Rejects hosts/addresses that would allow SSRF or local-network probing via markdown images.
/// Mirrors Arcanum <c>OutboundUrlGuard</c> posture: no auto-redirect at the handler layer and
/// connect-time IP pinning via <see cref="ConnectCallbackAsync"/>.
/// </summary>
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

        if (IPAddress.IsLoopback(address))
        {

            return false;

        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {

            byte[] bytes = address.GetAddressBytes();

            // 0.0.0.0/8, 10/8, 127/8 already loopback, 169.254/16, 172.16/12, 192.168/16, 100.64/10
            if (bytes[0] == 0
                || bytes[0] == 10
                || (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127)
                || (bytes[0] == 169 && bytes[1] == 254)
                || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0)
                || (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 2)
                || (bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19))
                || (bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100)
                || (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113)
                || bytes[0] >= 224)
            {

                return false;

            }

            return true;

        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {

            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
            {

                return false;

            }

            byte[] bytes = address.GetAddressBytes();

            // Unique local fc00::/7
            if ((bytes[0] & 0xfe) == 0xfc)
            {

                return false;

            }

            // IPv4-mapped
            if (address.IsIPv4MappedToIPv6)
            {

                return IsPublicAddress(address.MapToIPv4());

            }

            return !address.Equals(IPAddress.IPv6Any);

        }

        return false;

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

            Socket socket = new(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

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
