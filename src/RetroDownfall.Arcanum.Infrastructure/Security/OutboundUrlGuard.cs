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
    private static readonly IDnsResolver DefaultDnsResolver = new SystemDnsResolver();

    public const string BlockedErrorCode = ErrorCodes.Security.BlockedOutboundUrl;

    private const string BlockedMessage =
        "Outbound URL targets a loopback, private, or link-local address and is not permitted.";

    /// <summary>
    /// Test-only seam that substitutes the pinned address list between validation and connect, so tests
    /// can prove the post-resolution re-check still fails closed on a poisoned or empty list. It grants
    /// no bypass: every address it yields is re-validated by <see cref="IsBlockedAddress"/> before a
    /// socket is opened. Production code leaves this at the default (<see langword="null"/>).
    /// Set via <see cref="SetPinnedAddressRewriterForTests"/>.
    /// </summary>
    private static Func<IReadOnlyList<IPAddress>, IReadOnlyList<IPAddress>>? _pinnedAddressRewriterForTests;

    /// <summary>
    /// Supplies a test-only rewriter for the pinned address list. Pass <see langword="null"/> to restore
    /// the default.
    /// </summary>
    internal static void SetPinnedAddressRewriterForTests(
        Func<IReadOnlyList<IPAddress>, IReadOnlyList<IPAddress>>? rewriter)
    {
        _pinnedAddressRewriterForTests = rewriter;
    }

    /// <summary>
    /// Restores all test seams to production defaults. Call from test teardown to avoid cross-test leakage.
    /// </summary>
    internal static void ResetTestSeams()
    {
        _pinnedAddressRewriterForTests = null;
    }

    /// <summary>
    /// Validates an untrusted outbound URL (webhooks and similar operator-supplied egress targets).
    /// </summary>
    public static Task<Result> ValidateUntrustedUrlAsync(string? url, CancellationToken cancellationToken = default) =>
        ValidateUntrustedUrlAsync(url, DefaultDnsResolver, cancellationToken);

    public static Task<Result> ValidateUntrustedUrlAsync(
        string? url,
        IDnsResolver dnsResolver,
        CancellationToken cancellationToken = default) =>
        ValidateUrlAsync(url, allowPrivateAndLoopback: false, dnsResolver, cancellationToken);

    /// <summary>
    /// Validates a provider inference endpoint. Loopback and RFC1918 are allowed; link-local remains blocked.
    /// </summary>
    public static Task<Result> ValidateProviderEndpointAsync(string? url, CancellationToken cancellationToken = default) =>
        ValidateProviderEndpointAsync(url, DefaultDnsResolver, cancellationToken);

    public static Task<Result> ValidateProviderEndpointAsync(
        string? url,
        IDnsResolver dnsResolver,
        CancellationToken cancellationToken = default) =>
        ValidateUrlAsync(url, allowPrivateAndLoopback: true, dnsResolver, cancellationToken);

    /// <summary>
    /// Validates public provider endpoints referenced by <see cref="ArcanumSettings"/> before
    /// persistence. Secret-backed targets such as CommLink are resolved and validated only at
    /// their dispatch boundary.
    /// </summary>
    public static async Task<Result> ValidateArcanumSettingsAsync(
        ArcanumSettings settings,
        CancellationToken cancellationToken = default) =>
        await ValidateArcanumSettingsAsync(
            settings,
            DefaultDnsResolver,
            cancellationToken).ConfigureAwait(false);

    public static async Task<Result> ValidateArcanumSettingsAsync(
        ArcanumSettings settings,
        IDnsResolver dnsResolver,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dnsResolver);

        ProviderSettings[] providers = settings.Providers ?? [];

        foreach (ProviderSettings provider in providers)
        {
            if (string.IsNullOrWhiteSpace(provider.Endpoint))
            {
                continue;
            }

            Result endpoint = await ValidateProviderEndpointAsync(
                provider.Endpoint,
                dnsResolver,
                cancellationToken).ConfigureAwait(false);

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
        CancellationToken cancellationToken = default) =>
        await ResolveValidatedAddressesAsync(
            host,
            allowPrivateAndLoopback,
            DefaultDnsResolver,
            cancellationToken).ConfigureAwait(false);

    public static async Task<Result<IReadOnlyList<IPAddress>>> ResolveValidatedAddressesAsync(
        string host,
        bool allowPrivateAndLoopback,
        IDnsResolver dnsResolver,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dnsResolver);

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
            addresses = await dnsResolver.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
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
    /// <param name="connectTimeout">
    /// Optional bound on establishing the TCP connection to each candidate address. <c>null</c> (the
    /// default) leaves connection establishment to the OS. This bounds only connection setup — it is
    /// never a deadline on the request as a whole (see <c>docs/Arcanum.DESIGN.md</c> &#167;2.1).
    /// </param>
    public static SocketsHttpHandler CreateUntrustedEgressHandler(TimeSpan? connectTimeout = null) =>
        CreateUntrustedEgressHandler(DefaultDnsResolver, connectTimeout);

    public static SocketsHttpHandler CreateUntrustedEgressHandler(
        IDnsResolver dnsResolver,
        TimeSpan? connectTimeout = null) =>
        CreateEgressHandler(allowPrivateAndLoopback: false, dnsResolver, connectTimeout);

    /// <summary>
    /// Creates a <see cref="SocketsHttpHandler"/> for provider inference and connectivity probes.
    /// Loopback and RFC1918 are allowed; link-local remains blocked; DNS is pinned at connect time.
    /// </summary>
    public static SocketsHttpHandler CreateProviderEgressHandler() =>
        CreateProviderEgressHandler(DefaultDnsResolver);

    public static SocketsHttpHandler CreateProviderEgressHandler(IDnsResolver dnsResolver) =>
        CreateEgressHandler(allowPrivateAndLoopback: true, dnsResolver, connectTimeout: null);

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
        CancellationToken cancellationToken = default) =>
        await ValidateUrlAsync(
            url,
            allowPrivateAndLoopback,
            DefaultDnsResolver,
            cancellationToken).ConfigureAwait(false);

    public static async Task<Result> ValidateUrlAsync(
        string? url,
        bool allowPrivateAndLoopback,
        IDnsResolver dnsResolver,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dnsResolver);

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
            dnsResolver,
            cancellationToken).ConfigureAwait(false);

        if (resolved.IsFailure)
        {
            return Result.Failure(resolved.Error);
        }

        return Result.Success();
    }

    private static SocketsHttpHandler CreateEgressHandler(
        bool allowPrivateAndLoopback,
        IDnsResolver dnsResolver,
        TimeSpan? connectTimeout)
    {
        ArgumentNullException.ThrowIfNull(dnsResolver);

        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,

            // A system proxy would move DNS resolution outside this handler and
            // defeat address validation/pinning for the original destination.
            UseProxy = false,

            // SocketsHttpHandler.ConnectTimeout is ignored once ConnectCallback is set, so the bound is
            // applied inside the callback instead.
            ConnectCallback = (context, cancellationToken) =>
                EgressConnectCallbackAsync(
                    context,
                    allowPrivateAndLoopback,
                    dnsResolver,
                    connectTimeout,
                    cancellationToken),
        };
    }

    private static async ValueTask<Stream> EgressConnectCallbackAsync(
        SocketsHttpConnectionContext context,
        bool allowPrivateAndLoopback,
        IDnsResolver dnsResolver,
        TimeSpan? connectTimeout,
        CancellationToken cancellationToken)
    {
        string host = context.DnsEndPoint.Host;

        int port = context.DnsEndPoint.Port;

        using CancellationTokenSource? connectScope = connectTimeout is { } timeout
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;

        connectScope?.CancelAfter(connectTimeout!.Value);

        CancellationToken connectToken = connectScope?.Token ?? cancellationToken;

        Result<IReadOnlyList<IPAddress>> resolved;

        try
        {
            resolved = await ResolveValidatedAddressesAsync(
                host,
                allowPrivateAndLoopback,
                dnsResolver,
                connectToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ConnectDeadlineElapsed(connectScope, cancellationToken))
        {
            throw new HttpRequestException($"Timed out resolving '{host}' within the outbound connect timeout.");
        }

        if (resolved.IsFailure)
        {
            throw new HttpRequestException(resolved.Error.Message);
        }

        IReadOnlyList<IPAddress> pinned = _pinnedAddressRewriterForTests is null
            ? resolved.Value
            : _pinnedAddressRewriterForTests(resolved.Value);

        List<Exception>? connectErrors = null;

        foreach (IPAddress address in pinned)
        {
            if (IsBlockedAddress(address, allowPrivateAndLoopback))
            {
                throw new HttpRequestException(BlockedMessage);
            }

            Socket socket = new(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                // Mirrors SocketsHttpHandler's default connect path: small request frames must not wait
                // on Nagle's algorithm just because this handler owns the socket.
                NoDelay = true,
            };

            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), connectToken).ConfigureAwait(false);

                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException or TimeoutException)
            {
                connectErrors ??= [];

                connectErrors.Add(ex);

                socket.Dispose();
            }
            catch (OperationCanceledException ex) when (ConnectDeadlineElapsed(connectScope, cancellationToken))
            {
                // The connect bound elapsed, not the caller's cancellation. Surface it as a transport
                // failure so callers see a connection problem rather than a spurious cancellation.
                socket.Dispose();

                connectErrors ??= [];

                connectErrors.Add(ex);

                break;
            }
            catch
            {
                // ConnectAsync throws OperationCanceledException for the caller's
                // deadline. Do not leave that unconnected socket for finalization.
                socket.Dispose();

                throw;
            }
        }

        throw new HttpRequestException(
            $"Could not connect to '{host}' on port {port}.",
            connectErrors is null ? null : new AggregateException(connectErrors));
    }

    /// <summary>
    /// True when <paramref name="connectScope"/> fired its own connect bound rather than the caller
    /// cancelling the request.
    /// </summary>
    private static bool ConnectDeadlineElapsed(CancellationTokenSource? connectScope, CancellationToken callerToken) =>
        connectScope is { IsCancellationRequested: true } && !callerToken.IsCancellationRequested;

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

    /// <summary>
    /// The untrusted-egress address policy on its own, for a client that pins its own sockets (TheForge's
    /// markdown image loader) and must refuse exactly what this guard refuses.
    /// </summary>
    public static bool IsBlockedForUntrustedEgress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        return IsBlockedAddress(address, allowPrivateAndLoopback: false);
    }

    internal static bool IsBlockedAddress(IPAddress address, bool allowPrivateAndLoopback)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return IsBlockedIPv4(address.GetAddressBytes(), allowPrivateAndLoopback);
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

        // ff00::/8 is never a unicast egress target, whatever the trust level.
        if (address.IsIPv6Multicast)
        {
            return true;
        }

        byte[] ipv6Bytes = address.GetAddressBytes();

        // The RFC 8215 local-use NAT64 prefix names a translator inside the local network, so it is the
        // IPv6 spelling of the IPv4 hosts behind it and untrusted egress refuses it as it refuses RFC1918.
        if (!allowPrivateAndLoopback && IsLocalUseNat64(ipv6Bytes))
        {
            return true;
        }

        // NAT64, 6to4, Teredo and the IPv4-compatible and IPv4-translated forms carry an IPv4 destination
        // inside the IPv6 address; a translator on the path would reach that IPv4 host, so the embedded
        // address meets the same IPv4 policy. The IPv6 loopback ::1 (with or without a scope id) is the
        // IPv4-compatible spelling of 0.0.0.1, so untrusted egress refuses it here by the 0.0.0.0/8 rule.
        foreach (byte[] embedded in EmbeddedIPv4Addresses(ipv6Bytes))
        {
            if (IsBlockedIPv4(embedded, allowPrivateAndLoopback))
            {
                return true;
            }
        }

        if (allowPrivateAndLoopback)
        {
            return false;
        }

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

    private static bool IsBlockedIPv4(byte[] bytes, bool allowPrivateAndLoopback)
    {
        if (IsLinkLocalIPv4(bytes))
        {
            return true;
        }

        if (IsCarrierGradeNatIPv4(bytes))
        {
            return true;
        }

        // 224.0.0.0/4 multicast and 240.0.0.0/4 reserved (which includes 255.255.255.255 broadcast) are
        // never valid unicast destinations, so even trusted provider egress refuses them.
        if (bytes[0] >= 224)
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

        if (IsProtocolAssignmentOrBenchmarkIPv4(bytes))
        {
            return true;
        }

        return bytes[0] == 0;
    }

    private static IEnumerable<byte[]> EmbeddedIPv4Addresses(byte[] ipv6Bytes)
    {
        // NAT64 well-known prefix 64:ff9b::/96 (RFC 6052): the IPv4 address is the final 32 bits.
        if (ipv6Bytes[0] == 0x00
            && ipv6Bytes[1] == 0x64
            && ipv6Bytes[2] == 0xFF
            && ipv6Bytes[3] == 0x9B
            && HasZeroBytes(ipv6Bytes, 4, 12))
        {
            yield return ipv6Bytes[12..16];

            yield break;
        }

        // Local-use NAT64 64:ff9b:1::/48 (RFC 8215). The operator chooses the prefix length, and RFC 6052
        // places the IPv4 address differently for each, skipping the reserved octet at bits 64-71, so every
        // position a prefix inside this /48 can use is judged.
        if (IsLocalUseNat64(ipv6Bytes))
        {
            // /48
            yield return [ipv6Bytes[6], ipv6Bytes[7], ipv6Bytes[9], ipv6Bytes[10]];

            // /56
            yield return [ipv6Bytes[7], ipv6Bytes[9], ipv6Bytes[10], ipv6Bytes[11]];

            // /64
            yield return ipv6Bytes[9..13];

            // /96
            yield return ipv6Bytes[12..16];

            yield break;
        }

        // IPv4-compatible ::a.b.c.d (RFC 4291, deprecated) and IPv4-translated ::ffff:0:a.b.c.d (RFC 2765):
        // the IPv4 address is the final 32 bits. The unspecified address was refused before this point.
        if (HasZeroBytes(ipv6Bytes, 0, 12)
            || (HasZeroBytes(ipv6Bytes, 0, 8)
                && ipv6Bytes[8] == 0xFF
                && ipv6Bytes[9] == 0xFF
                && ipv6Bytes[10] == 0x00
                && ipv6Bytes[11] == 0x00))
        {
            yield return ipv6Bytes[12..16];

            yield break;
        }

        // 6to4 2002::/16 (RFC 3056): the IPv4 address follows the prefix.
        if (ipv6Bytes[0] == 0x20 && ipv6Bytes[1] == 0x02)
        {
            yield return ipv6Bytes[2..6];

            yield break;
        }

        // Teredo 2001:0000::/32 (RFC 4380): the Teredo server IPv4 follows the prefix and the client IPv4
        // is stored inverted in the final 32 bits.
        if (ipv6Bytes[0] == 0x20
            && ipv6Bytes[1] == 0x01
            && ipv6Bytes[2] == 0x00
            && ipv6Bytes[3] == 0x00)
        {
            yield return ipv6Bytes[4..8];

            yield return
            [
                (byte)~ipv6Bytes[12],
                (byte)~ipv6Bytes[13],
                (byte)~ipv6Bytes[14],
                (byte)~ipv6Bytes[15],
            ];
        }
    }

    private static bool IsLocalUseNat64(byte[] ipv6Bytes) =>
        ipv6Bytes[0] == 0x00
        && ipv6Bytes[1] == 0x64
        && ipv6Bytes[2] == 0xFF
        && ipv6Bytes[3] == 0x9B
        && ipv6Bytes[4] == 0x00
        && ipv6Bytes[5] == 0x01;

    private static bool HasZeroBytes(byte[] bytes, int start, int end)
    {
        for (int index = start; index < end; index++)
        {
            if (bytes[index] != 0)
            {
                return false;
            }
        }

        return true;
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

    /// <summary>
    /// 192.0.0.0/24 (IETF protocol assignments) and 198.18.0.0/15 (benchmarking). Neither is a public
    /// service address, but proxy fake-IP modes use 198.18.0.0/15, so trusted provider egress keeps it.
    /// </summary>
    private static bool IsProtocolAssignmentOrBenchmarkIPv4(byte[] bytes) =>
        (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0)
        || (bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19));
}
