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

    public async Task<Result> DispatchAsync(CommLinkMessage message, CancellationToken cancellationToken = default)
    {

        CommLinkSettings? commLinkSettings = optionsMonitor.CurrentValue.CommLink;

        string? url = commLinkSettings?.WebhookUrl;

        if (string.IsNullOrWhiteSpace(url))
        {

            logger.LogWarning("Comm Link webhook URL is not configured; alert was not sent.");

            return Result.Failure(new Error(ErrorCodes.CommLink.Suppressed, "webhook URL is not configured"));

        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? endpoint))
        {

            logger.LogWarning("Comm Link webhook URL is invalid; alert was not sent.");

            return Result.Failure(new Error(ErrorCodes.CommLink.Suppressed, "webhook URL is invalid"));

        }

        string[] allowedSchemes = commLinkSettings?.AllowedSchemes ?? ["https"];

        if (commLinkSettings?.AllowedHosts.Length > 0 && !IsHostAllowed(endpoint.Host, commLinkSettings.AllowedHosts))
        {

            logger.LogWarning(
                "Comm Link webhook URL host '{Host}' is not in Arcanum:CommLink:AllowedHosts; alert was not sent.",
                endpoint.Host);

            return Result.Failure(new Error(ErrorCodes.CommLink.Suppressed, "webhook host not allowed"));

        }

        if (!IsSchemeAllowed(endpoint.Scheme, allowedSchemes))
        {

            logger.LogWarning(
                "Comm Link webhook URL scheme '{Scheme}' is not in Arcanum:CommLink:AllowedSchemes; alert was not sent.",
                endpoint.Scheme);

            return Result.Failure(new Error(ErrorCodes.CommLink.Suppressed, "webhook scheme not allowed"));

        }

        Result outbound = await OutboundUrlGuard.ValidateUntrustedUrlAsync(url, cancellationToken).ConfigureAwait(false);

        if (outbound.IsFailure)
        {

            logger.LogWarning(
                "Comm Link webhook URL was rejected by outbound URL policy: {Reason}",
                outbound.Error.Message);

            return Result.Failure(new Error(ErrorCodes.CommLink.Suppressed, "webhook rejected by outbound URL policy"));

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

            using HttpResponseMessage response = await client
                .PostAsync(endpoint, content, cancellationToken)
                .ConfigureAwait(false);

            await HttpResponseBodyDrainer.DrainAsync(response.Content, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {

                string phrase = response.ReasonPhrase ?? string.Empty;

                return Result.Failure(
                    new Error(
                        "CommLink.WebhookHttpError",
                        $"Webhook returned HTTP {(int)response.StatusCode} {phrase}".Trim()));

            }

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

            throw;

        }
        catch (Exception ex)
        {

            logger.LogWarning(ex, "Comm Link webhook POST failed.");

            return Result.Failure(
                new Error(
                    "CommLink.WebhookException",
                    "Comm Link webhook POST failed. See server logs for details."));

        }

        return Result.Success();

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
