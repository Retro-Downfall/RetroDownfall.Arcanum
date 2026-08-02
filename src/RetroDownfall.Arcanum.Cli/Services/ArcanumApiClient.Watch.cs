using System.Net;

using System.Net.Http.Headers;

using System.Runtime.CompilerServices;

using System.Text;

using RetroDownfall.Arcanum.Api.Models;

using RetroDownfall.Arcanum.Api.Security;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Cli.Services;

public sealed partial class ArcanumApiClient
{

    public async Task<Result<HealthReportDto>> GetHealthReportAsync(
        CancellationToken cancellationToken = default)
    {

        HealthWatchResult observation = await GetHealthWatchReportAsync(
                cancellationToken)
            .ConfigureAwait(false);

        return observation.Report;

    }

    internal async Task<HealthWatchResult> GetHealthWatchReportAsync(
        CancellationToken cancellationToken = default)
    {

        HttpStatusCode? responseStatus = null;

        Result<HealthReportDto> report = await SendRequestAsync(
            HttpMethod.Get,
            "api/health",
            body: null,
            contentType: null,
            ArcanumJsonContext.Default.ApiResponseHealthReportDto,
            (response, _, envelope) =>
            {

                responseStatus = response.StatusCode;

                return MapHealthReport(response, envelope);

            },
            cancellationToken).ConfigureAwait(false);

        bool retryable = report.IsFailure
            && (responseStatus is { } status
                ? IsRetryableStatusCode(status)
                : IsRetryableTransportError(report.Error));

        return new HealthWatchResult(report, retryable);

    }

    internal IAsyncEnumerable<WatchSseFrame> WatchSseAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        WatchSseAsync(
            path,
            WatchSseParser.IdleDiagnosticInterval,
            static (delay, token) => Task.Delay(delay, token),
            cancellationToken);

    internal async IAsyncEnumerable<WatchSseFrame> WatchSseAsync(
        string path,
        TimeSpan diagnosticInterval,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {

        ValidateWatchPath(path);

        ArgumentNullException.ThrowIfNull(delayAsync);

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            diagnosticInterval,
            TimeSpan.Zero);

        string? apiKey = await TryGetApiKeyAsync(cancellationToken)
            .ConfigureAwait(false);

        if (apiKey is null)
        {

            yield return new WatchSseFrame(
                WatchSseFrameType.Error,
                Error: MissingApiKeyError);

            yield break;

        }

        HttpClient client = httpClientFactory.CreateClient(
            StreamingHttpClientName);

        using HttpRequestMessage request = new(HttpMethod.Get, path);

        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("text/event-stream"));

        _ = request.Headers.TryAddWithoutValidation(
            ArcanumApiHeaders.ApiKey,
            apiKey);

        HttpResponseMessage? response = null;

        Task<HttpResponseMessage>? sendTask = null;

        Error? sendError = null;

        bool sendRetryable = false;

        try
        {

            sendTask = client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {

            throw;

        }
        catch (OperationCanceledException)
        {

            sendError = RequestTimeoutError;

            sendRetryable = true;

        }
        catch (HttpRequestException)
        {

            sendError = RequestUnreachableError;

            sendRetryable = true;

        }
        catch (IOException)
        {

            sendError = RequestDisconnectedError;

            sendRetryable = true;

        }
        catch (Exception)
        {

            sendError = RequestUnexpectedError;

        }

        if (sendTask is not null)
        {

            while (!sendTask.IsCompleted)
            {

                bool reportWaiting = await WaitForOperationOrDiagnosticAsync(
                        sendTask,
                        diagnosticInterval,
                        delayAsync,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (reportWaiting)
                {

                    yield return new WatchSseFrame(
                        WatchSseFrameType.Heartbeat,
                        Diagnostic: "Still waiting for Arcanum API response headers; the watch remains active.");

                }

            }

            try
            {

                response = await sendTask.ConfigureAwait(false);

            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {

                throw;

            }
            catch (OperationCanceledException)
            {

                sendError = RequestTimeoutError;

                sendRetryable = true;

            }
            catch (HttpRequestException)
            {

                sendError = RequestUnreachableError;

                sendRetryable = true;

            }
            catch (IOException)
            {

                sendError = RequestDisconnectedError;

                sendRetryable = true;

            }
            catch (Exception)
            {

                sendError = RequestUnexpectedError;

            }

        }

        if (sendError is not null || response is null)
        {

            yield return new WatchSseFrame(
                WatchSseFrameType.Error,
                Error: sendError ?? RequestUnreachableError,
                Retryable: sendError is null || sendRetryable);

            yield break;

        }

        using (response)
        {

            if (!response.IsSuccessStatusCode)
            {

                Task<(Error Error, bool Retryable)> errorTask =
                    ReadStreamErrorAsync(
                        response,
                        cancellationToken);

                while (!errorTask.IsCompleted)
                {

                    bool reportWaiting = await WaitForOperationOrDiagnosticAsync(
                            errorTask,
                            diagnosticInterval,
                            delayAsync,
                            cancellationToken)
                        .ConfigureAwait(false);

                    if (reportWaiting)
                    {

                        yield return new WatchSseFrame(
                            WatchSseFrameType.Heartbeat,
                            Diagnostic: "The API returned an error status; still waiting for its error details while the watch remains active.");

                    }

                }

                (Error error, bool retryable) = await errorTask
                    .ConfigureAwait(false);

                yield return new WatchSseFrame(
                    WatchSseFrameType.Error,
                    Error: error,
                    Retryable: retryable);

                yield break;

            }

            Stream? stream = null;

            Task<Stream>? openTask = null;

            Error? openError = null;

            bool openRetryable = false;

            try
            {

                openTask = response.Content.ReadAsStreamAsync(
                    cancellationToken);

            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {

                throw;

            }
            catch (OperationCanceledException)
            {

                openError = RequestTimeoutError;

                openRetryable = true;

            }
            catch (HttpRequestException)
            {

                openError = RequestUnreachableError;

                openRetryable = true;

            }
            catch (IOException)
            {

                openError = RequestDisconnectedError;

                openRetryable = true;

            }
            catch (Exception)
            {

                openError = RequestUnexpectedError;

            }

            if (openTask is not null)
            {

                while (!openTask.IsCompleted)
                {

                    bool reportWaiting = await WaitForOperationOrDiagnosticAsync(
                            openTask,
                            diagnosticInterval,
                            delayAsync,
                            cancellationToken)
                        .ConfigureAwait(false);

                    if (reportWaiting)
                    {

                        yield return new WatchSseFrame(
                            WatchSseFrameType.Heartbeat,
                            Diagnostic: "The API responded, but its response stream is not available yet; the watch remains active.");

                    }

                }

                try
                {

                    stream = await openTask.ConfigureAwait(false);

                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {

                    throw;

                }
                catch (OperationCanceledException)
                {

                    openError = RequestTimeoutError;

                    openRetryable = true;

                }
                catch (HttpRequestException)
                {

                    openError = RequestUnreachableError;

                    openRetryable = true;

                }
                catch (IOException)
                {

                    openError = RequestDisconnectedError;

                    openRetryable = true;

                }
                catch (Exception)
                {

                    openError = RequestUnexpectedError;

                }

            }

            if (openError is not null || stream is null)
            {

                yield return new WatchSseFrame(
                    WatchSseFrameType.Error,
                    Error: openError ?? RequestDisconnectedError,
                    Retryable: openError is null || openRetryable);

                yield break;

            }

            await using (stream.ConfigureAwait(false))
            {

                using StreamReader reader = new(
                    stream,
                    new UTF8Encoding(
                        encoderShouldEmitUTF8Identifier: false,
                        throwOnInvalidBytes: true),
                    detectEncodingFromByteOrderMarks: true,
                    bufferSize: 4096,
                    leaveOpen: true);

                await using IAsyncEnumerator<WatchSseFrame> frames =
                    WatchSseParser
                        .ParseAsync(
                            reader,
                            diagnosticInterval,
                            delayAsync,
                            cancellationToken)
                        .GetAsyncEnumerator(cancellationToken);

                while (true)
                {

                    bool hasFrame = false;

                    Error? readError = null;

                    bool readRetryable = false;

                    try
                    {

                        hasFrame = await frames
                            .MoveNextAsync()
                            .ConfigureAwait(false);

                    }
                    catch (OperationCanceledException)
                        when (cancellationToken.IsCancellationRequested)
                    {

                        throw;

                    }
                    catch (OperationCanceledException)
                    {

                        readError = RequestTimeoutError;

                        readRetryable = true;

                    }
                    catch (HttpRequestException)
                    {

                        readError = RequestUnreachableError;

                        readRetryable = true;

                    }
                    catch (IOException)
                    {

                        readError = RequestDisconnectedError;

                        readRetryable = true;

                    }
                    catch (DecoderFallbackException)
                    {

                        readError = InvalidResponseError;

                    }
                    catch (Exception)
                    {

                        readError = RequestUnexpectedError;

                    }

                    if (readError is Error error)
                    {

                        yield return new WatchSseFrame(
                            WatchSseFrameType.Error,
                            Error: error,
                            Retryable: readRetryable);

                        yield break;

                    }

                    if (!hasFrame)
                    {

                        yield break;

                    }

                    yield return frames.Current;

                }

            }

        }

    }

    private async Task<(Error Error, bool Retryable)> ReadStreamErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {

        bool retryable = IsRetryableStatusCode(response.StatusCode);

        byte[]? responseBytes;

        try
        {

            responseBytes = await TryReadCappedContentAsync(
                    response.Content,
                    MaxResponseBytes,
                    cancellationToken)
                .ConfigureAwait(false);

        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {

            throw;

        }
        catch (OperationCanceledException)
        {

            return (RequestTimeoutError, true);

        }
        catch (IOException)
        {

            return (RequestDisconnectedError, true);

        }
        catch (HttpRequestException)
        {

            return (RequestUnreachableError, true);

        }
        catch (Exception)
        {

            return (RequestUnexpectedError, retryable);

        }

        if (responseBytes is null)
        {

            return (ResponseTooLargeError, retryable);

        }

        ApiResponse<string>? envelope = TryDeserialize(
            responseBytes,
            ArcanumJsonContext.Default.ApiResponseString);

        if (envelope is { IsSuccess: false, Error: not null })
        {

            Error error = envelope.Error.Value;

            bool retryableError = retryable
                && !string.Equals(
                    error.Code,
                    ErrorCodes.Api.TooManyConnections,
                    StringComparison.Ordinal);

            return (error, retryableError);

        }

        return (
            new Error(
                "Api.HttpError",
                $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}"),
            retryable);

    }

    private static async Task<bool> WaitForOperationOrDiagnosticAsync(
        Task operation,
        TimeSpan diagnosticInterval,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        CancellationToken cancellationToken)
    {

        if (operation.IsCompleted)
        {

            return false;

        }

        using CancellationTokenSource delayCts =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        Task diagnosticDelay = delayAsync(
            diagnosticInterval,
            delayCts.Token);

        Task completed = await Task
            .WhenAny(operation, diagnosticDelay)
            .ConfigureAwait(false);

        if (completed == operation)
        {

            delayCts.Cancel();

            return false;

        }

        await diagnosticDelay.ConfigureAwait(false);

        return true;

    }

    private static bool IsRetryableStatusCode(HttpStatusCode statusCode)
    {

        int value = (int)statusCode;

        return value is 408 or 425 or 429
            || value is >= 500 and <= 599;

    }

    private static void ValidateWatchPath(string path)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string normalizedPath = path.StartsWith("/", StringComparison.Ordinal)
            ? path[1..]
            : path;

        if (path.StartsWith("//", StringComparison.Ordinal)
            || path.Contains('\\')
            || !normalizedPath.StartsWith("api/", StringComparison.Ordinal))
        {

            throw new ArgumentException(
                "Watch paths must be relative Arcanum API paths.",
                nameof(path));

        }

    }

    private static bool IsRetryableTransportError(Error error) =>
        string.Equals(
            error.Code,
            ErrorCodes.Connection.Timeout,
            StringComparison.Ordinal)
        || string.Equals(
            error.Code,
            ErrorCodes.Connection.Unreachable,
            StringComparison.Ordinal);

    private static Result<HealthReportDto> MapHealthReport(
        HttpResponseMessage response,
        ApiResponse<HealthReportDto>? envelope)
    {

        if (envelope is { IsSuccess: true, Data: not null }
            && (response.IsSuccessStatusCode
                || response.StatusCode == HttpStatusCode.ServiceUnavailable))
        {

            return Result<HealthReportDto>.Success(envelope.Data);

        }

        if (envelope is { IsSuccess: false, Error: not null })
        {

            return Result<HealthReportDto>.Failure(envelope.Error.Value);

        }

        if (response.IsSuccessStatusCode
            || response.StatusCode == HttpStatusCode.ServiceUnavailable)
        {

            return Result<HealthReportDto>.Failure(InvalidResponseError);

        }

        return Result<HealthReportDto>.Failure(
            new Error(
                "Api.HttpError",
                $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}"));

    }

}
