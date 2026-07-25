using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

/// <summary>
/// Blocks outbound <c>http</c>/<c>https</c> requests to loopback, RFC1918, and link-local targets (SSRF hardening).
/// </summary>
public static class OutboundUrlGuard
{

    public const string BlockedErrorCode = ErrorCodes.Security.BlockedOutboundUrl;

    private const string BlockedMessage =
        "Outbound URL targets a loopback, private, or link-local address and is not permitted.";

    /// <summary>
    /// DNS resolver used for hostname lookups. Production code uses the real DNS;
    /// tests can replace this with a fake to avoid network dependencies.
    /// </summary>
    public static IDnsResolver DnsResolver { get; set; } = new SystemDnsResolver();

    /// <summary>
    /// Validates an untrusted outbound URL (webhooks and similar operator-supplied egress targets).
    /// </summary>
    public static Task<Result> ValidateUntrustedUrlAsync(string? url, CancellationToken cancellationToken = default) =>
        ValidateUrlAsync(url, allowPrivateAndLoopback: false, cancellationToken);

    /// <summary>
    /// Validates a provider inference endpoint. Loopback and RFC1918 are allowed; link-local remains blocked.
    /// </summary>
    public static Task<Result> ValidateProviderEndpointAsync(string? url, CancellationToken cancellationToken = default) =>
        ValidateUrlAsync(url, allowPrivateAndLoopback: true, cancellationToken);

    /// <summary>
    /// Validates outbound URLs referenced by <see cref="ArcanumSettings"/> before persistence.
    /// </summary>
    public static async Task<Result> ValidateArcanumSettingsAsync(
        ArcanumSettings settings,
        CancellationToken cancellationToken = default)
    {

        CommLinkSettings? commLink = settings.CommLink;

        if (!string.IsNullOrWhiteSpace(commLink?.WebhookUrl))
        {

            Result webhook = await ValidateUntrustedUrlAsync(commLink.WebhookUrl, cancellationToken).ConfigureAwait(false);

            if (webhook.IsFailure)
            {
                return Result.Failure(new Error(BlockedErrorCode, $"CommLink.WebhookUrl: {webhook.Error.Message}"));
            }

        }

        ProviderSettings[] providers = settings.Providers ?? [];

        foreach (ProviderSettings provider in providers)
        {

            if (string.IsNullOrWhiteSpace(provider.Endpoint))
            {
                continue;
            }

            Result endpoint = await ValidateProviderEndpointAsync(provider.Endpoint, cancellationToken).ConfigureAwait(false);

            if (endpoint.IsFailure)
            {
                return Result.Failure(new Error(
                    BlockedErrorCode,
                    $"Provider '{provider.Name}' endpoint: {endpoint.Error.Message}"));
            }

        }

        return Result.Success();

    }

    /// <summary>
    /// Resolves a hostname and returns every address that passes the outbound guard (for DNS-rebind IP pinning).
    /// </summary>
    public static async Task<Result<IReadOnlyList<IPAddress>>> ResolveValidatedAddressesAsync(
        string host,
        bool allowPrivateAndLoopback,
        CancellationToken cancellationToken = default)
    {

        if (string.IsNullOrWhiteSpace(host))
        {
            return Result<IReadOnlyList<IPAddress>>.Failure(new Error(BlockedErrorCode, "URL must include a host."));
        }

        Result literalHost = ValidateLiteralHost(host, allowPrivateAndLoopback);

        if (literalHost.IsFailure)
        {
            return Result<IReadOnlyList<IPAddress>>.Failure(literalHost.Error);
        }

        IPAddress[] addresses;

        try
        {

            addresses = await DnsResolver.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);

        }
        catch (SocketException)
        {

            return Result<IReadOnlyList<IPAddress>>.Failure(
                new Error(BlockedErrorCode, $"Could not resolve host '{host}'."));

        }

        if (addresses.Length == 0)
        {

            return Result<IReadOnlyList<IPAddress>>.Failure(
                new Error(BlockedErrorCode, $"Could not resolve host '{host}'."));

        }

        List<IPAddress> validated = new(addresses.Length);

        foreach (IPAddress address in addresses)
        {

            if (IsBlockedAddress(address, allowPrivateAndLoopback))
            {
                return Result<IReadOnlyList<IPAddress>>.Failure(new Error(BlockedErrorCode, BlockedMessage));
            }

            validated.Add(address);

        }

        return Result<IReadOnlyList<IPAddress>>.Success(validated);

    }

    public const int MaxUntrustedRedirectHops = 8;

    /// <summary>
    /// Creates a <see cref="SocketsHttpHandler"/> for untrusted egress with DNS-rebind IP pinning.
    /// </summary>
    public static SocketsHttpHandler CreateUntrustedEgressHandler() =>
        CreateEgressHandler(allowPrivateAndLoopback: false);

    /// <summary>
    /// Creates a <see cref="SocketsHttpHandler"/> for provider inference and connectivity probes.
    /// Loopback and RFC1918 are allowed; link-local remains blocked; DNS is pinned at connect time.
    /// </summary>
    public static SocketsHttpHandler CreateProviderEgressHandler() =>
        CreateEgressHandler(allowPrivateAndLoopback: true);

    public static bool IsRedirectStatusCode(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.Moved
            or HttpStatusCode.Redirect
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    public static Result<string> ResolveRedirectLocation(Uri requestUri, string? locationHeader)
    {

        if (string.IsNullOrWhiteSpace(locationHeader))
        {
            return Result<string>.Failure(
                new Error(BlockedErrorCode, "Redirect response is missing a Location header."));
        }

        string trimmed = locationHeader.Trim();

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? absolute)
            && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            return Result<string>.Success(absolute.AbsoluteUri);
        }

        if (Uri.TryCreate(requestUri, trimmed, out Uri? relative))
        {
            return Result<string>.Success(relative.AbsoluteUri);
        }

        return Result<string>.Failure(
            new Error(BlockedErrorCode, "Redirect Location is not a valid absolute http or https URI."));

    }

    public static async Task<Result> ValidateUrlAsync(
        string? url,
        bool allowPrivateAndLoopback,
        CancellationToken cancellationToken = default)
    {

        if (string.IsNullOrWhiteSpace(url))
        {
            return Result.Failure(new Error(BlockedErrorCode, "URL is required."));
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri))
        {
            return Result.Failure(new Error(BlockedErrorCode, "URL must be an absolute http or https URI."));
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return Result.Failure(new Error(BlockedErrorCode, "URL must use the http or https scheme."));
        }

        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            return Result.Failure(new Error(BlockedErrorCode, "URL must include a host."));
        }

        Result<IReadOnlyList<IPAddress>> resolved = await ResolveValidatedAddressesAsync(
            uri.Host,
            allowPrivateAndLoopback,
            cancellationToken).ConfigureAwait(false);

        if (resolved.IsFailure)
        {
            return Result.Failure(resolved.Error);
        }

        return Result.Success();

    }

    private static SocketsHttpHandler CreateEgressHandler(bool allowPrivateAndLoopback) =>
        new()
        {

            AllowAutoRedirect = false,

            ConnectCallback = (context, cancellationToken) =>
                EgressConnectCallbackAsync(context, allowPrivateAndLoopback, cancellationToken),

        };

    private static async ValueTask<Stream> EgressConnectCallbackAsync(
        SocketsHttpConnectionContext context,
        bool allowPrivateAndLoopback,
        CancellationToken cancellationToken)
    {

        string host = context.DnsEndPoint.Host;

        int port = context.DnsEndPoint.Port;

        Result<IReadOnlyList<IPAddress>> resolved = await ResolveValidatedAddressesAsync(
            host,
            allowPrivateAndLoopback,
            cancellationToken).ConfigureAwait(false);

        if (resolved.IsFailure)
        {
            throw new HttpRequestException(resolved.Error.Message);
        }

        List<Exception>? connectErrors = null;

        foreach (IPAddress address in resolved.Value)
        {

            if (IsBlockedAddress(address, allowPrivateAndLoopback))
            {
                throw new HttpRequestException(BlockedMessage);
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

    private static Result ValidateLiteralHost(string host, bool allowPrivateAndLoopback)
    {

        if (!allowPrivateAndLoopback && IsBlockedHostname(host))
        {
            return Result.Failure(new Error(BlockedErrorCode, BlockedMessage));
        }

        if (IPAddress.TryParse(host, out IPAddress? literal))
        {

            if (IsBlockedAddress(literal, allowPrivateAndLoopback))
            {
                return Result.Failure(new Error(BlockedErrorCode, BlockedMessage));
            }

        }

        return Result.Success();

    }

    private static bool IsBlockedHostname(string host)
    {

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);

    }

    internal static bool IsBlockedAddress(IPAddress address, bool allowPrivateAndLoopback)
    {

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {

            byte[] bytes = address.GetAddressBytes();

            if (IsLinkLocalIPv4(bytes))
            {
                return true;
            }

            if (IsCarrierGradeNatIPv4(bytes))
            {
                return true;
            }

            if (allowPrivateAndLoopback)
            {
                return false;
            }

            if (IsLoopbackIPv4(bytes))
            {
                return true;
            }

            if (IsPrivateIPv4(bytes))
            {
                return true;
            }

            return bytes[0] == 0;

        }

        // IPAddress instances are IPv4 or IPv6; the IPv4 path returned above.
        if (address.Equals(IPAddress.IPv6Any))
        {
            return true;
        }

        if (address.IsIPv6LinkLocal)
        {
            return true;
        }

        if (allowPrivateAndLoopback)
        {
            return false;
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        byte[] ipv6Bytes = address.GetAddressBytes();

        if ((ipv6Bytes[0] & 0xFE) == 0xFC)
        {
            return true;
        }

        if (address.IsIPv6SiteLocal)
        {
            return true;
        }

        return false;

    }

    private static bool IsLoopbackIPv4(byte[] bytes) => bytes[0] == 127;

    private static bool IsLinkLocalIPv4(byte[] bytes) => bytes[0] == 169 && bytes[1] == 254;

    private static bool IsPrivateIPv4(byte[] bytes)
    {

        if (bytes[0] == 10)
        {
            return true;
        }

        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
        {
            return true;
        }

        return bytes[0] == 192 && bytes[1] == 168;

    }

    private static bool IsCarrierGradeNatIPv4(byte[] bytes) =>
        bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127;

}
