using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.CommLink;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.CommLink;

internal sealed class WebhookCommLinkDispatcher(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<ArcanumSettings> optionsMonitor,
    ILogger<WebhookCommLinkDispatcher> logger) : ICommLinkDispatcher
{

    internal const string HttpClientName = "CommLinkWebhook";

    public async Task<Result<CommLinkDeliveryResult>> DispatchAsync(
        CommLinkMessage message,
        CancellationToken cancellationToken = default)
    {

        CommLinkSettings commLinkSettings = optionsMonitor.CurrentValue.ResolveCommLink();

        // Resolve the secret-bearing URL only at the dispatch boundary. The URL never enters the
        // bindable configuration graph and is never included in logs or returned errors.
        string? url = EnvironmentCredentialResolver.ResolveCommLinkWebhookUrl(
            commLinkSettings);

        if (string.IsNullOrWhiteSpace(url))
        {

            logger.LogWarning("Comm Link webhook URL environment reference is unset; alert was not sent.");

            return Result<CommLinkDeliveryResult>.Success(
                new CommLinkDeliveryResult(CommLinkDeliveryStatus.Suppressed));

        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? endpoint))
        {

            logger.LogWarning("Comm Link webhook URL is invalid; alert was not sent.");

            return Result<CommLinkDeliveryResult>.Success(
                new CommLinkDeliveryResult(CommLinkDeliveryStatus.Suppressed));

        }

        string[] allowedSchemes = commLinkSettings.AllowedSchemes ?? ["https"];

        if (commLinkSettings.AllowedHosts.Length > 0 && !IsHostAllowed(endpoint.Host, commLinkSettings.AllowedHosts))
        {

            logger.LogWarning(
                "Comm Link webhook URL host '{Host}' is not in Arcanum:Integrations:CommLink:AllowedHosts; alert was not sent.",
                endpoint.Host);

            return Result<CommLinkDeliveryResult>.Success(
                new CommLinkDeliveryResult(CommLinkDeliveryStatus.Suppressed));

        }

        if (!IsSchemeAllowed(endpoint.Scheme, allowedSchemes))
        {

            logger.LogWarning(
                "Comm Link webhook URL scheme '{Scheme}' is not in Arcanum:Integrations:CommLink:AllowedSchemes; alert was not sent.",
                endpoint.Scheme);

            return Result<CommLinkDeliveryResult>.Success(
                new CommLinkDeliveryResult(CommLinkDeliveryStatus.Suppressed));

        }

        Result outbound = await OutboundUrlGuard.ValidateUntrustedUrlAsync(url, cancellationToken).ConfigureAwait(false);

        if (outbound.IsFailure)
        {

            logger.LogWarning(
                "Comm Link webhook URL was rejected by outbound URL policy: {Reason}",
                outbound.Error.Message);

            return Result<CommLinkDeliveryResult>.Success(
                new CommLinkDeliveryResult(CommLinkDeliveryStatus.Suppressed));

        }

        string? severityName = Enum.GetName(message.Severity);

        if (string.IsNullOrEmpty(severityName))
        {

            severityName = nameof(CommLinkSeverity.Info);

        }

        WebhookPayloadDto dto = new()
        {

            Title = message.Title,

            Body = message.Body,

            Severity = severityName,

            Source = message.Source,

            TimestampUtc = DateTimeOffset.UtcNow.ToString("O"),

        };

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(dto, CommLinkInfrastructureJsonContext.Default.WebhookPayloadDto);

        using ByteArrayContent content = new(json);

        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        HttpClient client = httpClientFactory.CreateClient(HttpClientName);

        try
        {

            using HttpRequestMessage request = new(HttpMethod.Post, endpoint)
            {

                Content = content,

            };

            using HttpResponseMessage response = await client
                .SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);

            await HttpResponseBodyDrainer.DrainAsync(response.Content, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {

                return Result<CommLinkDeliveryResult>.Failure(
                    new Error(
                        "CommLink.WebhookHttpError",
                        $"Webhook returned HTTP {(int)response.StatusCode}."));

            }

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

            throw;

        }
        catch (Exception ex)
        {

            // Log host only — never the full webhook URL (may contain path/query secrets).
            logger.LogWarning(
                "Comm Link webhook POST failed for host {Host} ({FailureType}).",
                endpoint.Host,
                ex.GetType().Name);

            return Result<CommLinkDeliveryResult>.Failure(
                new Error(
                    "CommLink.WebhookException",
                    "Comm Link webhook POST failed. See server logs for details."));

        }

        return Result<CommLinkDeliveryResult>.Success(
            new CommLinkDeliveryResult(CommLinkDeliveryStatus.Delivered));

    }

    private static bool IsSchemeAllowed(string scheme, string[] allowedSchemes)
    {

        foreach (string allowed in allowedSchemes)
        {

            if (string.IsNullOrWhiteSpace(allowed))
            {
                continue;
            }

            if (string.Equals(scheme, allowed.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

        }

        return false;

    }

    private static bool IsHostAllowed(string host, string[] allowedHosts)
    {

        foreach (string allowed in allowedHosts)
        {

            string trimmed = allowed.Trim();

            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            if (string.Equals(host, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

        }

        return false;

    }

}
