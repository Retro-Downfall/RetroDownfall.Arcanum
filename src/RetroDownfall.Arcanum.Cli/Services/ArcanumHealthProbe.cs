using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Models;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Cli.Services;

public enum HealthProbeState
{

    NotAttempted,

    Healthy,

    Unauthorized,

    UnhealthyStatus,

    ConnectionRefused,

    NetworkUnreachable,

    DnsFailure,

    TlsFailure,

    Timeout,

}

public sealed record HealthProbeResult(
    HealthProbeState State,
    int? StatusCode,
    TimeSpan Latency,
    string? Error,
    string? DurableOperationsDetail = null,
    IReadOnlyList<HealthComponentDto>? Components = null);

/// <summary>
/// Authenticated probe of <c>GET /api/health</c>. Distinguishes auth failure from
/// definite no-listener (connection refused / network unreachable / DNS) from
/// "something answered" cases (TLS failure, timeout) so auto-serve never spawns
/// a colliding second server.
/// </summary>
internal static class ArcanumHealthProbe
{

    internal static async Task<HealthProbeResult> ProbeAsync(
        HttpClient client,
        Uri url,
        string? apiKey,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(client);

        ArgumentNullException.ThrowIfNull(url);

        using HttpRequestMessage request = new(HttpMethod.Get, url);

        if (apiKey is not null)
        {
            _ = request.Headers.TryAddWithoutValidation(ArcanumApiHeaders.ApiKey, apiKey);
        }

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        cts.CancelAfter(timeout);

        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            using HttpResponseMessage response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);

            sw.Stop();

            int statusCode = (int)response.StatusCode;

            if (response.IsSuccessStatusCode)
            {
                IReadOnlyList<HealthComponentDto>? components = await TryReadComponentsAsync(
                    response,
                    cts.Token).ConfigureAwait(false);
                return new HealthProbeResult(
                    HealthProbeState.Healthy,
                    statusCode,
                    sw.Elapsed,
                    null,
                    components
                        ?.FirstOrDefault(static component => component.Name == "DurableOperations")
                        ?.Detail,
                    components);
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new HealthProbeResult(HealthProbeState.Unauthorized, statusCode, sw.Elapsed, null);
            }

            return new HealthProbeResult(
                HealthProbeState.UnhealthyStatus,
                statusCode,
                sw.Elapsed,
                response.ReasonPhrase);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            sw.Stop();

            return new HealthProbeResult(HealthProbeState.Timeout, null, sw.Elapsed, "Probe timed out.");
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();

            return new HealthProbeResult(ClassifyHttpRequestException(ex), null, sw.Elapsed, ex.Message);
        }

    }

    /// <summary>
    /// The whole component array, not just the durable-operations row. The host already computes a
    /// per-subsystem verdict for Grimoire, MCP, providers, embeddings, file encryption, the tool-child
    /// sandbox, workspace checks, and Conclave; surfacing it lets <c>arcanum doctor</c> report those
    /// subsystems without a second, divergent CLI-side implementation of each one.
    /// </summary>
    private static async Task<IReadOnlyList<HealthComponentDto>?> TryReadComponentsAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            await using Stream stream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            ApiResponse<HealthReportDto>? envelope = await JsonSerializer.DeserializeAsync(
                stream,
                ArcanumJsonContext.Default.ApiResponseHealthReportDto,
                cancellationToken).ConfigureAwait(false);
            return envelope?.Data?.Components;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static HealthProbeState ClassifyHttpRequestException(HttpRequestException ex)
    {

        if (IsTlsFailure(ex))
        {
            return HealthProbeState.TlsFailure;
        }

        SocketError? socketError = FindSocketError(ex);

        if (socketError is SocketError.ConnectionRefused)
        {
            return HealthProbeState.ConnectionRefused;
        }

        if (socketError is SocketError.HostNotFound or SocketError.NoData)
        {
            return HealthProbeState.DnsFailure;
        }

        if (socketError is SocketError.NetworkUnreachable
            or SocketError.HostUnreachable
            or SocketError.AddressNotAvailable)
        {
            return HealthProbeState.NetworkUnreachable;
        }

        // Conservative default: treat as no-listener so auto-serve can recover a down host.
        // Prefer not spawning when the inner exception looks TLS-related (already handled)
        // or when status code hints at an HTTP response (not present on HttpRequestException).
        return HealthProbeState.NetworkUnreachable;

    }

    private static bool IsTlsFailure(Exception ex)
    {

        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is AuthenticationException)
            {
                return true;
            }

            string typeName = current.GetType().FullName ?? string.Empty;

            if (typeName.Contains("Ssl", StringComparison.OrdinalIgnoreCase)
                || typeName.Contains("Tls", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string message = current.Message;

            if (message.Contains("SSL", StringComparison.OrdinalIgnoreCase)
                || message.Contains("TLS", StringComparison.OrdinalIgnoreCase)
                || message.Contains("certificate", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;

    }

    private static SocketError? FindSocketError(Exception ex)
    {

        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is SocketException socket)
            {
                return socket.SocketErrorCode;
            }
        }

        return null;

    }

}
