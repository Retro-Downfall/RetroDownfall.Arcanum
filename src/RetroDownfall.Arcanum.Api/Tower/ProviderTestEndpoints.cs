using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
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
using RetroDownfall.Arcanum.Core.Security;
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
            async (
                ProviderTestRequest? body,
                IDnsResolver dnsResolver,
                HttpContext ctx,
                ILoggerFactory loggerFactory) =>
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
                    .ValidateProviderEndpointForProbeAsync(body.Endpoint, dnsResolver, ctx.RequestAborted)
                    .ConfigureAwait(false);

                if (urlValidation.IsFailure)
                {
                    (string reason, string message) = ValidationDiagnostic(urlValidation.Error.Code);

                    LogFailure(loggerFactory.CreateLogger("RetroDownfall.Arcanum.Api.Tower.ProviderTest"), body.Endpoint, reason);

                    return Results.BadRequest(
                        ApiResponse<ProviderTestResult>.FromResult(
                            Result<ProviderTestResult>.Failure(
                                new Error(urlValidation.Error.Code, message)),
                            traceId));
                }

                ProviderTestResult result = await ProbeProviderAsync(
                    body,
                    dnsResolver,
                    loggerFactory.CreateLogger("RetroDownfall.Arcanum.Api.Tower.ProviderTest"),
                    ctx.RequestAborted).ConfigureAwait(false);

                return Results.Ok(
                    ApiResponse<ProviderTestResult>.FromResult(Result<ProviderTestResult>.Success(result), traceId));
            })
        .WithName("PostProviderTest");

        return apiGroup;
    }

    private static async Task<ProviderTestResult> ProbeProviderAsync(
        ProviderTestRequest request,
        IDnsResolver dnsResolver,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        string baseUrl = request.Endpoint.Trim().TrimEnd('/');

        string probeUrl = $"{baseUrl}/models";

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        deadline.CancelAfter(ProbeTimeout);

        using HttpClient client = new(
            OutboundUrlGuard.CreateProviderEgressHandler(dnsResolver),
            disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
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
                .GetAsync(probeUrl, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);

            stopwatch.Stop();

            if (!response.IsSuccessStatusCode)
            {
                return response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? Failed("Unauthorized", "Unauthorized: Check apiKey and the provider's access permissions.")
                    : OutboundUrlGuard.IsRedirectStatusCode(response.StatusCode)
                        ? Failed("Blocked", "Blocked: Provider redirects are not followed. Use a permitted direct endpoint URL.")
                        : Failed("HttpStatus", $"HttpStatus: Endpoint returned HTTP {(int)response.StatusCode}. Check the provider's /v1 base URL.");
            }

            string? payload = await TryReadCappedStringAsync(response.Content, MaxProbeResponseBytes, deadline.Token)
                .ConfigureAwait(false);

            if (payload is null)
            {
                return Failed("ResponseTooLarge", "Endpoint response exceeded the maximum allowed size and was not read.");
            }

            string[]? models = ParseOpenAiModels(payload);

            return models is null
                ? Failed("NotAModelList", "NotAModelList: Expected an OpenAI model list. Check the provider's /v1 base URL.")
                : new ProviderTestResult(true, stopwatch.ElapsedMilliseconds, models, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            stopwatch.Stop();

            return Failed("Timeout", "The provider probe timed out.");
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();

            for (Exception? cause = ex; cause is not null; cause = cause.InnerException)
            {
                if (cause is OutboundConnectException { ErrorCode: { } code })
                {
                    (string reason, string message) = ValidationDiagnostic(code);

                    return Failed(reason, message);
                }

                if (cause is SocketException { SocketErrorCode: SocketError.ConnectionRefused })
                {
                    return Failed("ConnectionRefused", "ConnectionRefused: Check that the provider is running and the endpoint port is correct.");
                }
            }

            return ex.HttpRequestError switch
            {
                HttpRequestError.SecureConnectionError => Failed("Tls", "Tls: Check the endpoint's https scheme and trusted certificate configuration."),
                HttpRequestError.NameResolutionError => Failed("DnsFailed", "DnsFailed: Check the endpoint hostname and DNS configuration."),
                _ => Failed("ConnectionFailed", "ConnectionFailed: Check that the provider is running and the endpoint address and port are correct."),
            };
        }
        catch (Exception)
        {
            stopwatch.Stop();

            return Failed("ProbeFailed", "ProbeFailed: Check the endpoint URL and provider configuration.");
        }

        ProviderTestResult Failed(string reason, string message)
        {
            LogFailure(logger, request.Endpoint, reason);

            return new ProviderTestResult(false, stopwatch.ElapsedMilliseconds, [], message);
        }
    }

    private static (string Reason, string Message) ValidationDiagnostic(string code) => code switch
    {
        ErrorCodes.Validation.InvalidUrl => ("InvalidUrl", "InvalidUrl: Use an absolute http:// or https:// endpoint URL."),
        ErrorCodes.Provider.DnsFailed => ("DnsFailed", "DnsFailed: Check the endpoint hostname and DNS configuration."),
        _ => ("Blocked", "Blocked: Use an endpoint address permitted by the outbound URL policy."),
    };

    private static void LogFailure(ILogger logger, string endpoint, string reason)
    {
        string host = Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out Uri? uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && uri.Host.Length <= 253
                ? uri.Host
                : "unavailable";

        logger.LogWarning("Provider test host {Host} failed: {Reason}.", host, reason);
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

    private static string[]? ParseOpenAiModels(string json)
    {
        try
        {
            OpenAiModelListResponse? response = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.OpenAiModelListResponse);

            if (response?.Data is not { } data
                || data.Any(static model => model is null || string.IsNullOrWhiteSpace(model.Id)))
            {
                return null;
            }

            return data.Select(static model => model.Id).ToArray();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
