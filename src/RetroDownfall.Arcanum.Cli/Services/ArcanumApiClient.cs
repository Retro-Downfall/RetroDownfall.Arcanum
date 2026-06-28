using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.CommLink;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.LlamaCpp;
using RetroDownfall.Arcanum.Core.Pattern.Entities;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Cli.Services;

public sealed class ArcanumApiClient(IHttpClientFactory httpClientFactory, ISecretStore secretStore)
{

    public const string StreamingHttpClientName = "ArcanumApi";

    public const string RequestHttpClientName = "ArcanumApiRequest";

    private const string StreamDisconnectMessage =
        "The connection to the Arcanum API was lost before the stream completed.";

    private const string StreamTimeoutMessage =
        "The request to the Arcanum API timed out. The server may be busy with a long-running model operation.";

    private const string StreamUnreachableMessage =
        "API is unreachable. Is 'arcanum serve' running in a background terminal?";

    private static string? TryMapStreamReadFailure(Exception exception, CancellationToken cancellationToken)
    {

        if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            throw exception;
        }

        return exception switch
        {
            OperationCanceledException => StreamTimeoutMessage,
            IOException => StreamDisconnectMessage,
            HttpRequestException => StreamUnreachableMessage,
            _ => null,
        };

    }

    private static T? TryDeserialize<T>(byte[] bytes, JsonTypeInfo<T> typeInfo) where T : class
    {

        if (bytes.Length == 0)
        {

            return null;

        }

        try
        {

            return JsonSerializer.Deserialize(bytes, typeInfo);

        }
        catch (JsonException)
        {

            return null;

        }

    }

    private static readonly Error MissingApiKeyError = new(
        "Security.MissingApiKey",
        "No API key found. Run 'arcanum serve' once to generate and store a key.");

    private static readonly Error RequestTimeoutError = new(
        "Connection.Timeout",
        "The request to the Arcanum API timed out. The server may be busy with a long-running model operation.");

    private static readonly Error RequestUnreachableError = new(
        "Connection",
        "API is unreachable. Is 'arcanum serve' running in a background terminal?");

    private static readonly Error InvalidResponseError = new(
        "Api.InvalidResponse",
        "Empty or invalid response from API.");

    private static readonly Error ApiRequestFailedError = new(
        "Api.Error",
        "Request failed.");

    private async Task<string?> TryGetApiKeyAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await secretStore.GetApiKeyAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private async Task<Result<T>> SendRequestAsync<T>(
        HttpMethod method,
        string relativePath,
        byte[]? body,
        MediaTypeHeaderValue? contentType,
        JsonTypeInfo<ApiResponse<T>> responseTypeInfo,
        Func<ApiResponse<T>, Result<T>> mapSuccess,
        CancellationToken cancellationToken)
    {
        string? apiKey = await TryGetApiKeyAsync(cancellationToken).ConfigureAwait(false);

        if (apiKey is null)
        {
            return Result<T>.Failure(MissingApiKeyError);
        }

        HttpClient client = httpClientFactory.CreateClient(RequestHttpClientName);

        using HttpRequestMessage request = new(method, relativePath);

        if (body is not null)
        {
            ByteArrayContent content = new(body);

            if (contentType is not null)
            {
                content.Headers.ContentType = contentType;
            }

            request.Content = content;
        }

        _ = request.Headers.TryAddWithoutValidation(ArcanumApiHeaders.ApiKey, apiKey);

        try
        {
            using HttpResponseMessage response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            byte[] responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            ApiResponse<T>? envelope = TryDeserialize(responseBytes, responseTypeInfo);

            if (response.IsSuccessStatusCode)
            {
                if (envelope is null)
                {
                    return Result<T>.Failure(InvalidResponseError);
                }

                if (!envelope.IsSuccess)
                {
                    Error err = envelope.Error ?? ApiRequestFailedError;

                    return Result<T>.Failure(err);
                }

                return mapSuccess(envelope);
            }

            if (envelope is not null && envelope is { IsSuccess: false, Error: not null })
            {
                return Result<T>.Failure(envelope.Error.Value);
            }

            string fallback = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

            return Result<T>.Failure(new Error("Api.HttpError", fallback));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Result<T>.Failure(RequestTimeoutError);
        }
        catch (HttpRequestException)
        {
            return Result<T>.Failure(RequestUnreachableError);
        }
    }

    private async Task<Result<T>> SendRequestAsync<T>(
        HttpMethod method,
        string relativePath,
        byte[]? body,
        MediaTypeHeaderValue? contentType,
        JsonTypeInfo<ApiResponse<T>> responseTypeInfo,
        CancellationToken cancellationToken)
    {
        return await SendRequestAsync(
            method,
            relativePath,
            body,
            contentType,
            responseTypeInfo,
            static envelope => Result<T>.Success(envelope.Data!),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<string>> AskAsync(PingRequest body, CancellationToken cancellationToken)
    {
        string? apiKey = await secretStore.GetApiKeyAsync().ConfigureAwait(false);

        if (apiKey is null)
        {
            return Result<string>.Failure(new Error(
                "Security.MissingApiKey",
                "No API key found. Run 'arcanum serve' once to generate and store a key."));
        }

        HttpClient client = httpClientFactory.CreateClient(RequestHttpClientName);

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

            ApiResponse<PromptResponseDto>? envelope = TryDeserialize(responseBytes, ArcanumJsonContext.Default.ApiResponsePromptResponseDto);

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

                return Result<string>.Success(envelope.Data?.Text ?? string.Empty);
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

        HttpClient client = httpClientFactory.CreateClient(RequestHttpClientName);

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

            ApiResponse<bool>? envelope = TryDeserialize(responseBytes, ArcanumJsonContext.Default.ApiResponseBoolean);

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

                if (envelope.Data != true)
                {
                    return Result<bool>.Failure(
                        new Error("Api.InvalidResponse", "Expected human-response success envelope with data true."));
                }

                return Result<bool>.Success(true);
            }

            if ((int)response.StatusCode == 404)
            {
                if (envelope is not null && envelope is { IsSuccess: false, Error: not null })
                {
                    return Result<bool>.Failure(envelope.Error.Value);
                }

                return Result<bool>.Failure(
                    new Error("Intelligence.HumanPromptNotFound", "No active ask_human prompt matches that promptId."));
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

    public async Task<Result<string>> ReloadMcpAsync(OptionalWorkspaceRequest request, CancellationToken cancellationToken)
    {
        string? apiKey = await secretStore.GetApiKeyAsync().ConfigureAwait(false);

        if (apiKey is null)
        {
            return Result<string>.Failure(new Error(
                "Security.MissingApiKey",
                "No API key found. Run 'arcanum serve' once to generate and store a key."));
        }

        HttpClient client = httpClientFactory.CreateClient(RequestHttpClientName);

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.OptionalWorkspaceRequest);

        using ByteArrayContent content = new(json);

        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        using HttpRequestMessage httpRequest = new(HttpMethod.Post, "api/mcp/reload");

        httpRequest.Content = content;

        _ = httpRequest.Headers.TryAddWithoutValidation(ArcanumApiHeaders.ApiKey, apiKey);

        try
        {
            using HttpResponseMessage response = await client
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            byte[] responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            ApiResponse<string>? envelope = TryDeserialize(responseBytes, ArcanumJsonContext.Default.ApiResponseString);

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

    public async Task<Result<WorkspaceArsenalDto>> GetWorkspaceArsenalAsync(OptionalWorkspaceRequest request, CancellationToken cancellationToken)
    {
        string? apiKey = await secretStore.GetApiKeyAsync().ConfigureAwait(false);

        if (apiKey is null)
        {
            return Result<WorkspaceArsenalDto>.Failure(new Error(
                "Security.MissingApiKey",
                "No API key found. Run 'arcanum serve' once to generate and store a key."));
        }

        HttpClient client = httpClientFactory.CreateClient(RequestHttpClientName);

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.OptionalWorkspaceRequest);

        using ByteArrayContent content = new(json);

        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        using HttpRequestMessage httpRequest = new(HttpMethod.Post, "api/intelligence/arsenal");

        httpRequest.Content = content;

        _ = httpRequest.Headers.TryAddWithoutValidation(ArcanumApiHeaders.ApiKey, apiKey);

        try
        {
            using HttpResponseMessage response = await client
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            byte[] responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            ApiResponse<WorkspaceArsenalDto>? envelope = TryDeserialize(responseBytes, ArcanumJsonContext.Default.ApiResponseWorkspaceArsenalDto);

            if (response.IsSuccessStatusCode)
            {
                if (envelope is null)
                {
                    return Result<WorkspaceArsenalDto>.Failure(new Error("Api.InvalidResponse", "Empty or invalid response from API."));
                }

                if (!envelope.IsSuccess)
                {
                    Error err = envelope.Error ?? new Error("Api.Error", "Request failed.");

                    return Result<WorkspaceArsenalDto>.Failure(err);
                }

                if (envelope.Data is null)
                {
                    return Result<WorkspaceArsenalDto>.Failure(new Error("Api.InvalidResponse", "Arsenal payload was empty."));
                }

                return Result<WorkspaceArsenalDto>.Success(envelope.Data);
            }

            if (envelope is not null && envelope is { IsSuccess: false, Error: not null })
            {
                return Result<WorkspaceArsenalDto>.Failure(envelope.Error.Value);
            }

            string fallback = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

            return Result<WorkspaceArsenalDto>.Failure(new Error("Api.HttpError", fallback));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Result<WorkspaceArsenalDto>.Failure(new Error(
                "Connection.Timeout",
                "The request to the Arcanum API timed out. The server may be busy with a long-running model operation."));
        }
        catch (HttpRequestException)
        {
            return Result<WorkspaceArsenalDto>.Failure(new Error(
                "Connection",
                "API is unreachable. Is 'arcanum serve' running in a background terminal?"));
        }
    }

    public async Task<Result<PatternSnapshot>> PerceivePatternAsync(string directory, CancellationToken cancellationToken = default)
    {
        string? apiKey = await secretStore.GetApiKeyAsync().ConfigureAwait(false);

        if (apiKey is null)
        {
            return Result<PatternSnapshot>.Failure(new Error(
                "Security.MissingApiKey",
                "No API key found. Run 'arcanum serve' once to generate and store a key."));
        }

        HttpClient client = httpClientFactory.CreateClient(RequestHttpClientName);

        string encoded = Uri.EscapeDataString(directory);

        using HttpRequestMessage httpRequest = new(HttpMethod.Get, $"api/perception/look?directory={encoded}");

        _ = httpRequest.Headers.TryAddWithoutValidation(ArcanumApiHeaders.ApiKey, apiKey);

        try
        {
            using HttpResponseMessage response = await client
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            byte[] responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            ApiResponse<PatternSnapshot>? envelope = TryDeserialize(responseBytes, ArcanumJsonContext.Default.ApiResponsePatternSnapshot);

            if (response.IsSuccessStatusCode)
            {
                if (envelope is null)
                {
                    return Result<PatternSnapshot>.Failure(
                        new Error("Api.InvalidResponse", "Empty or invalid response from API."));
                }

                if (!envelope.IsSuccess)
                {
                    Error err = envelope.Error ?? new Error("Api.Error", "Request failed.");

                    return Result<PatternSnapshot>.Failure(err);
                }

                if (envelope.Data is null)
                {
                    return Result<PatternSnapshot>.Failure(
                        new Error("Api.InvalidResponse", "Perception payload was empty."));
                }

                return Result<PatternSnapshot>.Success(envelope.Data);
            }

            if (envelope is not null && envelope is { IsSuccess: false, Error: not null })
            {
                return Result<PatternSnapshot>.Failure(envelope.Error.Value);
            }

            string fallback = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

            return Result<PatternSnapshot>.Failure(new Error("Api.HttpError", fallback));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Result<PatternSnapshot>.Failure(new Error(
                "Connection.Timeout",
                "The request to the Arcanum API timed out. The server may be busy with a long-running model operation."));
        }
        catch (HttpRequestException)
        {
            return Result<PatternSnapshot>.Failure(new Error(
                "Connection",
                "API is unreachable. Is 'arcanum serve' running in a background terminal?"));
        }
    }

    public async Task<Result<SessionQueryResult>> QuerySessionsAsync(
        int? limit = null,
        DateTimeOffset? beforeUpdatedAt = null,
        CancellationToken cancellationToken = default)
    {
        string? apiKey = await secretStore.GetApiKeyAsync().ConfigureAwait(false);

        if (apiKey is null)
        {
            return Result<SessionQueryResult>.Failure(new Error(
                "Security.MissingApiKey",
                "No API key found. Run 'arcanum serve' once to generate and store a key."));
        }

        HttpClient client = httpClientFactory.CreateClient(RequestHttpClientName);

        string query = limit is int l
            ? $"api/sessions?limit={l}"
            : "api/sessions";

        if (beforeUpdatedAt is DateTimeOffset before)
        {
            string encoded = Uri.EscapeDataString(before.ToString("O"));

            query += query.Contains('?', StringComparison.Ordinal) ? $"&beforeUpdatedAt={encoded}" : $"?beforeUpdatedAt={encoded}";
        }

        using HttpRequestMessage httpRequest = new(HttpMethod.Get, query);

        _ = httpRequest.Headers.TryAddWithoutValidation(ArcanumApiHeaders.ApiKey, apiKey);

        try
        {
            using HttpResponseMessage response = await client
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            byte[] responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            ApiResponse<SessionQueryResult>? envelope = TryDeserialize(responseBytes, ArcanumJsonContext.Default.ApiResponseSessionQueryResult);

            if (response.IsSuccessStatusCode)
            {
                if (envelope is null)
                {
                    return Result<SessionQueryResult>.Failure(
                        new Error("Api.InvalidResponse", "Empty or invalid response from API."));
                }

                if (!envelope.IsSuccess)
                {
                    Error err = envelope.Error ?? new Error("Api.Error", "Request failed.");

                    return Result<SessionQueryResult>.Failure(err);
                }

                return Result<SessionQueryResult>.Success(
                    envelope.Data ?? new SessionQueryResult([], null, false));
            }

            if (envelope is not null && envelope is { IsSuccess: false, Error: not null })
            {
                return Result<SessionQueryResult>.Failure(envelope.Error.Value);
            }

            string fallback = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

            return Result<SessionQueryResult>.Failure(new Error("Api.HttpError", fallback));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Result<SessionQueryResult>.Failure(new Error(
                "Connection.Timeout",
                "The request to the Arcanum API timed out. The server may be busy with a long-running model operation."));
        }
        catch (HttpRequestException)
        {
            return Result<SessionQueryResult>.Failure(new Error(
                "Connection",
                "API is unreachable. Is 'arcanum serve' running in a background terminal?"));
        }
    }

    public async Task<Result<SessionAnalytics>> GetSessionAnalyticsAsync(CancellationToken cancellationToken = default)
    {
        string? apiKey = await secretStore.GetApiKeyAsync().ConfigureAwait(false);

        if (apiKey is null)
        {
            return Result<SessionAnalytics>.Failure(new Error(
                "Security.MissingApiKey",
                "No API key found. Run 'arcanum serve' once to generate and store a key."));
        }

        HttpClient client = httpClientFactory.CreateClient(RequestHttpClientName);

        using HttpRequestMessage httpRequest = new(HttpMethod.Get, "api/sessions/analytics");

        _ = httpRequest.Headers.TryAddWithoutValidation(ArcanumApiHeaders.ApiKey, apiKey);

        try
        {
            using HttpResponseMessage response = await client
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            byte[] responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            ApiResponse<SessionAnalytics>? envelope = TryDeserialize(responseBytes, ArcanumJsonContext.Default.ApiResponseSessionAnalytics);

            if (response.IsSuccessStatusCode)
            {
                if (envelope is null)
                {
                    return Result<SessionAnalytics>.Failure(
                        new Error("Api.InvalidResponse", "Empty or invalid response from API."));
                }

                if (!envelope.IsSuccess)
                {
                    Error err = envelope.Error ?? new Error("Api.Error", "Request failed.");

                    return Result<SessionAnalytics>.Failure(err);
                }

                if (envelope.Data is null)
                {
                    return Result<SessionAnalytics>.Failure(
                        new Error("Api.InvalidResponse", "Session analytics payload was empty."));
                }

                return Result<SessionAnalytics>.Success(envelope.Data);
            }

            if (envelope is not null && envelope is { IsSuccess: false, Error: not null })
            {
                return Result<SessionAnalytics>.Failure(envelope.Error.Value);
            }

            string fallback = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

            return Result<SessionAnalytics>.Failure(new Error("Api.HttpError", fallback));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Result<SessionAnalytics>.Failure(new Error(
                "Connection.Timeout",
                "The request to the Arcanum API timed out. The server may be busy with a long-running model operation."));
        }
        catch (HttpRequestException)
        {
            return Result<SessionAnalytics>.Failure(new Error(
                "Connection",
                "API is unreachable. Is 'arcanum serve' running in a background terminal?"));
        }
    }

    public async Task<Result> RestAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        string? apiKey = await secretStore.GetApiKeyAsync().ConfigureAwait(false);

        if (apiKey is null)
        {
            return Result.Failure(new Error(
                "Security.MissingApiKey",
                "No API key found. Run 'arcanum serve' once to generate and store a key."));
        }

        HttpClient client = httpClientFactory.CreateClient(RequestHttpClientName);

        using HttpRequestMessage httpRequest = new(HttpMethod.Post, $"api/sessions/{sessionId:D}/rest");

        _ = httpRequest.Headers.TryAddWithoutValidation(ArcanumApiHeaders.ApiKey, apiKey);

        try
        {
            using HttpResponseMessage response = await client
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            byte[] responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            ApiResponse<bool>? boolEnvelope = TryDeserialize(responseBytes, ArcanumJsonContext.Default.ApiResponseBoolean);

            if (response.StatusCode == System.Net.HttpStatusCode.Accepted)
            {
                if (boolEnvelope is { IsSuccess: true, Data: true })
                {
                    return Result.Success();
                }

                if (boolEnvelope is not null && boolEnvelope is { IsSuccess: false, Error: not null })
                {
                    return Result.Failure(boolEnvelope.Error.Value);
                }

                return Result.Failure(
                    new Error("Api.InvalidResponse", "Expected ApiResponse JSON body on 202 Accepted."));
            }

            if ((int)response.StatusCode == 404)
            {
                if (boolEnvelope is not null && boolEnvelope is { IsSuccess: false, Error: not null })
                {
                    return Result.Failure(boolEnvelope.Error.Value);
                }

                return Result.Failure(
                    new Error("Session.NotFound", "No session exists with that id."));
            }

            ApiResponse<string>? envelope = TryDeserialize(responseBytes, ArcanumJsonContext.Default.ApiResponseString);

            if (envelope is not null && envelope is { IsSuccess: false, Error: not null })
            {
                return Result.Failure(envelope.Error.Value);
            }

            string fallback = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

            return Result.Failure(new Error("Api.HttpError", fallback));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Result.Failure(new Error(
                "Connection.Timeout",
                "The request to the Arcanum API timed out. The server may be busy with a long-running model operation."));
        }
        catch (HttpRequestException)
        {
            return Result.Failure(new Error(
                "Connection",
                "API is unreachable. Is 'arcanum serve' running in a background terminal?"));
        }
    }

    public async Task<Result<SessionDetailDto>> GetSessionAsync(Guid id, CancellationToken cancellationToken = default)
    {
        string? apiKey = await secretStore.GetApiKeyAsync().ConfigureAwait(false);

        if (apiKey is null)
        {
            return Result<SessionDetailDto>.Failure(new Error(
                "Security.MissingApiKey",
                "No API key found. Run 'arcanum serve' once to generate and store a key."));
        }

        HttpClient client = httpClientFactory.CreateClient(RequestHttpClientName);

        using HttpRequestMessage httpRequest = new(HttpMethod.Get, $"api/sessions/{id:D}");

        _ = httpRequest.Headers.TryAddWithoutValidation(ArcanumApiHeaders.ApiKey, apiKey);

        try
        {
            using HttpResponseMessage response = await client
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            byte[] responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            ApiResponse<SessionDetailDto>? envelope = TryDeserialize(responseBytes, ArcanumJsonContext.Default.ApiResponseSessionDetailDto);

            if (response.IsSuccessStatusCode)
            {
                if (envelope is null)
                {
                    return Result<SessionDetailDto>.Failure(
                        new Error("Api.InvalidResponse", "Empty or invalid response from API."));
                }

                if (!envelope.IsSuccess)
                {
                    Error err = envelope.Error ?? new Error("Api.Error", "Request failed.");

                    return Result<SessionDetailDto>.Failure(err);
                }

                if (envelope.Data is null)
                {
                    return Result<SessionDetailDto>.Failure(
                        new Error("Api.InvalidResponse", "Session payload was empty."));
                }

                return Result<SessionDetailDto>.Success(envelope.Data);
            }

            if ((int)response.StatusCode == 404)
            {
                if (envelope is not null && envelope is { IsSuccess: false, Error: not null })
                {
                    return Result<SessionDetailDto>.Failure(envelope.Error.Value);
                }

                return Result<SessionDetailDto>.Failure(
                    new Error("Session.NotFound", "No session exists with that id."));
            }

            if (envelope is not null && envelope is { IsSuccess: false, Error: not null })
            {
                return Result<SessionDetailDto>.Failure(envelope.Error.Value);
            }

            string fallback = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

            return Result<SessionDetailDto>.Failure(new Error("Api.HttpError", fallback));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Result<SessionDetailDto>.Failure(new Error(
                "Connection.Timeout",
                "The request to the Arcanum API timed out. The server may be busy with a long-running model operation."));
        }
        catch (HttpRequestException)
        {
            return Result<SessionDetailDto>.Failure(new Error(
                "Connection",
                "API is unreachable. Is 'arcanum serve' running in a background terminal?"));
        }
    }

    public async Task<Result<EntryDto[]>> GetSessionEntriesAsync(
        Guid sessionId,
        int? offset = null,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        string? apiKey = await secretStore.GetApiKeyAsync().ConfigureAwait(false);

        if (apiKey is null)
        {
            return Result<EntryDto[]>.Failure(new Error(
                "Security.MissingApiKey",
                "No API key found. Run 'arcanum serve' once to generate and store a key."));
        }

        HttpClient client = httpClientFactory.CreateClient(RequestHttpClientName);

        string query = $"api/sessions/{sessionId:D}/entries";

        List<string> queryParts = new();

        if (offset is int off)
        {
            queryParts.Add($"offset={off}");
        }

        if (limit is int lim)
        {
            queryParts.Add($"limit={lim}");
        }

        if (queryParts.Count > 0)
        {
            query += "?" + string.Join('&', queryParts);
        }

        using HttpRequestMessage httpRequest = new(HttpMethod.Get, query);

        _ = httpRequest.Headers.TryAddWithoutValidation(ArcanumApiHeaders.ApiKey, apiKey);

        try
        {
            using HttpResponseMessage response = await client
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            byte[] responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            ApiResponse<EntryDto[]>? envelope = TryDeserialize(responseBytes, ArcanumJsonContext.Default.ApiResponseEntryDtoArray);

            if (response.IsSuccessStatusCode)
            {
                if (envelope is null)
                {
                    return Result<EntryDto[]>.Failure(
                        new Error("Api.InvalidResponse", "Empty or invalid response from API."));
                }

                if (!envelope.IsSuccess)
                {
                    Error err = envelope.Error ?? new Error("Api.Error", "Request failed.");

                    return Result<EntryDto[]>.Failure(err);
                }

                if (envelope.Data is null)
                {
                    return Result<EntryDto[]>.Failure(
                        new Error("Api.InvalidResponse", "Session entries payload was empty."));
                }

                return Result<EntryDto[]>.Success(envelope.Data);
            }

            if ((int)response.StatusCode == 404)
            {
                if (envelope is not null && envelope is { IsSuccess: false, Error: not null })
                {
                    return Result<EntryDto[]>.Failure(envelope.Error.Value);
                }

                return Result<EntryDto[]>.Failure(
                    new Error("Session.NotFound", "No session exists with that id."));
            }

            if (envelope is not null && envelope is { IsSuccess: false, Error: not null })
            {
                return Result<EntryDto[]>.Failure(envelope.Error.Value);
            }

            string fallback = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

            return Result<EntryDto[]>.Failure(new Error("Api.HttpError", fallback));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Result<EntryDto[]>.Failure(new Error(
                "Connection.Timeout",
                "The request to the Arcanum API timed out. The server may be busy with a long-running model operation."));
        }
        catch (HttpRequestException)
        {
            return Result<EntryDto[]>.Failure(new Error(
                "Connection",
                "API is unreachable. Is 'arcanum serve' running in a background terminal?"));
        }
    }

    public async Task<Result> ArchiveSessionAsync(Guid id, CancellationToken cancellationToken = default)
    {
        string? apiKey = await secretStore.GetApiKeyAsync().ConfigureAwait(false);

        if (apiKey is null)
        {
            return Result.Failure(new Error(
                "Security.MissingApiKey",
                "No API key found. Run 'arcanum serve' once to generate and store a key."));
        }

        HttpClient client = httpClientFactory.CreateClient(RequestHttpClientName);

        using HttpRequestMessage httpRequest = new(HttpMethod.Delete, $"api/sessions/{id:D}");

        _ = httpRequest.Headers.TryAddWithoutValidation(ArcanumApiHeaders.ApiKey, apiKey);

        try
        {
            using HttpResponseMessage response = await client
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if ((int)response.StatusCode == 204)
            {
                return Result.Success();
            }

            byte[] responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            ApiResponse<bool>? envelope = TryDeserialize(responseBytes, ArcanumJsonContext.Default.ApiResponseBoolean);

            if ((int)response.StatusCode == 404)
            {
                if (envelope is not null && envelope is { IsSuccess: false, Error: not null })
                {
                    return Result.Failure(envelope.Error.Value);
                }

                return Result.Failure(
                    new Error("Session.NotFound", "No session exists with that id."));
            }

            if (envelope is not null && envelope is { IsSuccess: false, Error: not null })
            {
                return Result.Failure(envelope.Error.Value);
            }

            string fallback = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

            return Result.Failure(new Error("Api.HttpError", fallback));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Result.Failure(new Error(
                "Connection.Timeout",
                "The request to the Arcanum API timed out. The server may be busy with a long-running model operation."));
        }
        catch (HttpRequestException)
        {
            return Result.Failure(new Error(
                "Connection",
                "API is unreachable. Is 'arcanum serve' running in a background terminal?"));
        }
    }

    public async Task<Result<SessionExportResult>> ExportSessionAsync(
        Guid id,
        SessionExportFormat format,
        CancellationToken cancellationToken = default)
    {
        string? apiKey = await secretStore.GetApiKeyAsync().ConfigureAwait(false);

        if (apiKey is null)
        {
            return Result<SessionExportResult>.Failure(new Error(
                "Security.MissingApiKey",
                "No API key found. Run 'arcanum serve' once to generate and store a key."));
        }

        HttpClient client = httpClientFactory.CreateClient(RequestHttpClientName);

        string formatValue = format == SessionExportFormat.Markdown ? "markdown" : "json";

        using HttpRequestMessage httpRequest = new(HttpMethod.Get, $"api/sessions/{id:D}/export?format={formatValue}");

        _ = httpRequest.Headers.TryAddWithoutValidation(ArcanumApiHeaders.ApiKey, apiKey);

        try
        {
            using HttpResponseMessage response = await client
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            byte[] responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            ApiResponse<SessionExportResult>? envelope = TryDeserialize(responseBytes, ArcanumJsonContext.Default.ApiResponseSessionExportResult);

            if (response.IsSuccessStatusCode)
            {
                if (envelope is null)
                {
                    return Result<SessionExportResult>.Failure(
                        new Error("Api.InvalidResponse", "Empty or invalid response from API."));
                }

                if (!envelope.IsSuccess)
                {
                    Error err = envelope.Error ?? new Error("Api.Error", "Request failed.");

                    return Result<SessionExportResult>.Failure(err);
                }

                if (envelope.Data is null)
                {
                    return Result<SessionExportResult>.Failure(
                        new Error("Api.InvalidResponse", "Session export payload was empty."));
                }

                return Result<SessionExportResult>.Success(envelope.Data);
            }

            if ((int)response.StatusCode == 404)
            {
                if (envelope is not null && envelope is { IsSuccess: false, Error: not null })
                {
                    return Result<SessionExportResult>.Failure(envelope.Error.Value);
                }

                return Result<SessionExportResult>.Failure(
                    new Error("Session.NotFound", "No session exists with that id."));
            }

            if (envelope is not null && envelope is { IsSuccess: false, Error: not null })
            {
                return Result<SessionExportResult>.Failure(envelope.Error.Value);
            }

            string fallback = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

            return Result<SessionExportResult>.Failure(new Error("Api.HttpError", fallback));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Result<SessionExportResult>.Failure(new Error(
                "Connection.Timeout",
                "The request to the Arcanum API timed out. The server may be busy with a long-running model operation."));
        }
        catch (HttpRequestException)
        {
            return Result<SessionExportResult>.Failure(new Error(
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

        HttpClient client = httpClientFactory.CreateClient(StreamingHttpClientName);

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

                ApiResponse<string>? envelope = TryDeserialize(responseBytes, ArcanumJsonContext.Default.ApiResponseString);

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

            Stream? responseStream = null;

            string? openStreamError = null;

            try
            {
                responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                openStreamError = TryMapStreamReadFailure(ex, cancellationToken);

                if (openStreamError is null)
                {
                    throw;
                }
            }

            if (openStreamError is not null)
            {
                yield return new IntelligenceEvent(IntelligenceEventType.Error, openStreamError);

                yield break;
            }

            await using (responseStream!)
            {
                Stream openedStream = responseStream!;

                using StreamReader lineReader = new(openedStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);

                while (true)
                {
                    string? line = null;

                    string? readError = null;

                    try
                    {
                        line = await lineReader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        readError = TryMapStreamReadFailure(ex, cancellationToken);

                        if (readError is null)
                        {
                            throw;
                        }
                    }

                    if (readError is not null)
                    {
                        yield return new IntelligenceEvent(IntelligenceEventType.Error, readError);

                        yield break;
                    }

                    if (line is null)
                    {
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    IntelligenceEvent? item;
                    bool malformed = false;

                    try
                    {
                        item = JsonSerializer.Deserialize(line, ArcanumJsonContext.Default.IntelligenceEvent);
                    }
                    catch (JsonException)
                    {
                        item = null;
                        malformed = true;
                    }

                    if (malformed)
                    {
                        yield return new IntelligenceEvent(
                            IntelligenceEventType.Status,
                            "Malformed data received from server. Skipping frame.");

                        continue;
                    }

                    if (item is not null)
                    {
                        yield return item;
                    }
                }
            }
        }
    }

    public async Task<Result<List<LoreDto>>> ListLoreAsync(CancellationToken cancellationToken = default)
    {
        string? apiKey = await secretStore.GetApiKeyAsync().ConfigureAwait(false);

        if (apiKey is null)
        {
            return Result<List<LoreDto>>.Failure(new Error(
                "Security.MissingApiKey",
                "No API key found. Run 'arcanum serve' once to generate and store a key."));
        }

        HttpClient client = httpClientFactory.CreateClient(RequestHttpClientName);

        List<LoreDto> all = [];

        int offset = 0;

        bool hasMore;

        int pageIterations = 0;

        const int maxPageIterations = 10_000;

        do
        {
            pageIterations++;

            if (pageIterations > maxPageIterations)
            {

                return Result<List<LoreDto>>.Failure(new Error(
                    "Api.PaginationLoop",
                    "Lore list pagination exceeded the safety limit. The server may be returning a malformed page."));

            }

            int offsetBeforePage = offset;
            using HttpRequestMessage httpRequest = new(
                HttpMethod.Get,
                $"api/lore?limit=1000&offset={offset}");

            _ = httpRequest.Headers.TryAddWithoutValidation(ArcanumApiHeaders.ApiKey, apiKey);

            try
            {
                using HttpResponseMessage response = await client
                    .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                byte[] responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

                ApiResponse<ListPageResult<LoreDto>>? envelope = TryDeserialize(responseBytes, ArcanumJsonContext.Default.ApiResponseListPageResultLoreDto);

                if (response.IsSuccessStatusCode)
                {
                    if (envelope is null)
                    {
                        return Result<List<LoreDto>>.Failure(
                            new Error("Api.InvalidResponse", "Empty or invalid response from API."));
                    }

                    if (!envelope.IsSuccess)
                    {
                        Error err = envelope.Error ?? new Error("Api.Error", "Request failed.");

                        return Result<List<LoreDto>>.Failure(err);
                    }

                    ListPageResult<LoreDto> page = envelope.Data ?? new ListPageResult<LoreDto>([], false);

                    all.AddRange(page.Items);

                    hasMore = page.HasMore;

                    int nextOffset = page.NextOffset ?? offset + page.Items.Length;

                    offset = nextOffset;
                }
                else
                {
                    if (envelope is not null && envelope is { IsSuccess: false, Error: not null })
                    {
                        return Result<List<LoreDto>>.Failure(envelope.Error.Value);
                    }

                    string fallback = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

                    return Result<List<LoreDto>>.Failure(new Error("Api.HttpError", fallback));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return Result<List<LoreDto>>.Failure(new Error(
                    "Connection.Timeout",
                    "The request to the Arcanum API timed out. The server may be busy with a long-running model operation."));
            }
            catch (HttpRequestException)
            {
                return Result<List<LoreDto>>.Failure(new Error(
                    "Connection",
                    "API is unreachable. Is 'arcanum serve' running in a background terminal?"));
            }
        }
        while (hasMore);

        return Result<List<LoreDto>>.Success(all);
    }

    public async Task<Result<LoreDto>> GetLoreAsync(string key, CancellationToken cancellationToken = default)
    {
        string? apiKey = await secretStore.GetApiKeyAsync().ConfigureAwait(false);

        if (apiKey is null)
        {
            return Result<LoreDto>.Failure(new Error(
                "Security.MissingApiKey",
                "No API key found. Run 'arcanum serve' once to generate and store a key."));
        }

        HttpClient client = httpClientFactory.CreateClient(RequestHttpClientName);

        string encoded = Uri.EscapeDataString(key);

        using HttpRequestMessage httpRequest = new(HttpMethod.Get, $"api/lore/{encoded}");

        _ = httpRequest.Headers.TryAddWithoutValidation(ArcanumApiHeaders.ApiKey, apiKey);

        try
        {
            using HttpResponseMessage response = await client
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            byte[] responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            ApiResponse<LoreDto>? envelope = TryDeserialize(responseBytes, ArcanumJsonContext.Default.ApiResponseLoreDto);

            if (response.IsSuccessStatusCode)
            {
                if (envelope is null)
                {
                    return Result<LoreDto>.Failure(
                        new Error("Api.InvalidResponse", "Empty or invalid response from API."));
                }

                if (!envelope.IsSuccess)
                {
                    Error err = envelope.Error ?? new Error("Api.Error", "Request failed.");

                    return Result<LoreDto>.Failure(err);
                }

                if (envelope.Data is null)
                {
                    return Result<LoreDto>.Failure(
                        new Error("Api.InvalidResponse", "Lore payload was empty."));
                }

                return Result<LoreDto>.Success(envelope.Data);
            }

            if ((int)response.StatusCode == 404)
            {
                if (envelope is not null && envelope is { IsSuccess: false, Error: not null })
                {
                    return Result<LoreDto>.Failure(envelope.Error.Value);
                }

                return Result<LoreDto>.Failure(
                    new Error("Grimoire.LoreNotFound", "No lore exists with that key."));
            }

            if (envelope is not null && envelope is { IsSuccess: false, Error: not null })
            {
                return Result<LoreDto>.Failure(envelope.Error.Value);
            }

            string fallback = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

            return Result<LoreDto>.Failure(new Error("Api.HttpError", fallback));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Result<LoreDto>.Failure(new Error(
                "Connection.Timeout",
                "The request to the Arcanum API timed out. The server may be busy with a long-running model operation."));
        }
        catch (HttpRequestException)
        {
            return Result<LoreDto>.Failure(new Error(
                "Connection",
                "API is unreachable. Is 'arcanum serve' running in a background terminal?"));
        }
    }

    public async Task<Result<LoreDto>> UpsertLoreAsync(
        string key,
        string value,
        CancellationToken cancellationToken = default)
    {
        string? apiKey = await secretStore.GetApiKeyAsync().ConfigureAwait(false);

        if (apiKey is null)
        {
            return Result<LoreDto>.Failure(new Error(
                "Security.MissingApiKey",
                "No API key found. Run 'arcanum serve' once to generate and store a key."));
        }

        HttpClient client = httpClientFactory.CreateClient(RequestHttpClientName);

        UpsertLoreRequest body = new(key, value);

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(body, ArcanumJsonContext.Default.UpsertLoreRequest);

        using ByteArrayContent content = new(json);

        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        using HttpRequestMessage httpRequest = new(HttpMethod.Post, "api/lore");

        httpRequest.Content = content;

        _ = httpRequest.Headers.TryAddWithoutValidation(ArcanumApiHeaders.ApiKey, apiKey);

        try
        {
            using HttpResponseMessage response = await client
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            byte[] responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            ApiResponse<LoreDto>? envelope = TryDeserialize(responseBytes, ArcanumJsonContext.Default.ApiResponseLoreDto);

            if (response.IsSuccessStatusCode)
            {
                if (envelope is null)
                {
                    return Result<LoreDto>.Failure(
                        new Error("Api.InvalidResponse", "Empty or invalid response from API."));
                }

                if (!envelope.IsSuccess)
                {
                    Error err = envelope.Error ?? new Error("Api.Error", "Request failed.");

                    return Result<LoreDto>.Failure(err);
                }

                if (envelope.Data is null)
                {
                    return Result<LoreDto>.Failure(
                        new Error("Api.InvalidResponse", "Lore payload was empty."));
                }

                return Result<LoreDto>.Success(envelope.Data);
            }

            if (envelope is not null && envelope is { IsSuccess: false, Error: not null })
            {
                return Result<LoreDto>.Failure(envelope.Error.Value);
            }

            string fallback = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

            return Result<LoreDto>.Failure(new Error("Api.HttpError", fallback));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Result<LoreDto>.Failure(new Error(
                "Connection.Timeout",
                "The request to the Arcanum API timed out. The server may be busy with a long-running model operation."));
        }
        catch (HttpRequestException)
        {
            return Result<LoreDto>.Failure(new Error(
                "Connection",
                "API is unreachable. Is 'arcanum serve' running in a background terminal?"));
        }
    }

    public async Task<Result<bool>> DeleteLoreAsync(string key, CancellationToken cancellationToken = default)
    {
        string? apiKey = await secretStore.GetApiKeyAsync().ConfigureAwait(false);

        if (apiKey is null)
        {
            return Result<bool>.Failure(new Error(
                "Security.MissingApiKey",
                "No API key found. Run 'arcanum serve' once to generate and store a key."));
        }

        HttpClient client = httpClientFactory.CreateClient(RequestHttpClientName);

        string encoded = Uri.EscapeDataString(key);

        using HttpRequestMessage httpRequest = new(HttpMethod.Delete, $"api/lore/{encoded}");

        _ = httpRequest.Headers.TryAddWithoutValidation(ArcanumApiHeaders.ApiKey, apiKey);

        try
        {
            using HttpResponseMessage response = await client
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            byte[] responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            ApiResponse<bool>? envelope = TryDeserialize(responseBytes, ArcanumJsonContext.Default.ApiResponseBoolean);

            if ((int)response.StatusCode == 404)
            {
                if (envelope is not null && envelope is { IsSuccess: false, Error: not null })
                {
                    return Result<bool>.Failure(envelope.Error.Value);
                }

                return Result<bool>.Failure(
                    new Error("Grimoire.LoreNotFound", "No lore exists with that key."));
            }

            if (response.IsSuccessStatusCode)
            {
                if (envelope is null)
                {
                    return Result<bool>.Failure(
                        new Error("Api.InvalidResponse", "Empty or invalid response from API."));
                }

                if (!envelope.IsSuccess)
                {
                    Error err = envelope.Error ?? new Error("Api.Error", "Request failed.");

                    return Result<bool>.Failure(err);
                }

                if (envelope.Data != true)
                {
                    return Result<bool>.Failure(
                        new Error("Api.InvalidResponse", "Expected delete-lore success envelope with data true."));
                }

                return Result<bool>.Success(true);
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

    public async Task<Result<UnseenServantJobStatusDto[]>> GetDaemonJobsAsync(CancellationToken cancellationToken = default)
    {
        string? apiKey = await secretStore.GetApiKeyAsync().ConfigureAwait(false);

        if (apiKey is null)
        {
            return Result<UnseenServantJobStatusDto[]>.Failure(new Error(
                "Security.MissingApiKey",
                "No API key found. Run 'arcanum serve' once to generate and store a key."));
        }

        HttpClient client = httpClientFactory.CreateClient(RequestHttpClientName);

        using HttpRequestMessage httpRequest = new(HttpMethod.Get, "api/unseen-servant/jobs");

        _ = httpRequest.Headers.TryAddWithoutValidation(ArcanumApiHeaders.ApiKey, apiKey);

        try
        {
            using HttpResponseMessage response = await client
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            byte[] responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            ApiResponse<UnseenServantJobStatusDto[]>? envelope = TryDeserialize(responseBytes, ArcanumJsonContext.Default.ApiResponseUnseenServantJobStatusDtoArray);

            if (response.IsSuccessStatusCode)
            {
                if (envelope is null)
                {
                    return Result<UnseenServantJobStatusDto[]>.Failure(
                        new Error("Api.InvalidResponse", "Empty or invalid response from API."));
                }

                if (!envelope.IsSuccess)
                {
                    Error err = envelope.Error ?? new Error("Api.Error", "Request failed.");

                    return Result<UnseenServantJobStatusDto[]>.Failure(err);
                }

                return Result<UnseenServantJobStatusDto[]>.Success(envelope.Data ?? []);
            }

            if (envelope is not null && envelope is { IsSuccess: false, Error: not null })
            {
                return Result<UnseenServantJobStatusDto[]>.Failure(envelope.Error.Value);
            }

            string fallback = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

            return Result<UnseenServantJobStatusDto[]>.Failure(new Error("Api.HttpError", fallback));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Result<UnseenServantJobStatusDto[]>.Failure(new Error(
                "Connection.Timeout",
                "The request to the Arcanum API timed out. The server may be busy with a long-running model operation."));
        }
        catch (HttpRequestException)
        {
            return Result<UnseenServantJobStatusDto[]>.Failure(new Error(
                "Connection",
                "API is unreachable. Is 'arcanum serve' running in a background terminal?"));
        }
    }

    public async Task<Result<UnseenServantJobStatusDto>> AdjustDaemonJobInitiativeAsync(
        string jobName,
        int intervalMinutes,
        CancellationToken cancellationToken = default)
    {
        string? apiKey = await secretStore.GetApiKeyAsync().ConfigureAwait(false);

        if (apiKey is null)
        {
            return Result<UnseenServantJobStatusDto>.Failure(new Error(
                "Security.MissingApiKey",
                "No API key found. Run 'arcanum serve' once to generate and store a key."));
        }

        HttpClient client = httpClientFactory.CreateClient(RequestHttpClientName);

        string encoded = Uri.EscapeDataString(jobName);

        AdjustInitiativeRequestDto body = new(intervalMinutes);

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(body, ArcanumJsonContext.Default.AdjustInitiativeRequestDto);

        using ByteArrayContent content = new(json);

        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        using HttpRequestMessage httpRequest = new(HttpMethod.Post, $"api/unseen-servant/jobs/{encoded}/initiative");

        httpRequest.Content = content;

        _ = httpRequest.Headers.TryAddWithoutValidation(ArcanumApiHeaders.ApiKey, apiKey);

        try
        {
            using HttpResponseMessage response = await client
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            byte[] responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            ApiResponse<UnseenServantJobStatusDto>? envelope = TryDeserialize(responseBytes, ArcanumJsonContext.Default.ApiResponseUnseenServantJobStatusDto);

            if (response.IsSuccessStatusCode)
            {
                if (envelope is null)
                {
                    return Result<UnseenServantJobStatusDto>.Failure(
                        new Error("Api.InvalidResponse", "Empty or invalid response from API."));
                }

                if (!envelope.IsSuccess)
                {
                    Error err = envelope.Error ?? new Error("Api.Error", "Request failed.");

                    return Result<UnseenServantJobStatusDto>.Failure(err);
                }

                if (envelope.Data is null)
                {
                    return Result<UnseenServantJobStatusDto>.Failure(
                        new Error("Api.InvalidResponse", "Daemon job status payload was empty."));
                }

                return Result<UnseenServantJobStatusDto>.Success(envelope.Data);
            }

            if (envelope is not null && envelope is { IsSuccess: false, Error: not null })
            {
                return Result<UnseenServantJobStatusDto>.Failure(envelope.Error.Value);
            }

            string fallback = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

            return Result<UnseenServantJobStatusDto>.Failure(new Error("Api.HttpError", fallback));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Result<UnseenServantJobStatusDto>.Failure(new Error(
                "Connection.Timeout",
                "The request to the Arcanum API timed out. The server may be busy with a long-running model operation."));
        }
        catch (HttpRequestException)
        {
            return Result<UnseenServantJobStatusDto>.Failure(new Error(
                "Connection",
                "API is unreachable. Is 'arcanum serve' running in a background terminal?"));
        }
    }

    public async Task<Result<bool>> SendCommLinkAlertAsync(
        CommLinkMessageRequestDto body,
        CancellationToken cancellationToken = default)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(body, ArcanumJsonContext.Default.CommLinkMessageRequestDto);

        return await SendRequestAsync(
            HttpMethod.Post,
            "api/commlink/send",
            json,
            new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" },
            ArcanumJsonContext.Default.ApiResponseBoolean,
            static envelope => envelope.Data == true
                ? Result<bool>.Success(true)
                : Result<bool>.Failure(new Error("Api.InvalidResponse", "Comm Link alert was not accepted by the API.")),
            cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<LlamaPullProgress> PullModelStreamAsync(
        PullModelRequestDto body,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {

        string? apiKey = await secretStore.GetApiKeyAsync().ConfigureAwait(false);

        if (apiKey is null)
        {
            yield return new LlamaPullProgress { Completed = true, Error = "No API key found. Run 'arcanum serve' once to generate and store a key." };

            yield break;
        }

        HttpClient client = httpClientFactory.CreateClient(StreamingHttpClientName);

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(body, ArcanumJsonContext.Default.PullModelRequestDto);

        using ByteArrayContent content = new(json);

        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        using HttpRequestMessage request = new(HttpMethod.Post, "api/llama/models/pull");

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
            yield return new LlamaPullProgress { Completed = true, Error = sendErrorMessage ?? "API is unreachable. Is 'arcanum serve' running in a background terminal?" };

            yield break;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                byte[] responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

                ApiResponse<string>? envelope = TryDeserialize(responseBytes, ArcanumJsonContext.Default.ApiResponseString);

                string message = envelope is { IsSuccess: false, Error: not null }
                    ? envelope.Error.Value.Message
                    : $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

                yield return new LlamaPullProgress { Completed = true, Error = message };

                yield break;
            }

            Stream? responseStream = null;

            string? openStreamError = null;

            try
            {
                responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                openStreamError = TryMapStreamReadFailure(ex, cancellationToken);

                if (openStreamError is null)
                {
                    throw;
                }
            }

            if (openStreamError is not null)
            {
                yield return new LlamaPullProgress { Completed = true, Error = openStreamError };

                yield break;
            }

            if (responseStream is null)
            {

                yield return new LlamaPullProgress { Completed = true, Error = "Could not read the pull response stream." };

                yield break;

            }

            await using (responseStream)
            {
                using StreamReader lineReader = new(responseStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);

                while (true)
                {
                    string? line = null;

                    string? readError = null;

                    try
                    {
                        line = await lineReader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        readError = TryMapStreamReadFailure(ex, cancellationToken);

                        if (readError is null)
                        {
                            throw;
                        }
                    }

                    if (readError is not null)
                    {
                        yield return new LlamaPullProgress { Completed = true, Error = readError };

                        yield break;
                    }

                    if (line is null)
                    {
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    LlamaPullProgress? item;
                    string? malformedError = null;

                    try
                    {
                        item = JsonSerializer.Deserialize(line, ArcanumJsonContext.Default.LlamaPullProgress);
                    }
                    catch (JsonException)
                    {
                        malformedError = "Malformed progress frame received; continuing pull.";
                        item = null;
                    }

                    if (malformedError is not null)
                    {
                        yield return new LlamaPullProgress { Completed = false, Error = malformedError };

                        continue;
                    }

                    if (item is not null)
                    {
                        yield return item;
                    }
                }
            }
        }

    }

    public async Task<Result<CachedModelInfo[]>> ListCachedModelsAsync(CancellationToken cancellationToken = default)
    {

        return await GetApiAsync(
            "api/llama/models",
            ArcanumJsonContext.Default.ApiResponseCachedModelInfoArray,
            cancellationToken).ConfigureAwait(false);

    }

    public async Task<Result<LlamaServerInfo[]>> ListLlamaServersAsync(CancellationToken cancellationToken = default)
    {

        return await GetApiAsync(
            "api/llama/servers",
            ArcanumJsonContext.Default.ApiResponseLlamaServerInfoArray,
            cancellationToken).ConfigureAwait(false);

    }

    public async Task<Result<LlamaServerInfo>> StartLlamaServerAsync(
        string cacheKey,
        int? gpuLayers,
        int? port,
        CancellationToken cancellationToken = default)
    {

        string? apiKey = await secretStore.GetApiKeyAsync().ConfigureAwait(false);

        if (apiKey is null)
        {
            return Result<LlamaServerInfo>.Failure(new Error(
                "Security.MissingApiKey",
                "No API key found. Run 'arcanum serve' once to generate and store a key."));
        }

        HttpClient client = httpClientFactory.CreateClient(RequestHttpClientName);

        var body = new StartLlamaServerRequestDto { GpuLayers = gpuLayers, Port = port };

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(body, ArcanumJsonContext.Default.StartLlamaServerRequestDto);

        using ByteArrayContent content = new(json);

        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        string encodedKey = Uri.EscapeDataString(cacheKey);

        using HttpRequestMessage request = new(HttpMethod.Post, $"api/llama/servers/{encodedKey}/start");

        request.Content = content;

        _ = request.Headers.TryAddWithoutValidation(ArcanumApiHeaders.ApiKey, apiKey);

        try
        {
            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

            byte[] responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            ApiResponse<LlamaServerInfo>? envelope = TryDeserialize(responseBytes, ArcanumJsonContext.Default.ApiResponseLlamaServerInfo);

            if (envelope is { IsSuccess: true, Data: not null })
            {
                return Result<LlamaServerInfo>.Success(envelope.Data);
            }

            if (envelope is { IsSuccess: false, Error: not null })
            {
                return Result<LlamaServerInfo>.Failure(envelope.Error.Value);
            }

            return Result<LlamaServerInfo>.Failure(new Error("Api.HttpError", $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Result<LlamaServerInfo>.Failure(new Error(
                "Connection.Timeout",
                "The request to the Arcanum API timed out. The server may be busy with a long-running model operation."));
        }
        catch (HttpRequestException)
        {
            return Result<LlamaServerInfo>.Failure(new Error(
                "Connection",
                "API is unreachable. Is 'arcanum serve' running in a background terminal?"));
        }

    }

    public async Task<Result<bool>> StopLlamaServerAsync(string? cacheKey, CancellationToken cancellationToken = default)
    {

        string? apiKey = await secretStore.GetApiKeyAsync().ConfigureAwait(false);

        if (apiKey is null)
        {
            return Result<bool>.Failure(new Error(
                "Security.MissingApiKey",
                "No API key found. Run 'arcanum serve' once to generate and store a key."));
        }

        HttpClient client = httpClientFactory.CreateClient(RequestHttpClientName);

        string path = string.IsNullOrWhiteSpace(cacheKey)
            ? "api/llama/servers/stop"
            : $"api/llama/servers/{Uri.EscapeDataString(cacheKey)}/stop";

        using HttpRequestMessage request = new(HttpMethod.Post, path);

        _ = request.Headers.TryAddWithoutValidation(ArcanumApiHeaders.ApiKey, apiKey);

        try
        {
            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

            byte[] responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            ApiResponse<bool>? envelope = TryDeserialize(responseBytes, ArcanumJsonContext.Default.ApiResponseBoolean);

            if (envelope is { IsSuccess: true })
            {
                return Result<bool>.Success(true);
            }

            if (envelope is { IsSuccess: false, Error: not null })
            {
                return Result<bool>.Failure(envelope.Error.Value);
            }

            return Result<bool>.Failure(new Error("Api.HttpError", $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}"));
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

    private async Task<Result<T>> GetApiAsync<T>(
        string path,
        JsonTypeInfo<ApiResponse<T>> responseTypeInfo,
        CancellationToken cancellationToken)
    {

        string? apiKey = await secretStore.GetApiKeyAsync().ConfigureAwait(false);

        if (apiKey is null)
        {
            return Result<T>.Failure(new Error(
                "Security.MissingApiKey",
                "No API key found. Run 'arcanum serve' once to generate and store a key."));
        }

        HttpClient client = httpClientFactory.CreateClient(RequestHttpClientName);

        using HttpRequestMessage request = new(HttpMethod.Get, path);

        _ = request.Headers.TryAddWithoutValidation(ArcanumApiHeaders.ApiKey, apiKey);

        try
        {
            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

            byte[] responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            ApiResponse<T>? envelope = TryDeserialize(responseBytes, responseTypeInfo);

            if (envelope is { IsSuccess: true, Data: not null })
            {
                return Result<T>.Success(envelope.Data);
            }

            if (envelope is { IsSuccess: false, Error: not null })
            {
                return Result<T>.Failure(envelope.Error.Value);
            }

            return Result<T>.Failure(new Error("Api.HttpError", $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Result<T>.Failure(new Error(
                "Connection.Timeout",
                "The request to the Arcanum API timed out. The server may be busy with a long-running model operation."));
        }
        catch (HttpRequestException)
        {
            return Result<T>.Failure(new Error(
                "Connection",
                "API is unreachable. Is 'arcanum serve' running in a background terminal?"));
        }

    }

}
