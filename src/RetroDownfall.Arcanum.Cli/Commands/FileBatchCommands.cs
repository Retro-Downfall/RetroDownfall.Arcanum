using System.Globalization;

using System.Text.Json;

using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Cli.Services;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Desktop;

using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Cli.Commands;

public sealed class FileBatchCommands(
    FileBatchApiClient apiClient,
    IConsoleDispatcher dispatcher,
    ICliInvocationContext invocationContext,
    IConfirmationPrompt confirmationPrompt)
{

    private const string BatchEndpoint = "/v1/chat/completions";

    public async Task<int> UploadFile(
        string path,
        string purpose,
        string? contentType,
        CancellationToken cancellationToken)
    {

        if (!File.Exists(path))
        {

            dispatcher.WriteDiagnostic($"Local file not found: {Path.GetFullPath(path)}");

            return 1;

        }

        Result<OpenAiFileObject> result = await apiClient
            .UploadFileAsync(path, purpose, contentType, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {

            return WriteError(result.Error);

        }

        WriteFile(result.Value);

        return 0;

    }

    public async Task<int> ListFiles(
        string? purpose,
        CancellationToken cancellationToken)
    {

        Result<OpenAiFileListResponse> result = await apiClient
            .ListFilesAsync(purpose, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {

            return WriteError(result.Error);

        }

        if (invocationContext.Options.Json)
        {

            dispatcher.WriteJson(
                result.Value,
                ArcanumJsonContext.Default.OpenAiFileListResponse);

            return 0;

        }

        if (result.Value.Data.Count == 0)
        {

            dispatcher.WritePayload("No uploaded files found.");

            return 0;

        }

        foreach (OpenAiFileObject file in result.Value.Data)
        {

            dispatcher.WritePayload(FormatFile(file));

        }

        return 0;

    }

    public async Task<int> ShowFile(
        string id,
        CancellationToken cancellationToken)
    {

        Result<OpenAiFileObject> result = await apiClient
            .GetFileAsync(id, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {

            return WriteError(result.Error);

        }

        WriteFile(result.Value);

        return 0;

    }

    public async Task<int> DownloadFile(
        string id,
        string? output,
        CancellationToken cancellationToken)
    {

        Result<OpenAiFileObject> metadata = await apiClient
            .GetFileAsync(id, cancellationToken)
            .ConfigureAwait(false);

        if (metadata.IsFailure)
        {

            return WriteError(metadata.Error);

        }

        string destination = Path.GetFullPath(
            string.IsNullOrWhiteSpace(output)
                ? SafeFilename(metadata.Value)
                : output);

        bool overwrite = File.Exists(destination);

        if (overwrite
            && !await confirmationPrompt
                .PromptForConfirmationAsync(
                    $"Overwrite existing file {destination}?",
                    cancellationToken)
                .ConfigureAwait(false))
        {

            dispatcher.WriteDiagnostic("Download cancelled; the existing file was not changed.");

            return 0;

        }

        Result<long> download = await apiClient
            .DownloadFileAsync(id, destination, overwrite, cancellationToken)
            .ConfigureAwait(false);

        if (download.IsFailure)
        {

            return WriteError(download.Error);

        }

        FileDownloadPayload payload = new(id, destination, download.Value);

        if (invocationContext.Options.Json)
        {

            dispatcher.WriteJson(payload, CliJsonContext.Default.FileDownloadPayload);

        }
        else
        {

            dispatcher.WritePayload(
                $"Downloaded {id} to {destination} ({download.Value.ToString(CultureInfo.InvariantCulture)} bytes).");

        }

        return 0;

    }

    public async Task<int> DeleteFile(
        string id,
        CancellationToken cancellationToken)
    {

        if (!await confirmationPrompt
                .PromptForConfirmationAsync($"Delete uploaded file {id}?", cancellationToken)
                .ConfigureAwait(false))
        {

            dispatcher.WriteDiagnostic("File deletion cancelled.");

            return 0;

        }

        Result<OpenAiFileDeleteResponse> result = await apiClient
            .DeleteFileAsync(id, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {

            return WriteError(result.Error);

        }

        if (invocationContext.Options.Json)
        {

            dispatcher.WriteJson(
                result.Value,
                ArcanumJsonContext.Default.OpenAiFileDeleteResponse);

        }
        else
        {

            dispatcher.WritePayload($"Deleted {result.Value.Id}.");

        }

        return 0;

    }

    public async Task<int> CreateBatch(
        string inputFile,
        CancellationToken cancellationToken)
    {

        string inputFileId;

        if (!File.Exists(inputFile) && LooksLikeUploadedFileId(inputFile))
        {

            inputFileId = inputFile;

        }
        else
        {

            if (!File.Exists(inputFile))
            {

                dispatcher.WriteDiagnostic(
                    $"Batch input must be an uploaded file ID or an existing local JSONL path: {inputFile}");

                return 1;

            }

            BatchPreflightResult preflight = await ValidateBatchJsonlAsync(
                    inputFile,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!preflight.Success)
            {

                dispatcher.WriteDiagnostic(preflight.Message!);

                return 1;

            }

            Result<OpenAiFileObject> upload = await apiClient
                .UploadFileAsync(
                    inputFile,
                    "batch",
                    "application/jsonl",
                    cancellationToken)
                .ConfigureAwait(false);

            if (upload.IsFailure)
            {

                return WriteError(upload.Error);

            }

            inputFileId = upload.Value.Id;

            if (!invocationContext.Options.Json)
            {

                dispatcher.WriteDiagnostic($"Uploaded batch input as {inputFileId}.");

            }

        }

        Result<OpenAiBatchObject> result = await apiClient
            .CreateBatchAsync(inputFileId, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {

            return WriteError(result.Error);

        }

        WriteBatch(result.Value);

        return 0;

    }

    public async Task<int> ListBatches(
        string? status,
        string? cursor,
        CancellationToken cancellationToken)
    {

        Result<OpenAiBatchListResponse> result = await apiClient
            .ListBatchesAsync(status, cursor, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {

            return WriteError(result.Error);

        }

        if (invocationContext.Options.Json)
        {

            dispatcher.WriteJson(
                result.Value,
                ArcanumJsonContext.Default.OpenAiBatchListResponse);

            return 0;

        }

        if (result.Value.Data.Count == 0)
        {

            dispatcher.WritePayload("No batches found.");

            return 0;

        }

        foreach (OpenAiBatchObject batch in result.Value.Data)
        {

            dispatcher.WritePayload(FormatBatch(batch));

        }

        if (result.Value.HasMore

            && !string.IsNullOrWhiteSpace(result.Value.NextCursor))

        {

            string statusArgument = string.IsNullOrWhiteSpace(status)

                ? string.Empty

                : " --status " + CommandDisplayFormatter.QuoteArgumentForCurrentPlatform(status.Trim());

            string cursorArgument = CommandDisplayFormatter.QuoteArgumentForCurrentPlatform(

                result.Value.NextCursor);

            dispatcher.WritePayload(

                $"More batches remain. Continue: arcanum batch list{statusArgument} --cursor {cursorArgument}");

        }

        return 0;

    }

    public async Task<int> ShowBatch(
        string id,
        CancellationToken cancellationToken)
    {

        Result<OpenAiBatchObject> result = await apiClient
            .GetBatchAsync(id, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {

            return WriteError(result.Error);

        }

        WriteBatch(result.Value);

        return 0;

    }

    public async Task<int> WatchBatch(
        string id,
        int pollIntervalMilliseconds,
        CancellationToken cancellationToken)
    {

        int delayMilliseconds = Math.Clamp(pollIntervalMilliseconds, 1, 10_000);

        while (true)
        {

            Result<OpenAiBatchObject> result = await apiClient
                .GetBatchAsync(id, cancellationToken)
                .ConfigureAwait(false);

            if (result.IsFailure)
            {

                return WriteError(result.Error);

            }

            OpenAiBatchObject batch = result.Value;

            if (!invocationContext.Options.Json)
            {

                dispatcher.WritePayload(FormatBatch(batch));

            }

            if (BatchStatuses.IsTerminal(batch.Status))
            {

                if (invocationContext.Options.Json)
                {

                    dispatcher.WriteJson(
                        batch,
                        ArcanumJsonContext.Default.OpenAiBatchObject);

                }

                return batch.Status == BatchStatuses.Completed ? 0 : 1;

            }

            await Task.Delay(delayMilliseconds, cancellationToken).ConfigureAwait(false);

            delayMilliseconds = Math.Min(delayMilliseconds * 2, 10_000);

        }

    }

    public Task<int> CancelBatch(
        string id,
        CancellationToken cancellationToken) =>
        MutateBatch(id, apiClient.CancelBatchAsync, cancellationToken);

    public Task<int> ResetBatch(
        string id,
        CancellationToken cancellationToken) =>
        MutateBatch(id, apiClient.ResetBatchAsync, cancellationToken);

    public Task<int> DownloadBatchOutput(
        string id,
        string? output,
        CancellationToken cancellationToken) =>
        DownloadBatchArtifact(id, output, isError: false, cancellationToken);

    public Task<int> DownloadBatchErrors(
        string id,
        string? output,
        CancellationToken cancellationToken) =>
        DownloadBatchArtifact(id, output, isError: true, cancellationToken);

    private async Task<int> MutateBatch(
        string id,
        Func<string, CancellationToken, Task<Result<OpenAiBatchObject>>> action,
        CancellationToken cancellationToken)
    {

        Result<OpenAiBatchObject> result = await action(id, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {

            return WriteError(result.Error);

        }

        WriteBatch(result.Value);

        return 0;

    }

    private async Task<int> DownloadBatchArtifact(
        string id,
        string? output,
        bool isError,
        CancellationToken cancellationToken)
    {

        Result<OpenAiBatchObject> result = await apiClient
            .GetBatchAsync(id, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {

            return WriteError(result.Error);

        }

        string? fileId = isError
            ? result.Value.ErrorFileId
            : result.Value.OutputFileId;

        string kind = isError ? "errors" : "output";

        if (string.IsNullOrWhiteSpace(fileId))
        {

            dispatcher.WriteDiagnostic(
                $"Batch {id} does not have a {kind} file (status: {result.Value.Status}).");

            return 1;

        }

        string destination = Path.GetFullPath(
            string.IsNullOrWhiteSpace(output)
                ? $"{SafeIdentifier(id)}-{kind}.jsonl"
                : output);

        bool overwrite = File.Exists(destination);

        if (overwrite
            && !await confirmationPrompt
                .PromptForConfirmationAsync(
                    $"Overwrite existing file {destination}?",
                    cancellationToken)
                .ConfigureAwait(false))
        {

            dispatcher.WriteDiagnostic("Download cancelled; the existing file was not changed.");

            return 0;

        }

        Result<long> download = await apiClient
            .DownloadFileAsync(fileId, destination, overwrite, cancellationToken)
            .ConfigureAwait(false);

        if (download.IsFailure)
        {

            return WriteError(download.Error);

        }

        BatchArtifactPayload payload = new(
            id,
            kind,
            fileId,
            destination,
            download.Value);

        if (invocationContext.Options.Json)
        {

            dispatcher.WriteJson(payload, CliJsonContext.Default.BatchArtifactPayload);

        }
        else
        {

            dispatcher.WritePayload(
                $"Downloaded batch {kind} to {destination} ({download.Value.ToString(CultureInfo.InvariantCulture)} bytes).");

        }

        return 0;

    }

    private void WriteFile(OpenAiFileObject file)
    {

        if (invocationContext.Options.Json)
        {

            dispatcher.WriteJson(file, ArcanumJsonContext.Default.OpenAiFileObject);

        }
        else
        {

            dispatcher.WritePayload(FormatFile(file));

        }

    }

    private void WriteBatch(OpenAiBatchObject batch)
    {

        if (invocationContext.Options.Json)
        {

            dispatcher.WriteJson(batch, ArcanumJsonContext.Default.OpenAiBatchObject);

        }
        else
        {

            dispatcher.WritePayload(FormatBatch(batch));

        }

    }

    private int WriteError(Error error)
    {

        dispatcher.WriteDiagnostic($"{error.Code}: {error.Message}");

        return 1;

    }

    private static string FormatFile(OpenAiFileObject file) =>
        $"{file.Id}  {file.Filename}  {file.Bytes.ToString(CultureInfo.InvariantCulture)} bytes  purpose={file.Purpose}";

    private static string FormatBatch(OpenAiBatchObject batch) =>
        $"{batch.Id}  {batch.Status}  "
        + $"{batch.RequestCounts.Completed.ToString(CultureInfo.InvariantCulture)} completed, "
        + $"{batch.RequestCounts.Failed.ToString(CultureInfo.InvariantCulture)} failed, "
        + $"{batch.RequestCounts.Total.ToString(CultureInfo.InvariantCulture)} total";

    private static bool LooksLikeUploadedFileId(string value) =>
        value.StartsWith("file-", StringComparison.Ordinal);

    private static string SafeFilename(OpenAiFileObject file)
    {

        string leaf = Path.GetFileName(file.Filename.Replace('\\', '/'));

        if (string.IsNullOrWhiteSpace(leaf) || leaf is "." or "..")
        {

            return file.Id + ".bin";

        }

        char[] invalid = Path.GetInvalidFileNameChars();

        char[] sanitized = leaf
            .Select(character =>
                character < ' '
                || character == '/'
                || character == '\\'
                || invalid.Contains(character)
                    ? '_'
                    : character)
            .ToArray();

        string result = new(sanitized);

        return string.IsNullOrWhiteSpace(result) || result is "." or ".."
            ? file.Id + ".bin"
            : result;

    }

    private static string SafeIdentifier(string value) =>
        new(value
            .Select(character => char.IsLetterOrDigit(character) || character is '-' or '_'
                ? character
                : '_')
            .ToArray());

    private static async Task<BatchPreflightResult> ValidateBatchJsonlAsync(
        string path,
        CancellationToken cancellationToken)
    {

        HashSet<string> customIds = new(StringComparer.Ordinal);

        int requestCount = 0;

        try
        {

            await using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81_920,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);

            using StreamReader reader = new(stream, detectEncodingFromByteOrderMarks: true);

            int lineNumber = 0;

            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {

                lineNumber++;

                if (string.IsNullOrWhiteSpace(line))
                {

                    continue;

                }

                requestCount++;

                JsonDocument document;

                try
                {

                    document = JsonDocument.Parse(line);

                }
                catch (JsonException exception)
                {

                    return BatchPreflightResult.Invalid(
                        $"Batch preflight failed at line {lineNumber}: invalid JSON ({exception.Message}).");

                }

                using (document)
                {

                    JsonElement root = document.RootElement;

                    if (root.ValueKind != JsonValueKind.Object)
                    {

                        return BatchPreflightResult.Invalid(
                            $"Batch preflight failed at line {lineNumber}: wrapper must be a JSON object.");

                    }

                    string? customId = RequiredString(root, "custom_id");

                    if (customId is null)
                    {

                        return BatchPreflightResult.Invalid(
                            $"Batch preflight failed at line {lineNumber}: custom_id is required.");

                    }

                    if (!customIds.Add(customId))
                    {

                        return BatchPreflightResult.Invalid(
                            $"Batch preflight failed at line {lineNumber}: custom_id '{customId}' is duplicated.");

                    }

                    string? method = RequiredString(root, "method");

                    if (!string.Equals(method, "POST", StringComparison.Ordinal))
                    {

                        return BatchPreflightResult.Invalid(
                            $"Batch preflight failed at line {lineNumber}: method must be POST.");

                    }

                    string? url = RequiredString(root, "url");

                    if (!string.Equals(url, BatchEndpoint, StringComparison.Ordinal))
                    {

                        return BatchPreflightResult.Invalid(
                            $"Batch preflight failed at line {lineNumber}: url must be {BatchEndpoint}.");

                    }

                    if (!root.TryGetProperty("body", out JsonElement body)
                        || body.ValueKind != JsonValueKind.Object)
                    {

                        return BatchPreflightResult.Invalid(
                            $"Batch preflight failed at line {lineNumber}: body must be a JSON object.");

                    }

                }

            }

            return requestCount == 0
                ? BatchPreflightResult.Invalid("Batch preflight failed: the JSONL file contains no requests.")
                : BatchPreflightResult.Valid;

        }
        catch (JsonException exception)
        {

            return BatchPreflightResult.Invalid(
                $"Batch preflight failed: invalid JSONL ({exception.Message}).");

        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {

            return BatchPreflightResult.Invalid(
                "Batch preflight failed: the local JSONL file could not be read.");

        }

    }

    private static string? RequiredString(
        JsonElement element,
        string property) =>
        element.TryGetProperty(property, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()
                : null;

    private sealed record BatchPreflightResult(
        bool Success,
        string? Message)
    {

        public static readonly BatchPreflightResult Valid = new(true, null);

        public static BatchPreflightResult Invalid(string message) =>
            new(false, message);

    }

}
