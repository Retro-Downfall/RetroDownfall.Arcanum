using System.Net.Http.Headers;

using System.Text;

using System.Text.Json;

using System.Text.Json.Serialization.Metadata;

using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;

using RetroDownfall.Arcanum.Api.Security;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Cli.Services;

/// <summary>
/// Dedicated client for OpenAI-compatible file and batch routes. Successful responses are bare
/// OpenAI objects, never <c>ApiResponse&lt;T&gt;</c>; uploads and downloads remain streaming.
/// </summary>
public sealed class FileBatchApiClient(
    IHttpClientFactory httpClientFactory,
    ISecretStore secretStore)
{

    private const long MaxJsonResponseBytes = 64 * 1024 * 1024;

    public Task<Result<OpenAiFileListResponse>> ListFilesAsync(
        string? purpose,
        CancellationToken cancellationToken) =>
        SendJsonAsync(
            HttpMethod.Get,
            BuildQuery("/v1/files", "purpose", purpose),
            content: null,
            ArcanumJsonContext.Default.OpenAiFileListResponse,
            cancellationToken);

    public Task<Result<OpenAiFileObject>> GetFileAsync(
        string fileId,
        CancellationToken cancellationToken) =>
        SendJsonAsync(
            HttpMethod.Get,
            "/v1/files/" + Uri.EscapeDataString(fileId),
            content: null,
            ArcanumJsonContext.Default.OpenAiFileObject,
            cancellationToken);

    public async Task<Result<OpenAiFileObject>> UploadFileAsync(
        string filePath,
        string purpose,
        string? contentType,
        CancellationToken cancellationToken)
    {

        try
        {

            await using FileStream stream = new(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81_920,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);

            using MultipartFormDataContent form = new();

            using StreamContent file = new(stream);

            file.Headers.ContentType = new MediaTypeHeaderValue(
                string.IsNullOrWhiteSpace(contentType)
                    ? InferContentType(filePath)
                    : contentType.Trim());

            form.Add(file, "file", Path.GetFileName(filePath));

            form.Add(new StringContent(purpose, Encoding.UTF8), "purpose");

            return await SendJsonAsync(
                    HttpMethod.Post,
                    "/v1/files",
                    form,
                    ArcanumJsonContext.Default.OpenAiFileObject,
                    cancellationToken)
                .ConfigureAwait(false);

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

            throw;

        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {

            return Result<OpenAiFileObject>.Failure(
                new Error("Files.ReadFailed", "The local upload file could not be read."));

        }

    }

    public Task<Result<OpenAiFileDeleteResponse>> DeleteFileAsync(
        string fileId,
        CancellationToken cancellationToken) =>
        SendJsonAsync(
            HttpMethod.Delete,
            "/v1/files/" + Uri.EscapeDataString(fileId),
            content: null,
            ArcanumJsonContext.Default.OpenAiFileDeleteResponse,
            cancellationToken);

    public Task<Result<OpenAiBatchListResponse>> ListBatchesAsync(
        string? status,
        string? cursor,
        CancellationToken cancellationToken) =>
        SendJsonAsync(
            HttpMethod.Get,
            BuildBatchListQuery(status, cursor),
            content: null,
            ArcanumJsonContext.Default.OpenAiBatchListResponse,
            cancellationToken);

    public Task<Result<OpenAiBatchObject>> GetBatchAsync(
        string batchId,
        CancellationToken cancellationToken) =>
        SendJsonAsync(
            HttpMethod.Get,
            "/v1/batches/" + Uri.EscapeDataString(batchId),
            content: null,
            ArcanumJsonContext.Default.OpenAiBatchObject,
            cancellationToken);

    public Task<Result<OpenAiBatchObject>> CreateBatchAsync(
        string inputFileId,
        CancellationToken cancellationToken)
    {

        OpenAiBatchRequest request = new(
            inputFileId,
            "/v1/chat/completions",
            "24h");

        string json = JsonSerializer.Serialize(
            request,
            ArcanumJsonContext.Default.OpenAiBatchRequest);

        return SendJsonAsync(
            HttpMethod.Post,
            "/v1/batches",
            new StringContent(json, Encoding.UTF8, "application/json"),
            ArcanumJsonContext.Default.OpenAiBatchObject,
            cancellationToken);

    }

    public Task<Result<OpenAiBatchObject>> CancelBatchAsync(
        string batchId,
        CancellationToken cancellationToken) =>
        PostBatchMutationAsync(batchId, "cancel", cancellationToken);

    public Task<Result<OpenAiBatchObject>> ResetBatchAsync(
        string batchId,
        CancellationToken cancellationToken) =>
        PostBatchMutationAsync(batchId, "reset", cancellationToken);

    public async Task<Result<long>> DownloadFileAsync(
        string fileId,
        string destinationPath,
        bool overwrite,
        CancellationToken cancellationToken)
    {

        string fullDestination = Path.GetFullPath(destinationPath);

        string directory = Path.GetDirectoryName(fullDestination)
            ?? throw new IOException("The download destination has no parent directory.");

        Directory.CreateDirectory(directory);

        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullDestination)}.{Guid.NewGuid():N}.download");

        try
        {

            string? apiKey = await PeekApiKeyAsync().ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(apiKey))
            {

                return Result<long>.Failure(MissingApiKey());

            }

            HttpClient client = httpClientFactory.CreateClient(ArcanumApiClient.StreamingHttpClientName);

            using HttpRequestMessage request = new(
                HttpMethod.Get,
                "/v1/files/" + Uri.EscapeDataString(fileId) + "/content");

            _ = request.Headers.TryAddWithoutValidation(ArcanumApiHeaders.ApiKey, apiKey);

            using HttpResponseMessage response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {

                return Result<long>.Failure(
                    await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false));

            }

            await using Stream network = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);

            long bytes = 0;

            await using (FileStream destination = new(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 81_920,
                             options: FileOptions.Asynchronous | FileOptions.SequentialScan))
            {

                byte[] buffer = new byte[81_920];

                int read;

                while ((read = await network.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {

                    await destination
                        .WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                        .ConfigureAwait(false);

                    bytes += read;

                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);

            }

            File.Move(temporaryPath, fullDestination, overwrite);

            temporaryPath = string.Empty;

            return Result<long>.Success(bytes);

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

            throw;

        }
        catch (OperationCanceledException)
        {

            return Result<long>.Failure(
                new Error(ErrorCodes.Connection.Timeout, "The file download timed out."));

        }
        catch (HttpRequestException)
        {

            return Result<long>.Failure(
                new Error(ErrorCodes.Connection.Unreachable, "The Arcanum API is unreachable."));

        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {

            return Result<long>.Failure(
                new Error("Files.WriteFailed", "The downloaded file could not be written safely."));

        }
        finally
        {

            if (!string.IsNullOrEmpty(temporaryPath))
            {

                TryDelete(temporaryPath);

            }

        }

    }

    private Task<Result<OpenAiBatchObject>> PostBatchMutationAsync(
        string batchId,
        string operation,
        CancellationToken cancellationToken) =>
        SendJsonAsync(
            HttpMethod.Post,
            $"/v1/batches/{Uri.EscapeDataString(batchId)}/{operation}",
            content: null,
            ArcanumJsonContext.Default.OpenAiBatchObject,
            cancellationToken);

    private async Task<Result<T>> SendJsonAsync<T>(
        HttpMethod method,
        string path,
        HttpContent? content,
        JsonTypeInfo<T> responseType,
        CancellationToken cancellationToken)
    {

        try
        {

            string? apiKey = await PeekApiKeyAsync().ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(apiKey))
            {

                content?.Dispose();

                return Result<T>.Failure(MissingApiKey());

            }

            HttpClient client = httpClientFactory.CreateClient(ArcanumApiClient.RequestHttpClientName);

            using HttpRequestMessage request = new(method, path)
            {

                Content = content,

            };

            _ = request.Headers.TryAddWithoutValidation(ArcanumApiHeaders.ApiKey, apiKey);

            using HttpResponseMessage response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {

                return Result<T>.Failure(
                    await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false));

            }

            byte[]? bytes = await ReadCappedAsync(
                    response.Content,
                    MaxJsonResponseBytes,
                    cancellationToken)
                .ConfigureAwait(false);

            if (bytes is null)
            {

                return Result<T>.Failure(
                    new Error("Api.ResponseTooLarge", "The API response exceeded the maximum allowed size."));

            }

            T? value = JsonSerializer.Deserialize(bytes, responseType);

            return value is null
                ? Result<T>.Failure(new Error("Api.InvalidResponse", "The API returned an invalid response."))
                : Result<T>.Success(value);

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

            throw;

        }
        catch (OperationCanceledException)
        {

            return Result<T>.Failure(
                new Error(ErrorCodes.Connection.Timeout, "The request to the Arcanum API timed out."));

        }
        catch (HttpRequestException)
        {

            return Result<T>.Failure(
                new Error(ErrorCodes.Connection.Unreachable, "The Arcanum API is unreachable."));

        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {

            return Result<T>.Failure(
                new Error("Api.InvalidResponse", "The API returned an invalid response."));

        }

    }

    private static async Task<Error> ReadErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {

        byte[]? bytes = await ReadCappedAsync(
                response.Content,
                MaxJsonResponseBytes,
                cancellationToken)
            .ConfigureAwait(false);

        if (bytes is not null)
        {

            try
            {

                using JsonDocument document = JsonDocument.Parse(bytes);

                if (document.RootElement.TryGetProperty("error", out JsonElement error))
                {

                    string code = TryString(error, "code")
                        ?? TryString(error, "type")
                        ?? $"Http.{(int)response.StatusCode}";

                    string message = TryString(error, "message")
                        ?? response.ReasonPhrase
                        ?? "Request failed.";

                    return new Error(code, message);

                }

            }
            catch (JsonException)
            {

            }

        }

        return new Error(
            $"Http.{(int)response.StatusCode}",
            response.ReasonPhrase ?? "Request failed.");

    }

    private static async Task<byte[]?> ReadCappedAsync(
        HttpContent content,
        long maxBytes,
        CancellationToken cancellationToken)
    {

        if (content.Headers.ContentLength is { } length && length > maxBytes)
        {

            return null;

        }

        await using Stream stream = await content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

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

            await buffer
                .WriteAsync(chunk.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);

        }

        return buffer.ToArray();

    }

    private static string BuildQuery(
        string path,
        string key,
        string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? path
            : $"{path}?{key}={Uri.EscapeDataString(value.Trim())}";

    private static string BuildBatchListQuery(

        string? status,

        string? cursor)

    {

        List<string> fields = [];

        if (!string.IsNullOrWhiteSpace(status))

        {

            fields.Add("status=" + Uri.EscapeDataString(status.Trim()));

        }

        if (!string.IsNullOrWhiteSpace(cursor))

        {

            fields.Add("after=" + Uri.EscapeDataString(cursor.Trim()));

        }

        return fields.Count == 0

            ? "/v1/batches"

            : "/v1/batches?" + string.Join('&', fields);

    }

    private static string InferContentType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jsonl" => "application/jsonl",
            ".json" => "application/json",
            ".txt" => "text/plain",
            ".md" => "text/markdown",
            ".csv" => "text/csv",
            ".pdf" => "application/pdf",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".zip" => "application/zip",
            _ => "application/octet-stream",
        };

    private static string? TryString(
        JsonElement element,
        string property) =>
        element.TryGetProperty(property, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    private async Task<string?> PeekApiKeyAsync()
    {

        SecretStoreReadResult result = await secretStore
            .PeekApiKeyReadResultAsync()
            .ConfigureAwait(false);

        return result.Status == SecretStoreReadStatus.Ok ? result.Value : null;

    }

    private static Error MissingApiKey() =>
        new(
            ErrorCodes.Security.MissingApiKey,
            "No API key found. Run 'arcanum serve' once to generate and store a key.");

    private static void TryDelete(string path)
    {

        try
        {

            File.Delete(path);

        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {

        }

    }

}
