using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.CommLink;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.CommLink;

internal sealed class WebhookCommLinkDispatcher(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<ArcanumSettings> optionsMonitor,
    ILogger<WebhookCommLinkDispatcher> logger) : ICommLinkDispatcher
{

    public async Task<Result> DispatchAsync(CommLinkMessage message, CancellationToken cancellationToken = default)
    {

        string? url = optionsMonitor.CurrentValue.CommLink?.WebhookUrl;

        if (string.IsNullOrWhiteSpace(url))
        {

            logger.LogWarning("Comm Link webhook URL is not configured; alert was not sent.");

            return Result.Success();

        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? endpoint))
        {

            logger.LogWarning("Comm Link webhook URL is invalid; alert was not sent.");

            return Result.Success();

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

        HttpClient client = httpClientFactory.CreateClient("CommLinkWebhook");

        try
        {

            using HttpResponseMessage response = await client
                .PostAsync(endpoint, content, cancellationToken)
                .ConfigureAwait(false);

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

}
