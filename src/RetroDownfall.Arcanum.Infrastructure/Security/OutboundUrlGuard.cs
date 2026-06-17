using System.Net;
using System.Net.Sockets;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

/// <summary>
/// Blocks outbound <c>http</c>/<c>https</c> requests to loopback, RFC1918, and link-local targets (SSRF hardening).
/// </summary>
public static class OutboundUrlGuard
{

    public const string BlockedErrorCode = "Security.BlockedOutboundUrl";

    private const string BlockedMessage =
        "Outbound URL targets a loopback, private, or link-local address and is not permitted.";

    /// <summary>
    /// Validates an untrusted outbound URL (model pulls, webhooks, model-map downloads).
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

            if (provider.Type != AiProviderKind.LlamaCppServer
                && !string.IsNullOrWhiteSpace(provider.Endpoint))
            {

                Result endpoint = await ValidateProviderEndpointAsync(provider.Endpoint, cancellationToken).ConfigureAwait(false);

                if (endpoint.IsFailure)
                {
                    return Result.Failure(new Error(
                        BlockedErrorCode,
                        $"Provider '{provider.Name}' endpoint: {endpoint.Error.Message}"));
                }

            }

            Dictionary<string, string>? modelMap = provider.LlamaCpp?.ModelMap;

            if (modelMap is null || modelMap.Count == 0)
            {
                continue;
            }

            foreach (KeyValuePair<string, string> entry in modelMap)
            {

                if (string.IsNullOrWhiteSpace(entry.Value))
                {
                    continue;
                }

                Result modelUrl = await ValidateUntrustedUrlAsync(entry.Value, cancellationToken).ConfigureAwait(false);

                if (modelUrl.IsFailure)
                {
                    return Result.Failure(new Error(
                        BlockedErrorCode,
                        $"Provider '{provider.Name}' llamaCpp.modelMap['{entry.Key}']: {modelUrl.Error.Message}"));
                }

            }

        }

        return Result.Success();

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

        Result literalHost = ValidateLiteralHost(uri.Host, allowPrivateAndLoopback);

        if (literalHost.IsFailure)
        {
            return literalHost;
        }

        IPAddress[] addresses;

        try
        {

            addresses = await Dns.GetHostAddressesAsync(uri.Host, cancellationToken).ConfigureAwait(false);

        }
        catch (SocketException)
        {

            return Result.Failure(new Error(BlockedErrorCode, $"Could not resolve host '{uri.Host}'."));
        }

        if (addresses.Length == 0)
        {

            return Result.Failure(new Error(BlockedErrorCode, $"Could not resolve host '{uri.Host}'."));

        }

        foreach (IPAddress address in addresses)
        {

            if (IsBlockedAddress(address, allowPrivateAndLoopback))
            {
                return Result.Failure(new Error(BlockedErrorCode, BlockedMessage));
            }

        }

        return Result.Success();

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

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {

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

            byte[] bytes = address.GetAddressBytes();

            if ((bytes[0] & 0xFE) == 0xFC)
            {
                return true;
            }

            if (address.IsIPv6SiteLocal)
            {
                return true;
            }

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

}
