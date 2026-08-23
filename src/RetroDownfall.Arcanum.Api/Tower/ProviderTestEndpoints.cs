using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Api.Tower;

[ExcludeFromCodeCoverage] // Reason: live HTTP provider connectivity probe endpoint; does not persist configuration.
/// <remarks>Non-billable (ADR 0002 / <c>NonBillableSurfaces.ProviderTest</c>): connectivity probe only.</remarks>
internal static class ProviderTestEndpoints
{

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Hard cap on a probed provider's response body. A hostile or misconfigured "OpenAI-compatible"
    /// endpoint could otherwise return an unbounded body that this endpoint would buffer wholesale via
    /// <see cref="HttpContent.ReadAsStringAsync(CancellationToken)"/>.
    /// </summary>
    private const long MaxProbeResponseBytes = 4 * 1024 * 1024;

    public static RouteGroupBuilder MapProviderTestEndpoints(this RouteGroupBuilder apiGroup)
    {
        apiGroup.MapPost(
            "/providers/test",
            async (ProviderTestRequest? body, HttpContext ctx, ILoggerFactory loggerFactory) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                if (body is null || string.IsNullOrWhiteSpace(body.Endpoint))
                {
                    return Results.BadRequest(
                        ApiResponse<ProviderTestResult>.FromResult(
                            Result<ProviderTestResult>.Failure(
                                new Error(ErrorCodes.Validation.InvalidBody, "endpoint is required.")),
                            traceId));
                }

                if (body.Type is not AiProviderKind.OpenAICompatible)
                {
                    return Results.BadRequest(
                        ApiResponse<ProviderTestResult>.FromResult(
                            Result<ProviderTestResult>.Failure(
                                new Error(ErrorCodes.Validation.InvalidProviderType, "type must be OpenAICompatible.")),
                            traceId));
                }

                Result urlValidation = await OutboundUrlGuard
                    .ValidateProviderEndpointAsync(body.Endpoint, ctx.RequestAborted)
                    .ConfigureAwait(false);

                if (urlValidation.IsFailure)
                {
                    // Matches the sanitized posture already used below for probe connectivity
                    // failures (W3.5): keep the detailed guard reason (which can carry resolved
                    // hosts/IPs) in the server log; return a generic, non-revealing message to the
                    // client.
                    loggerFactory.CreateLogger("RetroDownfall.Arcanum.Api.TheForge.ProviderTest")
                        .LogDebug(
                            "Provider test endpoint {Endpoint} failed outbound URL validation: {Reason}",
                            body.Endpoint,
                            urlValidation.Error.Message);

                    return Results.BadRequest(
                        ApiResponse<ProviderTestResult>.FromResult(
                            Result<ProviderTestResult>.Failure(
                                new Error(urlValidation.Error.Code, "The endpoint URL failed validation (invalid, unresolvable, or blocked). See server logs for detail.")),
                            traceId));
                }

                ProviderTestResult result = await ProbeProviderAsync(
                    body,
                    loggerFactory.CreateLogger("RetroDownfall.Arcanum.Api.TheForge.ProviderTest"),
                    ctx.RequestAborted).ConfigureAwait(false);

                return Results.Ok(
                    ApiResponse<ProviderTestResult>.FromResult(Result<ProviderTestResult>.Success(result), traceId));
            })
        .WithName("PostProviderTest");

        return apiGroup;
    }

    private static async Task<ProviderTestResult> ProbeProviderAsync(
        ProviderTestRequest request,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        string baseUrl = request.Endpoint.Trim().TrimEnd('/');

        string probeUrl = $"{baseUrl}/models";

        using HttpClient client = new(OutboundUrlGuard.CreateProviderEgressHandler(), disposeHandler: true)
        {
            Timeout = ProbeTimeout,
        };

        if (!string.IsNullOrWhiteSpace(request.ApiKey))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey.Trim());
        }

        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            // ResponseHeadersRead, not the default: with ResponseContentRead the whole body is buffered
            // inside GetAsync — pre-allocated to the declared Content-Length, up to HttpClient's ~2 GiB
            // default — before TryReadCappedStringAsync is ever consulted, which reduces the cap below
            // to a report of an allocation the probed endpoint already forced.
            using HttpResponseMessage response = await client
                .GetAsync(probeUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            stopwatch.Stop();

            if (!response.IsSuccessStatusCode)
            {
                return new ProviderTestResult(
                    false,
                    stopwatch.ElapsedMilliseconds,
                    [],
                    $"Endpoint returned HTTP {(int)response.StatusCode}.");
            }

            string? payload = await TryReadCappedStringAsync(response.Content, MaxProbeResponseBytes, cancellationToken)
                .ConfigureAwait(false);

            if (payload is null)
            {
                return new ProviderTestResult(
                    false,
                    stopwatch.ElapsedMilliseconds,
                    [],
                    "Endpoint response exceeded the maximum allowed size and was not read.");
            }

            string[] models = ParseOpenAiModels(payload);

            return new ProviderTestResult(true, stopwatch.ElapsedMilliseconds, models, null);
        }
        catch (TaskCanceledException)
        {
            stopwatch.Stop();

            return new ProviderTestResult(false, stopwatch.ElapsedMilliseconds, [], "The provider probe timed out.");
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();

            // W3.5: keep the detail (which can carry internal hostnames/paths) in the server log;
            // return a generic message to the client, matching the sanitized llama-pull posture.
            logger.LogDebug(ex, "Provider probe to {Endpoint} failed with a connection error.", request.Endpoint);

            return new ProviderTestResult(
                false,
                stopwatch.ElapsedMilliseconds,
                [],
                "The provider probe failed (connection, DNS, or TLS error). See server logs for detail.");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            logger.LogWarning(ex, "Provider probe to {Endpoint} failed unexpectedly.", request.Endpoint);

            return new ProviderTestResult(
                false,
                stopwatch.ElapsedMilliseconds,
                [],
                "The provider probe failed. See server logs for detail.");
        }
    }

    /// <summary>
    /// Reads <paramref name="content"/> up to <paramref name="maxBytes"/> as UTF-8 text, returning
    /// <c>null</c> (without buffering the rest) when the response is — or grows to be — larger than
    /// that, rather than <see cref="HttpContent.ReadAsStringAsync(CancellationToken)"/>'s unbounded
    /// buffering of an untrusted (potentially hostile) endpoint's body.
    /// </summary>
    private static async Task<string?> TryReadCappedStringAsync(
        HttpContent content,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        long? declaredLength = content.Headers.ContentLength;

        if (declaredLength is { } length && length > maxBytes)
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

                if (total > maxBytes)
                {
                    return null;
                }

                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }
    }

    private static string[] ParseOpenAiModels(string json)
    {
        try
        {
            OpenAiModelListResponse? response = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.OpenAiModelListResponse);

            if (response?.Data is not { Count: > 0 } data)
            {
                return [];
            }

            return data
                .Select(static model => model.Id)
                .Where(static id => !string.IsNullOrEmpty(id))
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

}
