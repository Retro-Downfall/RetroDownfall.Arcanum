using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Cli.Services.Setup;

/// <summary>
/// Distinguishable outcomes of the guided-setup provider validation step. Every non-reachable value
/// names a different operator action, so the wizard never reports "it failed" without saying which
/// dependency failed.
/// </summary>
public enum SetupConnectivityStatus
{

    NotAttempted,

    Reachable,

    /// <summary>The endpoint was rejected before any packet left the process (invalid, unresolvable, or blocked).</summary>
    EndpointRejected,

    TlsFailure,

    AuthenticationFailed,

    ModelMissing,

    MalformedResponse,

    Timeout,

    Unreachable,

}

/// <summary>
/// Network location class reported in the completion summary. The endpoint itself is treated as a
/// sensitive configuration value, so only this class is ever printed.
/// </summary>
public enum SetupEndpointClass
{

    Unknown,

    Loopback,

    PrivateNetwork,

    Public,

}

public sealed record SetupConnectivityResult(
    SetupConnectivityStatus Status,
    long LatencyMs,
    int ModelsAdvertised,
    bool SelectedModelAdvertised,
    string Detail)
{

    public static SetupConnectivityResult NotAttempted { get; } =
        new(SetupConnectivityStatus.NotAttempted, 0, 0, false, "Provider validation was not run.");

    public bool IsUsable =>
        Status is SetupConnectivityStatus.Reachable or SetupConnectivityStatus.NotAttempted;

}

/// <summary>
/// Seam over the guarded egress handler so tests can drive every classification without opening a
/// socket. Production always returns <see cref="OutboundUrlGuard.CreateProviderEgressHandler"/>.
/// </summary>
public interface ISetupProbeHandlerFactory
{

    HttpMessageHandler Create();

}

public sealed class SetupProbeHandlerFactory : ISetupProbeHandlerFactory
{

    public HttpMessageHandler Create() => OutboundUrlGuard.CreateProviderEgressHandler();

}

public interface ISetupProviderProbe
{

    Task<SetupConnectivityResult> ProbeAsync(
        string? endpoint,
        string? model,
        string? apiKey,
        CancellationToken cancellationToken);

}

/// <summary>
/// Non-billable provider validation for <c>arcanum setup</c>: one guarded <c>GET {endpoint}/models</c>
/// under a strict timeout. No completion is requested, so validation never spends inference tokens
/// (ADR 0002 non-billable surfaces). Runs entirely in-process, so it works before <c>arcanum serve</c>
/// has ever started.
/// </summary>
public sealed class SetupProviderProbe(
    ISetupProbeHandlerFactory handlerFactory) : ISetupProviderProbe
{

    /// <summary>Strict boundary from issue #19: a bad network must not hang the wizard.</summary>
    public static TimeSpan ProbeTimeout { get; } = TimeSpan.FromSeconds(5);

    private const long MaxProbeResponseBytes = 4 * 1024 * 1024;

    public async Task<SetupConnectivityResult> ProbeAsync(
        string? endpoint,
        string? model,
        string? apiKey,
        CancellationToken cancellationToken)
    {

        if (string.IsNullOrWhiteSpace(endpoint))
        {

            return new SetupConnectivityResult(
                SetupConnectivityStatus.EndpointRejected,
                0,
                0,
                false,
                "The provider endpoint is empty.");

        }

        Result guard = await OutboundUrlGuard
            .ValidateProviderEndpointAsync(endpoint, cancellationToken)
            .ConfigureAwait(false);

        if (guard.IsFailure)
        {

            return new SetupConnectivityResult(
                SetupConnectivityStatus.EndpointRejected,
                0,
                0,
                false,
                "The endpoint URL failed validation (invalid, unresolvable, or blocked by the "
                + "outbound guard). Nothing was sent.");

        }

        string probeUrl = endpoint.Trim().TrimEnd('/') + "/models";

        // The factory hands back a fresh guarded egress handler per probe, so the client owns it:
        // without disposeHandler the handler and its connection pool would outlive every probe.
        using HttpClient client = new(handlerFactory.Create(), disposeHandler: true)
        {

            Timeout = ProbeTimeout,

        };

        if (!string.IsNullOrWhiteSpace(apiKey))
        {

            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", apiKey.Trim());

        }

        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {

            using HttpResponseMessage response = await client
                .GetAsync(probeUrl, cancellationToken)
                .ConfigureAwait(false);

            stopwatch.Stop();

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {

                return new SetupConnectivityResult(
                    SetupConnectivityStatus.AuthenticationFailed,
                    stopwatch.ElapsedMilliseconds,
                    0,
                    false,
                    $"The endpoint rejected the credential with HTTP {(int)response.StatusCode}.");

            }

            if (!response.IsSuccessStatusCode)
            {

                return new SetupConnectivityResult(
                    SetupConnectivityStatus.Unreachable,
                    stopwatch.ElapsedMilliseconds,
                    0,
                    false,
                    $"The endpoint returned HTTP {(int)response.StatusCode}.");

            }

            string? payload = await ReadCappedAsync(response.Content, cancellationToken)
                .ConfigureAwait(false);

            if (payload is null)
            {

                return new SetupConnectivityResult(
                    SetupConnectivityStatus.MalformedResponse,
                    stopwatch.ElapsedMilliseconds,
                    0,
                    false,
                    "The endpoint response exceeded the maximum allowed size and was not read.");

            }

            if (!TryParseModels(payload, out string[] models))
            {

                return new SetupConnectivityResult(
                    SetupConnectivityStatus.MalformedResponse,
                    stopwatch.ElapsedMilliseconds,
                    0,
                    false,
                    "The endpoint responded successfully but the body is not an OpenAI model list.");

            }

            if (models.Length == 0)
            {

                return new SetupConnectivityResult(
                    SetupConnectivityStatus.Reachable,
                    stopwatch.ElapsedMilliseconds,
                    0,
                    false,
                    "The endpoint is reachable but advertises no models, so the selected model "
                    + "could not be confirmed.");

            }

            bool advertised = !string.IsNullOrWhiteSpace(model)
                && models.Any(candidate =>
                    string.Equals(candidate, model.Trim(), StringComparison.OrdinalIgnoreCase));

            return advertised || string.IsNullOrWhiteSpace(model)
                ? new SetupConnectivityResult(
                    SetupConnectivityStatus.Reachable,
                    stopwatch.ElapsedMilliseconds,
                    models.Length,
                    advertised,
                    $"The endpoint is reachable and advertises {models.Length} model(s).")
                : new SetupConnectivityResult(
                    SetupConnectivityStatus.ModelMissing,
                    stopwatch.ElapsedMilliseconds,
                    models.Length,
                    false,
                    $"The endpoint is reachable but does not advertise '{model.Trim()}'.");

        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {

            stopwatch.Stop();

            return new SetupConnectivityResult(
                SetupConnectivityStatus.Timeout,
                stopwatch.ElapsedMilliseconds,
                0,
                false,
                $"The provider probe timed out after {ProbeTimeout.TotalSeconds:0} seconds.");

        }
        catch (HttpRequestException exception)
        {

            stopwatch.Stop();

            return Classify(exception, stopwatch.ElapsedMilliseconds);

        }

    }

    /// <summary>
    /// Classifies an endpoint by host literal alone. No DNS lookup is performed, so classification
    /// never emits a resolver query for an endpoint the operator has not yet accepted.
    /// </summary>
    public static SetupEndpointClass ClassifyEndpoint(string? endpoint)
    {

        if (!Uri.TryCreate(endpoint?.Trim(), UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {

            return SetupEndpointClass.Unknown;

        }

        if (uri.IsLoopback)
        {

            return SetupEndpointClass.Loopback;

        }

        if (!IPAddress.TryParse(uri.Host.Trim('[', ']'), out IPAddress? address))
        {

            return SetupEndpointClass.Public;

        }

        if (IPAddress.IsLoopback(address))
        {

            return SetupEndpointClass.Loopback;

        }

        return IsPrivate(address)
            ? SetupEndpointClass.PrivateNetwork
            : SetupEndpointClass.Public;

    }

    private static bool IsPrivate(IPAddress address)
    {

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {

            return address.IsIPv6LinkLocal
                || address.IsIPv6SiteLocal
                || address.IsIPv6UniqueLocal;

        }

        Span<byte> octets = stackalloc byte[4];

        return address.TryWriteBytes(octets, out _)
            && (octets[0] == 10
                || (octets[0] == 172 && octets[1] >= 16 && octets[1] <= 31)
                || (octets[0] == 192 && octets[1] == 168)
                || (octets[0] == 169 && octets[1] == 254));

    }

    private static SetupConnectivityResult Classify(
        HttpRequestException exception,
        long latencyMs) =>
        exception.HttpRequestError switch
        {
            HttpRequestError.SecureConnectionError => new SetupConnectivityResult(
                SetupConnectivityStatus.TlsFailure,
                latencyMs,
                0,
                false,
                "The TLS handshake failed (untrusted, expired, or mismatched certificate)."),
            HttpRequestError.NameResolutionError => new SetupConnectivityResult(
                SetupConnectivityStatus.Unreachable,
                latencyMs,
                0,
                false,
                "The endpoint host could not be resolved."),
            _ => new SetupConnectivityResult(
                SetupConnectivityStatus.Unreachable,
                latencyMs,
                0,
                false,
                "The endpoint could not be contacted (connection, DNS, or proxy error)."),
        };

    private static bool TryParseModels(string json, out string[] models)
    {

        try
        {

            OpenAiModelListResponse? response = JsonSerializer.Deserialize(
                json,
                ArcanumJsonContext.Default.OpenAiModelListResponse);

            if (response is null)
            {

                models = [];

                return false;

            }

            models = response.Data is not { Count: > 0 } data
                ? []
                : [.. data
                    .Select(static model => model.Id)
                    .Where(static id => !string.IsNullOrWhiteSpace(id))];

            return true;

        }
        catch (JsonException)
        {

            models = [];

            return false;

        }

    }

    private static async Task<string?> ReadCappedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {

        if (content.Headers.ContentLength is { } declared && declared > MaxProbeResponseBytes)
        {

            return null;

        }

        Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        await using (stream.ConfigureAwait(false))
        {

            using MemoryStream buffer = new();

            byte[] chunk = new byte[81_920];

            long total = 0;

            int read;

            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {

                total += read;

                if (total > MaxProbeResponseBytes)
                {

                    return null;

                }

                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);

            }

            return Encoding.UTF8.GetString(buffer.ToArray());

        }

    }

}
