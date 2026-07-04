using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.CommLink;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.LlamaCpp;
using RetroDownfall.Arcanum.Core.Pattern.Entities;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.ProvingGrounds;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Wards;
using RetroDownfall.Arcanum.Core.Workspaces;

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
        ErrorCodes.Security.MissingApiKey,
        "No API key found. Run 'arcanum serve' once to generate and store a key.");

    private static readonly Error RequestTimeoutError = new(
        ErrorCodes.Connection.Timeout,
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

    private static readonly MediaTypeHeaderValue JsonUtf8ContentType = new("application/json") { CharSet = "utf-8" };

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
        Func<HttpResponseMessage, byte[], ApiResponse<T>?, Result<T>> mapResponse,
        CancellationToken cancellationToken,
        string httpClientName = RequestHttpClientName)
    {
        string? apiKey = await TryGetApiKeyAsync(cancellationToken).ConfigureAwait(false);

        if (apiKey is null)
        {
            return Result<T>.Failure(MissingApiKeyError);
        }

        HttpClient client = httpClientFactory.CreateClient(httpClientName);

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

            return mapResponse(response, responseBytes, envelope);
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
        Func<ApiResponse<T>, Result<T>> mapSuccess,
        CancellationToken cancellationToken,
        string httpClientName = RequestHttpClientName)
    {
        return await SendRequestAsync(
            method,
            relativePath,
            body,
            contentType,
            responseTypeInfo,
            (response, _, envelope) =>
            {
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
            },
            cancellationToken,
            httpClientName).ConfigureAwait(false);
    }

    private async Task<Result<T>> SendRequestAsync<T>(
        HttpMethod method,
        string relativePath,
        byte[]? body,
        MediaTypeHeaderValue? contentType,
        JsonTypeInfo<ApiResponse<T>> responseTypeInfo,
        CancellationToken cancellationToken,
        string httpClientName = RequestHttpClientName)
    {
        return await SendRequestAsync(
            method,
            relativePath,
            body,
            contentType,
            responseTypeInfo,
            static envelope => Result<T>.Success(envelope.Data!),
            cancellationToken,
            httpClientName).ConfigureAwait(false);
    }

    private static string BuildQueryString(string path, params (string Key, string? Value)[] parameters)
    {
        List<string> parts = new(parameters.Length);

        foreach ((string key, string? value) in parameters)
        {
            if (!string.IsNullOrEmpty(value))
            {
                parts.Add($"{key}={Uri.EscapeDataString(value)}");
            }
        }

        return parts.Count == 0 ? path : $"{path}?{string.Join('&', parts)}";
    }

    public async Task<Result<string>> AskAsync(PingRequest body, CancellationToken cancellationToken)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(body, ArcanumJsonContext.Default.PingRequest);

        Result<PromptResponseDto> result = await SendRequestAsync(
            HttpMethod.Post,
            "api/intelligence/ping",
            json,
            JsonUtf8ContentType,
            ArcanumJsonContext.Default.ApiResponsePromptResponseDto,
            cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? Result<string>.Success(result.Value?.Text ?? string.Empty)
            : Result<string>.Failure(result.Error);
    }

    public async Task<Result<bool>> SubmitHumanResponseAsync(
        string promptId,
        string answer,
        CancellationToken cancellationToken)
    {
        SubmitHumanResponseRequest body = new(promptId, answer);

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(body, ArcanumJsonContext.Default.SubmitHumanResponseRequest);

        return await SendRequestAsync(
            HttpMethod.Post,
            "api/intelligence/human-response",
            json,
            JsonUtf8ContentType,
            ArcanumJsonContext.Default.ApiResponseBoolean,
            static (response, _, envelope) =>
            {
                if (response.IsSuccessStatusCode)
                {
                    if (envelope is null)
                    {
                        return Result<bool>.Failure(InvalidResponseError);
                    }

                    if (!envelope.IsSuccess)
                    {
                        Error err = envelope.Error ?? ApiRequestFailedError;

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
                        new Error(ErrorCodes.Intelligence.HumanPromptNotFound, "No active ask_human prompt matches that promptId."));
                }

                if (envelope is not null && envelope is { IsSuccess: false, Error: not null })
                {
                    return Result<bool>.Failure(envelope.Error.Value);
                }

                string fallback = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

                return Result<bool>.Failure(new Error("Api.HttpError", fallback));
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<string>> ReloadMcpAsync(OptionalWorkspaceRequest request, CancellationToken cancellationToken)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.OptionalWorkspaceRequest);

        return await SendRequestAsync(
            HttpMethod.Post,
            "api/mcp/reload",
            json,
            JsonUtf8ContentType,
            ArcanumJsonContext.Default.ApiResponseString,
            static envelope => Result<string>.Success(envelope.Data ?? string.Empty),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<WorkspaceArsenalDto>> GetWorkspaceArsenalAsync(OptionalWorkspaceRequest request, CancellationToken cancellationToken)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.OptionalWorkspaceRequest);

        return await SendRequestAsync(
            HttpMethod.Post,
            "api/intelligence/arsenal",
            json,
            JsonUtf8ContentType,
            ArcanumJsonContext.Default.ApiResponseWorkspaceArsenalDto,
            static envelope => envelope.Data is null
                ? Result<WorkspaceArsenalDto>.Failure(new Error("Api.InvalidResponse", "Arsenal payload was empty."))
                : Result<WorkspaceArsenalDto>.Success(envelope.Data),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<PatternSnapshot>> PerceivePatternAsync(string directory, CancellationToken cancellationToken = default)
    {
        string encoded = Uri.EscapeDataString(directory);

        return await SendRequestAsync(
            HttpMethod.Get,
            $"api/perception/look?directory={encoded}",
            null,
            null,
            ArcanumJsonContext.Default.ApiResponsePatternSnapshot,
            static envelope => envelope.Data is null
                ? Result<PatternSnapshot>.Failure(new Error("Api.InvalidResponse", "Perception payload was empty."))
                : Result<PatternSnapshot>.Success(envelope.Data),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<SessionQueryResult>> QuerySessionsAsync(
        int? limit = null,
        DateTimeOffset? beforeUpdatedAt = null,
        CancellationToken cancellationToken = default)
    {
        string query = limit is int l
            ? $"api/sessions?limit={l}"
            : "api/sessions";

        if (beforeUpdatedAt is DateTimeOffset before)
        {
            string encoded = Uri.EscapeDataString(before.ToString("O"));

            query += query.Contains('?', StringComparison.Ordinal) ? $"&beforeUpdatedAt={encoded}" : $"?beforeUpdatedAt={encoded}";
        }

        return await SendRequestAsync(
            HttpMethod.Get,
            query,
            null,
            null,
            ArcanumJsonContext.Default.ApiResponseSessionQueryResult,
            static envelope => Result<SessionQueryResult>.Success(
                envelope.Data ?? new SessionQueryResult([], null, false)),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<SessionAnalytics>> GetSessionAnalyticsAsync(CancellationToken cancellationToken = default)
    {
        return await SendRequestAsync(
            HttpMethod.Get,
            "api/sessions/analytics",
            null,
            null,
            ArcanumJsonContext.Default.ApiResponseSessionAnalytics,
            static envelope => envelope.Data is null
                ? Result<SessionAnalytics>.Failure(new Error("Api.InvalidResponse", "Session analytics payload was empty."))
                : Result<SessionAnalytics>.Success(envelope.Data),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result> RestAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        Result<bool> result = await SendRequestAsync(
            HttpMethod.Post,
            $"api/sessions/{sessionId:D}/rest",
            null,
            null,
            ArcanumJsonContext.Default.ApiResponseBoolean,
            static (response, responseBytes, boolEnvelope) =>
            {
                if (response.StatusCode == System.Net.HttpStatusCode.Accepted)
                {
                    if (boolEnvelope is { IsSuccess: true, Data: true })
                    {
                        return Result<bool>.Success(true);
                    }

                    if (boolEnvelope is not null && boolEnvelope is { IsSuccess: false, Error: not null })
                    {
                        return Result<bool>.Failure(boolEnvelope.Error.Value);
                    }

                    return Result<bool>.Failure(
                        new Error("Api.InvalidResponse", "Expected ApiResponse JSON body on 202 Accepted."));
                }

                if ((int)response.StatusCode == 404)
                {
                    if (boolEnvelope is not null && boolEnvelope is { IsSuccess: false, Error: not null })
                    {
                        return Result<bool>.Failure(boolEnvelope.Error.Value);
                    }

                    return Result<bool>.Failure(
                        new Error(ErrorCodes.Session.NotFound, "No session exists with that id."));
                }

                ApiResponse<string>? envelope = TryDeserialize(responseBytes, ArcanumJsonContext.Default.ApiResponseString);

                if (envelope is not null && envelope is { IsSuccess: false, Error: not null })
                {
                    return Result<bool>.Failure(envelope.Error.Value);
                }

                string fallback = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

                return Result<bool>.Failure(new Error("Api.HttpError", fallback));
            },
            cancellationToken).ConfigureAwait(false);

        return result.IsSuccess ? Result.Success() : Result.Failure(result.Error);
    }

    public async Task<Result<SessionDetailDto>> GetSessionAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await SendRequestAsync(
            HttpMethod.Get,
            $"api/sessions/{id:D}",
            null,
            null,
            ArcanumJsonContext.Default.ApiResponseSessionDetailDto,
            static (response, _, envelope) =>
            {
                if (response.IsSuccessStatusCode)
                {
                    if (envelope is null)
                    {
                        return Result<SessionDetailDto>.Failure(InvalidResponseError);
                    }

                    if (!envelope.IsSuccess)
                    {
                        Error err = envelope.Error ?? ApiRequestFailedError;

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
                        new Error(ErrorCodes.Session.NotFound, "No session exists with that id."));
                }

                if (envelope is not null && envelope is { IsSuccess: false, Error: not null })
                {
                    return Result<SessionDetailDto>.Failure(envelope.Error.Value);
                }

                string fallback = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

                return Result<SessionDetailDto>.Failure(new Error("Api.HttpError", fallback));
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<EntryDto[]>> GetSessionEntriesAsync(
        Guid sessionId,
        int? offset = null,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
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

        return await SendRequestAsync(
            HttpMethod.Get,
            query,
            null,
            null,
            ArcanumJsonContext.Default.ApiResponseEntryDtoArray,
            static (response, _, envelope) =>
            {
                if (response.IsSuccessStatusCode)
                {
                    if (envelope is null)
                    {
                        return Result<EntryDto[]>.Failure(InvalidResponseError);
                    }

                    if (!envelope.IsSuccess)
                    {
                        Error err = envelope.Error ?? ApiRequestFailedError;

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
                        new Error(ErrorCodes.Session.NotFound, "No session exists with that id."));
                }

                if (envelope is not null && envelope is { IsSuccess: false, Error: not null })
                {
                    return Result<EntryDto[]>.Failure(envelope.Error.Value);
                }

                string fallback = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

                return Result<EntryDto[]>.Failure(new Error("Api.HttpError", fallback));
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result> ArchiveSessionAsync(Guid id, CancellationToken cancellationToken = default)
    {
        Result<bool> result = await SendRequestAsync(
            HttpMethod.Delete,
            $"api/sessions/{id:D}",
            null,
            null,
            ArcanumJsonContext.Default.ApiResponseBoolean,
            static (response, _, envelope) =>
            {
                if ((int)response.StatusCode == 204)
                {
                    return Result<bool>.Success(true);
                }

                if ((int)response.StatusCode == 404)
                {
                    if (envelope is not null && envelope is { IsSuccess: false, Error: not null })
                    {
                        return Result<bool>.Failure(envelope.Error.Value);
                    }

                    return Result<bool>.Failure(
                        new Error(ErrorCodes.Session.NotFound, "No session exists with that id."));
                }

                if (envelope is not null && envelope is { IsSuccess: false, Error: not null })
                {
                    return Result<bool>.Failure(envelope.Error.Value);
                }

                string fallback = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

                return Result<bool>.Failure(new Error("Api.HttpError", fallback));
            },
            cancellationToken).ConfigureAwait(false);

        return result.IsSuccess ? Result.Success() : Result.Failure(result.Error);
    }

    public async Task<Result<SessionExportResult>> ExportSessionAsync(
        Guid id,
        SessionExportFormat format,
        CancellationToken cancellationToken = default)
    {
        string formatValue = format == SessionExportFormat.Markdown ? "markdown" : "json";

        return await SendRequestAsync(
            HttpMethod.Get,
            $"api/sessions/{id:D}/export?format={formatValue}",
            null,
            null,
            ArcanumJsonContext.Default.ApiResponseSessionExportResult,
            static (response, _, envelope) =>
            {
                if (response.IsSuccessStatusCode)
                {
                    if (envelope is null)
                    {
                        return Result<SessionExportResult>.Failure(InvalidResponseError);
                    }

                    if (!envelope.IsSuccess)
                    {
                        Error err = envelope.Error ?? ApiRequestFailedError;

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
                        new Error(ErrorCodes.Session.NotFound, "No session exists with that id."));
                }

                if (envelope is not null && envelope is { IsSuccess: false, Error: not null })
                {
                    return Result<SessionExportResult>.Failure(envelope.Error.Value);
                }

                string fallback = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

                return Result<SessionExportResult>.Failure(new Error("Api.HttpError", fallback));
            },
            cancellationToken).ConfigureAwait(false);
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
                    message = FormatApiError(envelope.Error.Value);
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

            Result<ListPageResult<LoreDto>> pageResult = await SendRequestAsync(
                HttpMethod.Get,
                $"api/lore?limit=1000&offset={offset}",
                null,
                null,
                ArcanumJsonContext.Default.ApiResponseListPageResultLoreDto,
                static envelope => Result<ListPageResult<LoreDto>>.Success(
                    envelope.Data ?? new ListPageResult<LoreDto>([], false)),
                cancellationToken).ConfigureAwait(false);

            if (!pageResult.IsSuccess)
            {
                return Result<List<LoreDto>>.Failure(pageResult.Error);
            }

            ListPageResult<LoreDto> page = pageResult.Value!;

            all.AddRange(page.Items);

            hasMore = page.HasMore;

            offset = page.NextOffset ?? offset + page.Items.Length;
        }
        while (hasMore);

        return Result<List<LoreDto>>.Success(all);
    }

    public async Task<Result<LoreDto>> GetLoreAsync(string key, CancellationToken cancellationToken = default)
    {
        string encoded = Uri.EscapeDataString(key);

        return await SendRequestAsync(
            HttpMethod.Get,
            $"api/lore/{encoded}",
            null,
            null,
            ArcanumJsonContext.Default.ApiResponseLoreDto,
            static (response, _, envelope) =>
            {
                if (response.IsSuccessStatusCode)
                {
                    if (envelope is null)
                    {
                        return Result<LoreDto>.Failure(InvalidResponseError);
                    }

                    if (!envelope.IsSuccess)
                    {
                        Error err = envelope.Error ?? ApiRequestFailedError;

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
                        new Error(ErrorCodes.Grimoire.LoreNotFound, "No lore exists with that key."));
                }

                if (envelope is not null && envelope is { IsSuccess: false, Error: not null })
                {
                    return Result<LoreDto>.Failure(envelope.Error.Value);
                }

                string fallback = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

                return Result<LoreDto>.Failure(new Error("Api.HttpError", fallback));
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<LoreDto>> UpsertLoreAsync(
        string key,
        string value,
        CancellationToken cancellationToken = default)
    {
        UpsertLoreRequest body = new(key, value);

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(body, ArcanumJsonContext.Default.UpsertLoreRequest);

        return await SendRequestAsync(
            HttpMethod.Post,
            "api/lore",
            json,
            JsonUtf8ContentType,
            ArcanumJsonContext.Default.ApiResponseLoreDto,
            static envelope => envelope.Data is null
                ? Result<LoreDto>.Failure(new Error("Api.InvalidResponse", "Lore payload was empty."))
                : Result<LoreDto>.Success(envelope.Data),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<bool>> DeleteLoreAsync(string key, CancellationToken cancellationToken = default)
    {
        string encoded = Uri.EscapeDataString(key);

        return await SendRequestAsync(
            HttpMethod.Delete,
            $"api/lore/{encoded}",
            null,
            null,
            ArcanumJsonContext.Default.ApiResponseBoolean,
            static (response, _, envelope) =>
            {
                if ((int)response.StatusCode == 404)
                {
                    if (envelope is not null && envelope is { IsSuccess: false, Error: not null })
                    {
                        return Result<bool>.Failure(envelope.Error.Value);
                    }

                    return Result<bool>.Failure(
                        new Error(ErrorCodes.Grimoire.LoreNotFound, "No lore exists with that key."));
                }

                if (response.IsSuccessStatusCode)
                {
                    if (envelope is null)
                    {
                        return Result<bool>.Failure(InvalidResponseError);
                    }

                    if (!envelope.IsSuccess)
                    {
                        Error err = envelope.Error ?? ApiRequestFailedError;

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
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<UnseenServantJobStatusDto[]>> GetDaemonJobsAsync(CancellationToken cancellationToken = default)
    {
        return await SendRequestAsync(
            HttpMethod.Get,
            "api/unseen-servant/jobs",
            null,
            null,
            ArcanumJsonContext.Default.ApiResponseUnseenServantJobStatusDtoArray,
            static envelope => Result<UnseenServantJobStatusDto[]>.Success(envelope.Data ?? []),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<UnseenServantJobStatusDto>> AdjustDaemonJobInitiativeAsync(
        string jobName,
        int intervalMinutes,
        CancellationToken cancellationToken = default)
    {
        string encoded = Uri.EscapeDataString(jobName);

        AdjustInitiativeRequestDto body = new(intervalMinutes);

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(body, ArcanumJsonContext.Default.AdjustInitiativeRequestDto);

        return await SendRequestAsync(
            HttpMethod.Post,
            $"api/unseen-servant/jobs/{encoded}/initiative",
            json,
            JsonUtf8ContentType,
            ArcanumJsonContext.Default.ApiResponseUnseenServantJobStatusDto,
            static envelope => envelope.Data is null
                ? Result<UnseenServantJobStatusDto>.Failure(new Error("Api.InvalidResponse", "Daemon job status payload was empty."))
                : Result<UnseenServantJobStatusDto>.Success(envelope.Data),
            cancellationToken).ConfigureAwait(false);
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
            JsonUtf8ContentType,
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
                    ? FormatApiError(envelope.Error.Value)
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

    // W4.1: include the error Code (not just the Message) when surfacing a pre-stream HTTP error
    // envelope, so the CLI shows the same "{code}: {message}" detail as in-band stream error events.
    private static string FormatApiError(Error error) =>
        string.IsNullOrEmpty(error.Code) ? error.Message : $"{error.Code}: {error.Message}";

    public async Task<Result<LlamaServerInfo>> StartLlamaServerAsync(
        string cacheKey,
        int? gpuLayers,
        int? port,
        CancellationToken cancellationToken = default)
    {
        var body = new StartLlamaServerRequestDto { GpuLayers = gpuLayers, Port = port };

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(body, ArcanumJsonContext.Default.StartLlamaServerRequestDto);

        string encodedKey = Uri.EscapeDataString(cacheKey);

        return await SendRequestAsync(
            HttpMethod.Post,
            $"api/llama/servers/{encodedKey}/start",
            json,
            JsonUtf8ContentType,
            ArcanumJsonContext.Default.ApiResponseLlamaServerInfo,
            static (response, _, envelope) =>
            {
                if (envelope is { IsSuccess: true, Data: not null })
                {
                    return Result<LlamaServerInfo>.Success(envelope.Data);
                }

                if (envelope is { IsSuccess: false, Error: not null })
                {
                    return Result<LlamaServerInfo>.Failure(envelope.Error.Value);
                }

                return Result<LlamaServerInfo>.Failure(new Error("Api.HttpError", $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}"));
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<bool>> StopLlamaServerAsync(string? cacheKey, CancellationToken cancellationToken = default)
    {
        string path = string.IsNullOrWhiteSpace(cacheKey)
            ? "api/llama/servers/stop"
            : $"api/llama/servers/{Uri.EscapeDataString(cacheKey)}/stop";

        return await SendRequestAsync(
            HttpMethod.Post,
            path,
            null,
            null,
            ArcanumJsonContext.Default.ApiResponseBoolean,
            static (response, _, envelope) =>
            {
                if (envelope is { IsSuccess: true })
                {
                    return Result<bool>.Success(true);
                }

                if (envelope is { IsSuccess: false, Error: not null })
                {
                    return Result<bool>.Failure(envelope.Error.Value);
                }

                return Result<bool>.Failure(new Error("Api.HttpError", $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}"));
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<T>> GetApiAsync<T>(
        string path,
        JsonTypeInfo<ApiResponse<T>> responseTypeInfo,
        CancellationToken cancellationToken)
    {
        return await SendRequestAsync(
            HttpMethod.Get,
            path,
            null,
            null,
            responseTypeInfo,
            static (response, _, envelope) =>
            {
                if (envelope is { IsSuccess: true, Data: not null })
                {
                    return Result<T>.Success(envelope.Data);
                }

                if (envelope is { IsSuccess: false, Error: not null })
                {
                    return Result<T>.Failure(envelope.Error.Value);
                }

                return Result<T>.Failure(new Error("Api.HttpError", $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}"));
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static Func<HttpResponseMessage, byte[], ApiResponse<bool>?, Result<bool>> NoContentOrEnvelopeError() =>
        static (response, _, envelope) =>
        {
            if ((int)response.StatusCode == 204)
            {
                return Result<bool>.Success(true);
            }

            if (envelope is not null && envelope is { IsSuccess: false, Error: not null })
            {
                return Result<bool>.Failure(envelope.Error.Value);
            }

            string fallback = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

            return Result<bool>.Failure(new Error("Api.HttpError", fallback));
        };

    private async Task<Result> DeleteReturningNoContentAsync(string relativePath, CancellationToken cancellationToken)
    {
        Result<bool> result = await SendRequestAsync(
            HttpMethod.Delete,
            relativePath,
            null,
            null,
            ArcanumJsonContext.Default.ApiResponseBoolean,
            NoContentOrEnvelopeError(),
            cancellationToken).ConfigureAwait(false);

        return result.IsSuccess ? Result.Success() : Result.Failure(result.Error);
    }

    #region Campaign (The Forge)

    public async Task<Result<ListPageResult<CampaignDto>>> GetCampaignsAsync(
        WorkspaceType? type = null,
        CancellationToken cancellationToken = default)
    {
        string path = BuildQueryString("api/campaigns", ("type", type?.ToString()));

        return await SendRequestAsync(
            HttpMethod.Get,
            path,
            null,
            null,
            ArcanumJsonContext.Default.ApiResponseListPageResultCampaignDto,
            static envelope => Result<ListPageResult<CampaignDto>>.Success(
                envelope.Data ?? new ListPageResult<CampaignDto>([], false)),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<CampaignDto>> GetCampaignAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await SendRequestAsync(
            HttpMethod.Get,
            $"api/campaigns/{id:D}",
            null,
            null,
            ArcanumJsonContext.Default.ApiResponseCampaignDto,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<CampaignDto>> CreateCampaignAsync(
        RegisterCampaignRequest request,
        CancellationToken cancellationToken = default)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.RegisterCampaignRequest);

        return await SendRequestAsync(
            HttpMethod.Post,
            "api/campaigns",
            json,
            JsonUtf8ContentType,
            ArcanumJsonContext.Default.ApiResponseCampaignDto,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<CampaignDto>> UpdateCampaignAsync(
        Guid id,
        UpdateCampaignRequest request,
        CancellationToken cancellationToken = default)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.UpdateCampaignRequest);

        return await SendRequestAsync(
            HttpMethod.Put,
            $"api/campaigns/{id:D}",
            json,
            JsonUtf8ContentType,
            ArcanumJsonContext.Default.ApiResponseCampaignDto,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result> DeleteCampaignAsync(Guid id, CancellationToken cancellationToken = default) =>
        await DeleteReturningNoContentAsync($"api/campaigns/{id:D}", cancellationToken).ConfigureAwait(false);

    public async Task<Result<CampaignExportDto>> ExportCampaignAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await SendRequestAsync(
            HttpMethod.Post,
            $"api/campaigns/{id:D}/export",
            null,
            null,
            ArcanumJsonContext.Default.ApiResponseCampaignExportDto,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<CampaignImportResultDto>> ImportCampaignAsync(
        Guid id,
        CampaignImportRequest request,
        CancellationToken cancellationToken = default)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.CampaignImportRequest);

        return await SendRequestAsync(
            HttpMethod.Post,
            $"api/campaigns/{id:D}/import",
            json,
            JsonUtf8ContentType,
            ArcanumJsonContext.Default.ApiResponseCampaignImportResultDto,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<CodexContentDto>> GetCampaignCodexAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await SendRequestAsync(
            HttpMethod.Get,
            $"api/campaigns/{id:D}/codex",
            null,
            null,
            ArcanumJsonContext.Default.ApiResponseCodexContentDto,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<CodexContentDto>> PutCampaignCodexAsync(
        Guid id,
        string content,
        CancellationToken cancellationToken = default)
    {
        CodexPutRequest request = new(content);

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.CodexPutRequest);

        return await SendRequestAsync(
            HttpMethod.Put,
            $"api/campaigns/{id:D}/codex",
            json,
            JsonUtf8ContentType,
            ArcanumJsonContext.Default.ApiResponseCodexContentDto,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result> DeleteCampaignCodexAsync(Guid id, CancellationToken cancellationToken = default) =>
        await DeleteReturningNoContentAsync($"api/campaigns/{id:D}/codex", cancellationToken).ConfigureAwait(false);

    public async Task<Result<SpellSummary[]>> GetCampaignSpellsAsync(
        Guid campaignId,
        string? q = null,
        string? tag = null,
        string? tool = null,
        CancellationToken cancellationToken = default)
    {
        string path = BuildQueryString(
            $"api/campaigns/{campaignId:D}/spells",
            ("q", q),
            ("tag", tag),
            ("tool", tool));

        return await SendRequestAsync(
            HttpMethod.Get,
            path,
            null,
            null,
            ArcanumJsonContext.Default.ApiResponseSpellSummaryArray,
            static envelope => Result<SpellSummary[]>.Success(envelope.Data ?? []),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<ListPageResult<PromptSummaryDto>>> GetCampaignPromptsAsync(
        Guid campaignId,
        string? q = null,
        string? tag = null,
        CancellationToken cancellationToken = default)
    {
        string path = BuildQueryString(
            $"api/campaigns/{campaignId:D}/prompts",
            ("q", q),
            ("tag", tag));

        return await SendRequestAsync(
            HttpMethod.Get,
            path,
            null,
            null,
            ArcanumJsonContext.Default.ApiResponseListPageResultPromptSummaryDto,
            static envelope => Result<ListPageResult<PromptSummaryDto>>.Success(
                envelope.Data ?? new ListPageResult<PromptSummaryDto>([], false)),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<SessionQueryResult>> GetCampaignSessionsAsync(
        Guid campaignId,
        string? status = null,
        string? search = null,
        int? limit = null,
        DateTimeOffset? beforeUpdatedAt = null,
        CancellationToken cancellationToken = default)
    {
        string path = BuildQueryString(
            $"api/campaigns/{campaignId:D}/sessions",
            ("status", status),
            ("search", search),
            ("limit", limit?.ToString(CultureInfo.InvariantCulture)),
            ("beforeUpdatedAt", beforeUpdatedAt?.ToString("O")));

        return await SendRequestAsync(
            HttpMethod.Get,
            path,
            null,
            null,
            ArcanumJsonContext.Default.ApiResponseSessionQueryResult,
            static envelope => Result<SessionQueryResult>.Success(
                envelope.Data ?? new SessionQueryResult([], null, false)),
            cancellationToken).ConfigureAwait(false);
    }

    #endregion

    #region Configuration (Models and Providers)

    public async Task<Result<ModelInfoDto[]>> GetModelsAsync(CancellationToken cancellationToken = default)
    {
        return await SendRequestAsync(
            HttpMethod.Get,
            "api/models",
            null,
            null,
            ArcanumJsonContext.Default.ApiResponseModelInfoDtoArray,
            static envelope => Result<ModelInfoDto[]>.Success(envelope.Data ?? []),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<ProviderInfoDto[]>> GetProvidersAsync(CancellationToken cancellationToken = default)
    {
        return await SendRequestAsync(
            HttpMethod.Get,
            "api/providers",
            null,
            null,
            ArcanumJsonContext.Default.ApiResponseProviderInfoDtoArray,
            static envelope => Result<ProviderInfoDto[]>.Success(envelope.Data ?? []),
            cancellationToken).ConfigureAwait(false);
    }

    #endregion

    #region Spell (The Forge)

    public async Task<Result<SpellSummary[]>> GetSpellsAsync(
        string? workspace = null,
        CancellationToken cancellationToken = default)
    {
        string path = BuildQueryString("api/spells", ("workspace", workspace));

        return await SendRequestAsync(
            HttpMethod.Get,
            path,
            null,
            null,
            ArcanumJsonContext.Default.ApiResponseSpellSummaryArray,
            static envelope => Result<SpellSummary[]>.Success(envelope.Data ?? []),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<SpellDetail>> GetSpellAsync(
        string name,
        string? workspace = null,
        CancellationToken cancellationToken = default)
    {
        string path = BuildQueryString($"api/spells/{Uri.EscapeDataString(name)}", ("workspace", workspace));

        return await SendRequestAsync(
            HttpMethod.Get,
            path,
            null,
            null,
            ArcanumJsonContext.Default.ApiResponseSpellDetail,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<bool>> CreateSpellAsync(
        CreateSpellRequest request,
        string workspace,
        CancellationToken cancellationToken = default)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.CreateSpellRequest);

        string path = BuildQueryString("api/spells", ("workspace", workspace));

        return await SendRequestAsync(
            HttpMethod.Post,
            path,
            json,
            JsonUtf8ContentType,
            ArcanumJsonContext.Default.ApiResponseBoolean,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<bool>> UpdateSpellAsync(
        string name,
        UpdateSpellRequest request,
        string workspace,
        CancellationToken cancellationToken = default)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.UpdateSpellRequest);

        string path = BuildQueryString($"api/spells/{Uri.EscapeDataString(name)}", ("workspace", workspace));

        return await SendRequestAsync(
            HttpMethod.Put,
            path,
            json,
            JsonUtf8ContentType,
            ArcanumJsonContext.Default.ApiResponseBoolean,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result> DeleteSpellAsync(
        string name,
        string workspace,
        CancellationToken cancellationToken = default)
    {
        string path = BuildQueryString($"api/spells/{Uri.EscapeDataString(name)}", ("workspace", workspace));

        return await DeleteReturningNoContentAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<SpellSummary[]>> SearchSpellsAsync(
        string? q = null,
        string? tag = null,
        string? tool = null,
        SpellSource? source = null,
        Guid? campaignId = null,
        string? workspace = null,
        CancellationToken cancellationToken = default)
    {
        string path = BuildQueryString(
            "api/spells/search",
            ("q", q),
            ("tag", tag),
            ("tool", tool),
            ("source", source?.ToString()),
            ("campaignId", campaignId?.ToString("D")),
            ("workspace", workspace));

        return await SendRequestAsync(
            HttpMethod.Get,
            path,
            null,
            null,
            ArcanumJsonContext.Default.ApiResponseSpellSummaryArray,
            static envelope => Result<SpellSummary[]>.Success(envelope.Data ?? []),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<SpellValidationResultDto>> ValidateSpellAsync(
        string name,
        string? workspace = null,
        CancellationToken cancellationToken = default)
    {
        string path = BuildQueryString($"api/spells/{Uri.EscapeDataString(name)}/validate", ("workspace", workspace));

        return await SendRequestAsync(
            HttpMethod.Post,
            path,
            null,
            null,
            ArcanumJsonContext.Default.ApiResponseSpellValidationResultDto,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<PromptResponseDto>> ExecuteSpellAsync(
        string name,
        SpellExecuteRequest request,
        string? workspace = null,
        string? version = null,
        CancellationToken cancellationToken = default)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.SpellExecuteRequest);

        string path = BuildQueryString(
            $"api/spells/{Uri.EscapeDataString(name)}/execute",
            ("workspace", workspace),
            ("version", version));

        return await SendRequestAsync(
            HttpMethod.Post,
            path,
            json,
            JsonUtf8ContentType,
            ArcanumJsonContext.Default.ApiResponsePromptResponseDto,
            cancellationToken,
            StreamingHttpClientName).ConfigureAwait(false);
    }

    public async Task<Result<SpellVersionDto[]>> GetSpellVersionsAsync(
        string name,
        string? workspace = null,
        Guid? campaignId = null,
        CancellationToken cancellationToken = default)
    {
        string path = BuildQueryString(
            $"api/spells/{Uri.EscapeDataString(name)}/versions",
            ("workspace", workspace),
            ("campaignId", campaignId?.ToString("D")));

        return await SendRequestAsync(
            HttpMethod.Get,
            path,
            null,
            null,
            ArcanumJsonContext.Default.ApiResponseSpellVersionDtoArray,
            static envelope => Result<SpellVersionDto[]>.Success(envelope.Data ?? []),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<SpellExportDto>> ExportSpellAsync(
        string name,
        string? workspace = null,
        CancellationToken cancellationToken = default)
    {
        string path = BuildQueryString($"api/spells/{Uri.EscapeDataString(name)}/export", ("workspace", workspace));

        return await SendRequestAsync(
            HttpMethod.Post,
            path,
            null,
            null,
            ArcanumJsonContext.Default.ApiResponseSpellExportDto,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<SpellSummary>> ImportSpellAsync(
        SpellImportRequest request,
        CancellationToken cancellationToken = default)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.SpellImportRequest);

        return await SendRequestAsync(
            HttpMethod.Post,
            "api/spells/import",
            json,
            JsonUtf8ContentType,
            ArcanumJsonContext.Default.ApiResponseSpellSummary,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<SpellCastResult>> CastSpellAsync(
        string name,
        SpellCastRequest request,
        CancellationToken cancellationToken = default)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.SpellCastRequest);

        return await SendRequestAsync(
            HttpMethod.Post,
            $"api/spells/{Uri.EscapeDataString(name)}/cast",
            json,
            JsonUtf8ContentType,
            ArcanumJsonContext.Default.ApiResponseSpellCastResult,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<SpellSummary>> CloneSpellAsync(
        string name,
        CloneSpellRequest request,
        CancellationToken cancellationToken = default)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.CloneSpellRequest);

        return await SendRequestAsync(
            HttpMethod.Post,
            $"api/spells/{Uri.EscapeDataString(name)}/clone",
            json,
            JsonUtf8ContentType,
            ArcanumJsonContext.Default.ApiResponseSpellSummary,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<SpellVersionDto>> CreateSpellVersionAsync(
        string name,
        CreateSpellVersionRequest request,
        CancellationToken cancellationToken = default)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.CreateSpellVersionRequest);

        return await SendRequestAsync(
            HttpMethod.Post,
            $"api/spells/{Uri.EscapeDataString(name)}/versions",
            json,
            JsonUtf8ContentType,
            ArcanumJsonContext.Default.ApiResponseSpellVersionDto,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<SpellVersionDto>> UpdateSpellVersionAsync(
        string name,
        string version,
        UpdateSpellVersionRequest request,
        CancellationToken cancellationToken = default)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.UpdateSpellVersionRequest);

        return await SendRequestAsync(
            HttpMethod.Put,
            $"api/spells/{Uri.EscapeDataString(name)}/versions/{Uri.EscapeDataString(version)}",
            json,
            JsonUtf8ContentType,
            ArcanumJsonContext.Default.ApiResponseSpellVersionDto,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<SpellVersionDto>> ActivateSpellVersionAsync(
        string name,
        string version,
        ActivateSpellVersionRequest request,
        CancellationToken cancellationToken = default)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.ActivateSpellVersionRequest);

        return await SendRequestAsync(
            HttpMethod.Post,
            $"api/spells/{Uri.EscapeDataString(name)}/versions/{Uri.EscapeDataString(version)}/activate",
            json,
            JsonUtf8ContentType,
            ArcanumJsonContext.Default.ApiResponseSpellVersionDto,
            cancellationToken).ConfigureAwait(false);
    }

    #endregion

    #region Prompt (The Forge)

    public async Task<Result<ListPageResult<PromptSummaryDto>>> GetPromptsAsync(
        Guid? campaignId = null,
        string? q = null,
        string? tag = null,
        int? limit = null,
        int? offset = null,
        CancellationToken cancellationToken = default)
    {
        string path = BuildQueryString(
            "api/prompts",
            ("campaignId", campaignId?.ToString("D")),
            ("q", q),
            ("tag", tag),
            ("limit", limit?.ToString(CultureInfo.InvariantCulture)),
            ("offset", offset?.ToString(CultureInfo.InvariantCulture)));

        return await SendRequestAsync(
            HttpMethod.Get,
            path,
            null,
            null,
            ArcanumJsonContext.Default.ApiResponseListPageResultPromptSummaryDto,
            static envelope => Result<ListPageResult<PromptSummaryDto>>.Success(
                envelope.Data ?? new ListPageResult<PromptSummaryDto>([], false)),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<PromptDetailDto>> GetPromptAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await SendRequestAsync(
            HttpMethod.Get,
            $"api/prompts/{id:D}",
            null,
            null,
            ArcanumJsonContext.Default.ApiResponsePromptDetailDto,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<PromptVersionDto[]>> GetPromptVersionsByNameAsync(
        string name,
        Guid? campaignId = null,
        CancellationToken cancellationToken = default)
    {
        string path = BuildQueryString(
            $"api/prompts/by-name/{Uri.EscapeDataString(name)}/versions",
            ("campaignId", campaignId?.ToString("D")));

        return await SendRequestAsync(
            HttpMethod.Get,
            path,
            null,
            null,
            ArcanumJsonContext.Default.ApiResponsePromptVersionDtoArray,
            static envelope => Result<PromptVersionDto[]>.Success(envelope.Data ?? []),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<PromptDetailDto>> CreatePromptAsync(
        CreatePromptRequest request,
        CancellationToken cancellationToken = default)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.CreatePromptRequest);

        return await SendRequestAsync(
            HttpMethod.Post,
            "api/prompts",
            json,
            JsonUtf8ContentType,
            ArcanumJsonContext.Default.ApiResponsePromptDetailDto,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<PromptDetailDto>> UpdatePromptAsync(
        Guid id,
        UpdatePromptRequest request,
        CancellationToken cancellationToken = default)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.UpdatePromptRequest);

        return await SendRequestAsync(
            HttpMethod.Put,
            $"api/prompts/{id:D}",
            json,
            JsonUtf8ContentType,
            ArcanumJsonContext.Default.ApiResponsePromptDetailDto,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result> DeletePromptAsync(Guid id, CancellationToken cancellationToken = default) =>
        await DeleteReturningNoContentAsync($"api/prompts/{id:D}", cancellationToken).ConfigureAwait(false);

    public async Task<Result<PromptRenderResultDto>> RenderPromptAsync(
        Guid id,
        Dictionary<string, string>? parameters,
        CancellationToken cancellationToken = default)
    {
        PromptRenderRequest request = new(parameters);

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.PromptRenderRequest);

        return await SendRequestAsync(
            HttpMethod.Post,
            $"api/prompts/{id:D}/render",
            json,
            JsonUtf8ContentType,
            ArcanumJsonContext.Default.ApiResponsePromptRenderResultDto,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<PromptTestResultDto>> TestPromptAsync(
        Guid id,
        TestPromptRequest request,
        CancellationToken cancellationToken = default)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.TestPromptRequest);

        return await SendRequestAsync(
            HttpMethod.Post,
            $"api/prompts/{id:D}/test",
            json,
            JsonUtf8ContentType,
            ArcanumJsonContext.Default.ApiResponsePromptTestResultDto,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<PromptResponseDto>> ExecutePromptAsync(
        Guid id,
        PromptExecuteRequest request,
        string? workspace = null,
        CancellationToken cancellationToken = default)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.PromptExecuteRequest);

        string path = BuildQueryString($"api/prompts/{id:D}/execute", ("workspace", workspace));

        return await SendRequestAsync(
            HttpMethod.Post,
            path,
            json,
            JsonUtf8ContentType,
            ArcanumJsonContext.Default.ApiResponsePromptResponseDto,
            cancellationToken,
            StreamingHttpClientName).ConfigureAwait(false);
    }

    public async Task<Result<PromptExportDto>> ExportPromptAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await SendRequestAsync(
            HttpMethod.Post,
            $"api/prompts/{id:D}/export",
            null,
            null,
            ArcanumJsonContext.Default.ApiResponsePromptExportDto,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<PromptSummaryDto>> ImportPromptAsync(
        PromptImportRequest request,
        CancellationToken cancellationToken = default)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.PromptImportRequest);

        return await SendRequestAsync(
            HttpMethod.Post,
            "api/prompts/import",
            json,
            JsonUtf8ContentType,
            ArcanumJsonContext.Default.ApiResponsePromptSummaryDto,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<PromptDetailDto>> ClonePromptAsync(
        Guid id,
        ClonePromptRequest request,
        CancellationToken cancellationToken = default)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.ClonePromptRequest);

        return await SendRequestAsync(
            HttpMethod.Post,
            $"api/prompts/{id:D}/clone",
            json,
            JsonUtf8ContentType,
            ArcanumJsonContext.Default.ApiResponsePromptDetailDto,
            cancellationToken).ConfigureAwait(false);
    }

    #endregion

    #region Ward

    public async Task<Result<WardDto[]>> GetWardsAsync(CancellationToken cancellationToken = default)
    {
        return await SendRequestAsync(
            HttpMethod.Get,
            "api/wards",
            null,
            null,
            ArcanumJsonContext.Default.ApiResponseWardDtoArray,
            static envelope => Result<WardDto[]>.Success(envelope.Data ?? []),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<WardDto>> GetWardAsync(string id, CancellationToken cancellationToken = default)
    {
        return await SendRequestAsync(
            HttpMethod.Get,
            $"api/wards/{Uri.EscapeDataString(id)}",
            null,
            null,
            ArcanumJsonContext.Default.ApiResponseWardDto,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<WardResolutionDto>> ResolveWardAsync(
        string id,
        bool allow,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        ResolveWardRequest request = new(allow, reason);

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.ResolveWardRequest);

        return await SendRequestAsync(
            HttpMethod.Post,
            $"api/wards/{Uri.EscapeDataString(id)}",
            json,
            JsonUtf8ContentType,
            ArcanumJsonContext.Default.ApiResponseWardResolutionDto,
            cancellationToken).ConfigureAwait(false);
    }

    #endregion

    #region The Proving Grounds

    public async Task<Result<TrialResult>> RunTrialAsync(Trial trial, CancellationToken cancellationToken = default)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(trial, ArcanumJsonContext.Default.Trial);

        return await SendRequestAsync(
            HttpMethod.Post,
            "api/proving-grounds/trials/run",
            json,
            JsonUtf8ContentType,
            ArcanumJsonContext.Default.ApiResponseTrialResult,
            cancellationToken,
            StreamingHttpClientName).ConfigureAwait(false);
    }

    #endregion

    #region Apprentice (The Forge)

    public async Task<Result<ListPageResult<ApprenticeSummaryDto>>> GetApprenticesAsync(
        Guid? campaignId = null,
        string? status = null,
        int? limit = null,
        DateTimeOffset? beforeUpdatedAt = null,
        CancellationToken cancellationToken = default)
    {
        string path = BuildQueryString(
            "api/apprentices",
            ("campaignId", campaignId?.ToString("D")),
            ("status", status),
            ("limit", limit?.ToString(CultureInfo.InvariantCulture)),
            ("beforeUpdatedAt", beforeUpdatedAt?.ToString("O")));

        return await SendRequestAsync(
            HttpMethod.Get,
            path,
            null,
            null,
            ArcanumJsonContext.Default.ApiResponseListPageResultApprenticeSummaryDto,
            static envelope => Result<ListPageResult<ApprenticeSummaryDto>>.Success(
                envelope.Data ?? new ListPageResult<ApprenticeSummaryDto>([], false)),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<ApprenticeDetailDto>> GetApprenticeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await SendRequestAsync(
            HttpMethod.Get,
            $"api/apprentices/{id:D}",
            null,
            null,
            ArcanumJsonContext.Default.ApiResponseApprenticeDetailDto,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<ApprenticeDetailDto>> CreateApprenticeAsync(
        CreateApprenticeRequest request,
        CancellationToken cancellationToken = default)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.CreateApprenticeRequest);

        return await SendRequestAsync(
            HttpMethod.Post,
            "api/apprentices",
            json,
            JsonUtf8ContentType,
            ArcanumJsonContext.Default.ApiResponseApprenticeDetailDto,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result> DeleteApprenticeAsync(Guid id, CancellationToken cancellationToken = default) =>
        await DeleteReturningNoContentAsync($"api/apprentices/{id:D}", cancellationToken).ConfigureAwait(false);

    private async Task<Result<string>> PostApprenticeLifecycleAsync(
        Guid id,
        string action,
        CancellationToken cancellationToken)
    {
        return await SendRequestAsync(
            HttpMethod.Post,
            $"api/apprentices/{id:D}/{action}",
            null,
            null,
            ArcanumJsonContext.Default.ApiResponseString,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<string>> StartApprenticeAsync(Guid id, CancellationToken cancellationToken = default) =>
        await PostApprenticeLifecycleAsync(id, "start", cancellationToken).ConfigureAwait(false);

    public async Task<Result<string>> PauseApprenticeAsync(Guid id, CancellationToken cancellationToken = default) =>
        await PostApprenticeLifecycleAsync(id, "pause", cancellationToken).ConfigureAwait(false);

    public async Task<Result<string>> ResumeApprenticeAsync(Guid id, CancellationToken cancellationToken = default) =>
        await PostApprenticeLifecycleAsync(id, "resume", cancellationToken).ConfigureAwait(false);

    public async Task<Result<string>> CancelApprenticeAsync(Guid id, CancellationToken cancellationToken = default) =>
        await PostApprenticeLifecycleAsync(id, "cancel", cancellationToken).ConfigureAwait(false);

    public async Task<Result<ApprenticeDetailDto>> ReweaveApprenticeAsync(
        Guid id,
        IReadOnlyList<PlanStep> steps,
        CancellationToken cancellationToken = default)
    {
        ReweaveApprenticeRequest request = new(steps);

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.ReweaveApprenticeRequest);

        return await SendRequestAsync(
            HttpMethod.Post,
            $"api/apprentices/{id:D}/reweave",
            json,
            JsonUtf8ContentType,
            ArcanumJsonContext.Default.ApiResponseApprenticeDetailDto,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<string>> IntervereApprenticeAsync(
        Guid id,
        string guidance,
        CancellationToken cancellationToken = default)
    {
        InterveneApprenticeRequest request = new(guidance);

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.InterveneApprenticeRequest);

        return await SendRequestAsync(
            HttpMethod.Post,
            $"api/apprentices/{id:D}/intervene",
            json,
            JsonUtf8ContentType,
            ArcanumJsonContext.Default.ApiResponseString,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<ApprenticeDetailDto>> CastApprenticeAsync(
        Guid id,
        string goal,
        string? name,
        CancellationToken cancellationToken = default)
    {
        CastApprenticeRequest request = new(goal, name);

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.CastApprenticeRequest);

        return await SendRequestAsync(
            HttpMethod.Post,
            $"api/apprentices/{id:D}/cast",
            json,
            JsonUtf8ContentType,
            ArcanumJsonContext.Default.ApiResponseApprenticeDetailDto,
            cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<ChronicleFrame> StreamApprenticeChronicleAsync(
        Guid id,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string? apiKey = await secretStore.GetApiKeyAsync().ConfigureAwait(false);

        if (apiKey is null)
        {
            yield return new ChronicleFrame(
                "error",
                null,
                "No API key found. Run 'arcanum serve' once to generate and store a key.");

            yield break;
        }

        HttpClient client = httpClientFactory.CreateClient(StreamingHttpClientName);

        using HttpRequestMessage request = new(HttpMethod.Get, $"api/apprentices/{id:D}/chronicle");

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
            yield return new ChronicleFrame(
                "error",
                null,
                sendErrorMessage ?? "API is unreachable. Is 'arcanum serve' running in a background terminal?");

            yield break;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                byte[] responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

                ApiResponse<string>? envelope = TryDeserialize(responseBytes, ArcanumJsonContext.Default.ApiResponseString);

                string message = envelope is { IsSuccess: false, Error: not null }
                    ? FormatApiError(envelope.Error.Value)
                    : $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

                yield return new ChronicleFrame("error", null, message);

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
                yield return new ChronicleFrame("error", null, openStreamError);

                yield break;
            }

            await using (responseStream!)
            {
                using StreamReader lineReader = new(responseStream!, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);

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
                        yield return new ChronicleFrame("error", null, readError);

                        yield break;
                    }

                    if (line is null)
                    {
                        break;
                    }

                    if (line.Length == 0 || line[0] == ':')
                    {
                        continue;
                    }

                    if (!line.StartsWith("data:", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    string data = line[5..].TrimStart();

                    if (string.Equals(data, "[DONE]", StringComparison.Ordinal))
                    {
                        yield break;
                    }

                    ChronicleFrame? frame = TryParseChronicleFrame(data);

                    if (frame is not null)
                    {
                        yield return frame;
                    }
                }
            }
        }
    }

    private static readonly string[] ChronicleMessageProperties =
    [
        "message",
        "description",
        "result",
        "error",
        "summary",
    ];

    private static ChronicleFrame? TryParseChronicleFrame(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);

            JsonElement root = document.RootElement;

            string type = root.TryGetProperty("type", out JsonElement typeElement) && typeElement.ValueKind == JsonValueKind.String
                ? typeElement.GetString() ?? "unknown"
                : "unknown";

            DateTimeOffset? timestamp = root.TryGetProperty("timestamp", out JsonElement timestampElement)
                && timestampElement.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(
                    timestampElement.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out DateTimeOffset parsed)
                    ? parsed
                    : null;

            string message = type;

            foreach (string propertyName in ChronicleMessageProperties)
            {
                if (root.TryGetProperty(propertyName, out JsonElement valueElement)
                    && valueElement.ValueKind == JsonValueKind.String)
                {
                    string? value = valueElement.GetString();

                    if (!string.IsNullOrEmpty(value))
                    {
                        message = value;

                        break;
                    }
                }
            }

            return new ChronicleFrame(type, timestamp, message);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    #endregion

}

public sealed record ChronicleFrame(string Type, DateTimeOffset? Timestamp, string Message);
