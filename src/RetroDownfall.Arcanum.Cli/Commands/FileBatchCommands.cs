using System.Globalization;

using System.Text;

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

        if (!await CliOutputFile
                .ConfirmOverwriteAsync(confirmationPrompt, destination, cancellationToken)
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
                    BatchJsonlRules.MaxRecordBytes,
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

        if (!await CliOutputFile
                .ConfirmOverwriteAsync(confirmationPrompt, destination, cancellationToken)
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

        return CliFailureExit.ExitCode(error);
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

    internal static async Task<BatchPreflightResult> ValidateBatchJsonlAsync(
        string path,
        long maxRecordBytes,
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

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                lineNumber++;

                BoundedLine read = ReadBoundedLine(reader, maxRecordBytes, cancellationToken);

                if (read.EndOfInput)
                {
                    break;
                }

                if (read.TooLarge)
                {
                    return BatchPreflightResult.Invalid(
                        $"Batch preflight failed at line {lineNumber}: the record is larger than the "
                        + $"{maxRecordBytes.ToString(CultureInfo.InvariantCulture)}-byte per-record limit the host applies.");
                }

                string line = read.Text!;

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

                    if (!BatchJsonlRules.IsRequiredMethod(method))
                    {
                        return BatchPreflightResult.Invalid(
                            $"Batch preflight failed at line {lineNumber}: method must be POST.");
                    }

                    string? url = RequiredString(root, "url");

                    if (!string.Equals(url, BatchJsonlRules.SupportedEndpoint, StringComparison.Ordinal))
                    {
                        return BatchPreflightResult.Invalid(
                            $"Batch preflight failed at line {lineNumber}: url must be {BatchJsonlRules.SupportedEndpoint}.");
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

    /// <summary>
    /// One physical line read without ever holding more than the limit: a record over it is reported, not
    /// buffered, so a file whose "line" never ends cannot exhaust memory before the preflight judges it.
    /// </summary>
    internal static BoundedLine ReadBoundedLine(
        StreamReader reader,
        long maxRecordBytes,
        CancellationToken cancellationToken)
    {
        StringBuilder buffer = new();

        long bytes = 0;

        long scanned = 0;

        int next;

        while ((next = reader.Read()) >= 0)
        {
            // The scan is a synchronous per-character loop and one record may be 64 MiB, so Ctrl+C is
            // honoured every few thousand characters rather than only when the line ends.
            if ((++scanned & CancellationCheckMask) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            char character = (char)next;

            if (character == '\n')
            {
                return new BoundedLine(StripCarriageReturn(buffer), TooLarge: false, EndOfInput: false);
            }

            // The UTF-8 length of this UTF-16 unit. A surrogate pair is two units that encode as four
            // bytes together, so each counts as two; counting a lone unit as three bytes (which is what
            // an astral character's two halves cost) refused a record at two thirds of the limit.
            bytes += character < 0x80 ? 1
                : character < 0x800 ? 2
                : char.IsSurrogate(character) ? 2
                : 3;

            if (bytes > maxRecordBytes)
            {
                return new BoundedLine(null, TooLarge: true, EndOfInput: false);
            }

            buffer.Append(character);
        }

        return buffer.Length == 0
            ? new BoundedLine(null, TooLarge: false, EndOfInput: true)
            : new BoundedLine(StripCarriageReturn(buffer), TooLarge: false, EndOfInput: false);
    }

    private static string StripCarriageReturn(StringBuilder buffer)
    {
        if (buffer.Length > 0 && buffer[^1] == '\r')
        {
            buffer.Length--;
        }

        return buffer.ToString();
    }

    /// <summary>How often, in characters, a scan looks at its cancellation token; a power of two minus one.</summary>
    private const long CancellationCheckMask = 4095;

    internal readonly record struct BoundedLine(string? Text, bool TooLarge, bool EndOfInput);

    internal sealed record BatchPreflightResult(
        bool Success,
        string? Message)
    {
        public static readonly BatchPreflightResult Valid = new(true, null);

        public static BatchPreflightResult Invalid(string message) =>
            new(false, message);
    }
}
