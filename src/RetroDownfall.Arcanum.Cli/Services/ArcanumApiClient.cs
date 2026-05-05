using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Cli.Services;

public sealed class ArcanumApiClient(IHttpClientFactory httpClientFactory, ISecretStore secretStore)
{
    public async Task<Result<string>> AskAsync(PingRequest body, CancellationToken cancellationToken)
    {
        string? apiKey = await secretStore.GetApiKeyAsync().ConfigureAwait(false);

        if (apiKey is null)
        {
            return Result<string>.Failure(new Error(
                "Security.MissingApiKey",
                "No API key found. Run 'arcanum serve' once to generate and store a key."));
        }

        HttpClient client = httpClientFactory.CreateClient("ArcanumApi");

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(body, ArcanumJsonContext.Default.PingRequest);

        using ByteArrayContent content = new(json);

        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        using HttpRequestMessage request = new(HttpMethod.Post, "api/intelligence/ping");
        request.Content = content;

        _ = request.Headers.TryAddWithoutValidation(ArcanumApiHeaders.ApiKey, apiKey);

        try
        {
            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            byte[] responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            ApiResponse<string>? envelope = responseBytes.Length == 0
                ? null
                : JsonSerializer.Deserialize(responseBytes, ArcanumJsonContext.Default.ApiResponseString);

            if (response.IsSuccessStatusCode)
            {
                if (envelope is null)
                {
                    return Result<string>.Failure(new Error("Api.InvalidResponse", "Empty or invalid response from API."));
                }

                if (!envelope.IsSuccess)
                {
                    Error err = envelope.Error ?? new Error("Api.Error", "Request failed.");

                    return Result<string>.Failure(err);
                }

                return Result<string>.Success(envelope.Data ?? string.Empty);
            }

            if (envelope is not null && envelope is { IsSuccess: false, Error: not null })
            {
                return Result<string>.Failure(envelope.Error.Value);
            }

            string fallback = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

            return Result<string>.Failure(new Error("Api.HttpError", fallback));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Result<string>.Failure(new Error(
                "Connection.Timeout",
                "The request to the Arcanum API timed out. The server may be busy with a long-running model operation."));
        }
        catch (HttpRequestException)
        {
            return Result<string>.Failure(new Error(
                "Connection",
                "API is unreachable. Is 'arcanum serve' running in a background terminal?"));
        }
    }

    public async Task<Result<bool>> SubmitHumanResponseAsync(
        string promptId,
        string answer,
        CancellationToken cancellationToken)
    {
        string? apiKey = await secretStore.GetApiKeyAsync().ConfigureAwait(false);

        if (apiKey is null)
        {
            return Result<bool>.Failure(new Error(
                "Security.MissingApiKey",
                "No API key found. Run 'arcanum serve' once to generate and store a key."));
        }

        HttpClient client = httpClientFactory.CreateClient("ArcanumApi");

        SubmitHumanResponseRequest body = new(promptId, answer);

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(body, ArcanumJsonContext.Default.SubmitHumanResponseRequest);

        using ByteArrayContent content = new(json);

        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        using HttpRequestMessage request = new(HttpMethod.Post, "api/intelligence/human-response");
        request.Content = content;

        _ = request.Headers.TryAddWithoutValidation(ArcanumApiHeaders.ApiKey, apiKey);

        try
        {
            using HttpResponseMessage response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            byte[] responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            ApiResponse<bool>? envelope = responseBytes.Length == 0
                ? null
                : JsonSerializer.Deserialize(responseBytes, ArcanumJsonContext.Default.ApiResponseBoolean);

            if (response.IsSuccessStatusCode)
            {
                if (envelope is null)
                {
                    return Result<bool>.Failure(new Error("Api.InvalidResponse", "Empty or invalid response from API."));
                }

                if (!envelope.IsSuccess)
                {
                    Error err = envelope.Error ?? new Error("Api.Error", "Request failed.");

                    return Result<bool>.Failure(err);
                }

                return Result<bool>.Success(envelope.Data);
            }

            if (envelope is not null && envelope is { IsSuccess: false, Error: not null })
            {
                return Result<bool>.Failure(envelope.Error.Value);
            }

            string fallback = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

            return Result<bool>.Failure(new Error("Api.HttpError", fallback));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Result<bool>.Failure(new Error(
                "Connection.Timeout",
                "The request to the Arcanum API timed out. The server may be busy with a long-running model operation."));
        }
        catch (HttpRequestException)
        {
            return Result<bool>.Failure(new Error(
                "Connection",
                "API is unreachable. Is 'arcanum serve' running in a background terminal?"));
        }
    }

    public async IAsyncEnumerable<IntelligenceEvent> AskStreamAsync(
        PingRequest body,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string? apiKey = await secretStore.GetApiKeyAsync().ConfigureAwait(false);

        if (apiKey is null)
        {
            yield return new IntelligenceEvent(
                IntelligenceEventType.Error,
                "No API key found. Run 'arcanum serve' once to generate and store a key.");

            yield break;
        }

        HttpClient client = httpClientFactory.CreateClient("ArcanumApi");

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(body, ArcanumJsonContext.Default.PingRequest);

        using ByteArrayContent content = new(json);

        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        using HttpRequestMessage request = new(HttpMethod.Post, "api/intelligence/ping-stream");
        request.Content = content;

        _ = request.Headers.TryAddWithoutValidation(ArcanumApiHeaders.ApiKey, apiKey);

        HttpResponseMessage? response = null;

        string? sendErrorMessage = null;

        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            sendErrorMessage = "The request to the Arcanum API timed out. The server may be busy with a long-running model operation.";
        }
        catch (HttpRequestException)
        {
            sendErrorMessage = "API is unreachable. Is 'arcanum serve' running in a background terminal?";
        }

        if (sendErrorMessage is not null || response is null)
        {
            yield return new IntelligenceEvent(
                IntelligenceEventType.Error,
                sendErrorMessage ?? "API is unreachable. Is 'arcanum serve' running in a background terminal?");

            yield break;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                byte[] responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

                ApiResponse<string>? envelope = responseBytes.Length == 0
                    ? null
                    : JsonSerializer.Deserialize(responseBytes, ArcanumJsonContext.Default.ApiResponseString);

                string message;

                if (envelope is not null && envelope is { IsSuccess: false, Error: not null })
                {
                    message = envelope.Error.Value.Message;
                }
                else
                {
                    message = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                }

                yield return new IntelligenceEvent(IntelligenceEventType.Error, message);

                yield break;
            }

            await using Stream responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            using StreamReader lineReader = new(responseStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);

            while (true)
            {
                string? line = await lineReader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                IntelligenceEvent? item = JsonSerializer.Deserialize(line, ArcanumJsonContext.Default.IntelligenceEvent);

                if (item is not null)
                {
                    yield return item;
                }
            }
        }
    }
}
