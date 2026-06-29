using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Api.TheForge;

[ExcludeFromCodeCoverage] // Reason: live HTTP provider connectivity probe endpoint; does not persist configuration.
internal static class ProviderTestEndpoints
{

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

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

                if (body.Type is not AiProviderKind.Ollama and not AiProviderKind.OpenAICompatible)
                {
                    return Results.BadRequest(
                        ApiResponse<ProviderTestResult>.FromResult(
                            Result<ProviderTestResult>.Failure(
                                new Error("Validation.InvalidProviderType", "type must be Ollama or OpenAICompatible.")),
                            traceId));
                }

                Result urlValidation = await OutboundUrlGuard
                    .ValidateProviderEndpointAsync(body.Endpoint, ctx.RequestAborted)
                    .ConfigureAwait(false);

                if (urlValidation.IsFailure)
                {
                    return Results.BadRequest(
                        ApiResponse<ProviderTestResult>.FromResult(
                            Result<ProviderTestResult>.Failure(urlValidation.Error),
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

        string probeUrl = request.Type switch
        {
            AiProviderKind.Ollama => $"{baseUrl}/api/tags",
            AiProviderKind.OpenAICompatible => $"{baseUrl}/models",
            _ => $"{baseUrl}/models",
        };

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
            using HttpResponseMessage response = await client.GetAsync(probeUrl, cancellationToken).ConfigureAwait(false);

            stopwatch.Stop();

            if (!response.IsSuccessStatusCode)
            {
                return new ProviderTestResult(
                    false,
                    stopwatch.ElapsedMilliseconds,
                    [],
                    $"Endpoint returned HTTP {(int)response.StatusCode}.");
            }

            string payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            string[] models = request.Type switch
            {
                AiProviderKind.Ollama => ParseOllamaModels(payload),
                AiProviderKind.OpenAICompatible => ParseOpenAiModels(payload),
                _ => [],
            };

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

    private static string[] ParseOllamaModels(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("models", out JsonElement models) || models.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            List<string> names = [];

            foreach (JsonElement model in models.EnumerateArray())
            {
                if (model.TryGetProperty("name", out JsonElement name) && name.ValueKind == JsonValueKind.String)
                {
                    names.Add(name.GetString() ?? string.Empty);
                }
            }

            return names.Where(static n => n.Length > 0).ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string[] ParseOpenAiModels(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            List<string> ids = [];

            foreach (JsonElement model in data.EnumerateArray())
            {
                if (model.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.String)
                {
                    ids.Add(id.GetString() ?? string.Empty);
                }
            }

            return ids.Where(static n => n.Length > 0).ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

}
