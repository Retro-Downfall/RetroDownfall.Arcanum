using System.Buffers;

using System.Security.Cryptography;

using System.Text;

using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Intelligence.Models;

namespace RetroDownfall.Arcanum.Cli.Commands;

internal interface IRunAttachmentStager
{

    Task<RunAttachmentStageResult> StageAsync(
        IReadOnlyList<string> withValues,
        string workingDirectory,
        string? pipedContent,
        CancellationToken cancellationToken);

}

internal sealed record RunAttachmentMetadata(
    string Source,
    string Kind,
    long ByteCount,
    string Sha256,
    int ChunkCount);

internal sealed record RunAttachmentStageResult(
    bool IsSuccess,
    List<AttachedFileDto> AttachedFiles,
    List<ScryingFocusDto> ScryingFoci,
    List<RunAttachmentMetadata> Metadata,
    string[] Diagnostics,
    string? Error);

internal sealed class RunAttachmentStager(
    IOptions<ArcanumSettings> settings) : IRunAttachmentStager
{

    internal const int MaxAttachedFileChunkBytes =
        (int)ArcanumRuntimeDefaults.CliMaxAttachFileSizeBytes;

    internal const int MaxTextFileSourceBytes =
        (int)ArcanumRuntimeDefaults.CliMaxAttachedTotalBytes;

    private const int Utf8BomBytes = 3;

    private const int FileReadBufferBytes = 81920;

    private const long MaxAttachedTotalBytes =
        ArcanumRuntimeDefaults.CliMaxAttachedTotalBytes;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly ArcanumSettings _settings = settings.Value;

    public async Task<RunAttachmentStageResult> StageAsync(
        IReadOnlyList<string> withValues,
        string workingDirectory,
        string? pipedContent,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(withValues);

        if (string.IsNullOrWhiteSpace(workingDirectory))
        {

            return Failure("A working directory is required to resolve --with paths.");

        }

        string fullWorkingDirectory;

        try
        {

            fullWorkingDirectory = Path.GetFullPath(workingDirectory);

        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {

            return Failure(
                $"The working directory could not be resolved: {exception.Message}");

        }

        List<AttachedFileDto> attachedFiles = [];

        List<ScryingFocusDto> scryingFoci = [];

        List<RunAttachmentMetadata> metadata = [];

        List<string> diagnostics = [];

        long attachedUtf8Bytes = 0;

        ScryingSettings scrying = _settings.ResolveScrying();

        int maximumImages = ArcanumSettingClamps.ScryingMaxImagesPerRequest(
            scrying.MaxImagesPerRequest);

        foreach (string value in withValues)
        {

            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(value))
            {

                return Failure("Each --with value must use the @path form.");

            }

            if (value[0] != '@')
            {

                return Failure(
                    $"The --with value '{value}' must use the @path form.");

            }

            string requestedPath = value[1..];

            if (string.IsNullOrWhiteSpace(requestedPath))
            {

                return Failure("The --with @path value must include a file path.");

            }

            string fullPath;

            try
            {

                fullPath = Path.IsPathRooted(requestedPath)
                    ? Path.GetFullPath(requestedPath)
                    : Path.GetFullPath(
                        Path.Combine(fullWorkingDirectory, requestedPath));

            }
            catch (Exception exception) when (
                exception is ArgumentException
                    or NotSupportedException
                    or PathTooLongException)
            {

                return Failure(
                    $"The --with path '{requestedPath}' could not be resolved: {exception.Message}");

            }

            if (!File.Exists(fullPath))
            {

                string reason = Directory.Exists(fullPath)
                    ? "it is a directory, not a file"
                    : "the file does not exist";

                return Failure(
                    $"The --with path '{requestedPath}' cannot be staged because {reason}.");

            }

            string relativePath = Path.GetRelativePath(
                fullWorkingDirectory,
                fullPath);

            if (ScryingFocusStager.IsImagePath(fullPath))
            {

                if (scryingFoci.Count >= maximumImages)
                {

                    return Failure(
                        $"Provider request-shape boundary reached: measured {scryingFoci.Count + 1} images, "
                        + $"which exceeds the maximum of {maximumImages} images per request. "
                        + "No staged work was saved; send the remaining images in a later turn "
                        + "or choose a provider/model with a larger factual image capacity.");

                }

                RunAttachmentStageResult? imageFailure = StageImage(
                    fullPath,
                    relativePath,
                    scrying,
                    scryingFoci,
                    metadata,
                    diagnostics);

                if (imageFailure is not null)
                {

                    return imageFailure;

                }

                continue;

            }

            TextSourceRead text = await ReadTextFileAsync(
                fullPath,
                cancellationToken).ConfigureAwait(false);

            if (!text.IsSuccess)
            {

                return Failure(
                    $"The --with path '{requestedPath}' could not be staged: {text.Error}");

            }

            RunAttachmentStageResult? textFailure = AddTextSource(
                relativePath,
                relativePath,
                text.Content!,
                MaxTextFileSourceBytes,
                attachedFiles,
                metadata,
                diagnostics,
                ref attachedUtf8Bytes);

            if (textFailure is not null)
            {

                return textFailure;

            }

        }

        if (pipedContent is not null)
        {

            RunAttachmentStageResult? pipeFailure = AddTextSource(
                "stdin",
                "stdin.txt",
                pipedContent,
                RunInputReader.MaxRedirectedInputBytes,
                attachedFiles,
                metadata,
                diagnostics,
                ref attachedUtf8Bytes);

            if (pipeFailure is not null)
            {

                return pipeFailure;

            }

        }

        return new RunAttachmentStageResult(
            true,
            attachedFiles,
            scryingFoci,
            metadata,
            [.. diagnostics],
            null);

    }

    private RunAttachmentStageResult? StageImage(
        string fullPath,
        string relativePath,
        ScryingSettings scrying,
        List<ScryingFocusDto> scryingFoci,
        List<RunAttachmentMetadata> metadata,
        List<string> diagnostics)
    {

        long maximumBytes = ArcanumSettingClamps.ScryingMaxImageBytes(
            scrying.MaxImageBytes);

        string[] allowedMimeTypes = scrying.AllowedMimeTypes is { Length: > 0 }
            ? scrying.AllowedMimeTypes
            : ArcanumRuntimeDefaults.Scrying.AllowedMimeTypes;

        ScryingFocusStager.StagingResult staged = ScryingFocusStager.Stage(
            fullPath,
            maximumBytes,
            allowedMimeTypes);

        if (!staged.IsSuccess || staged.Focus is null)
        {

            return Failure(
                $"The --with image '{relativePath}' could not be staged: {staged.Error ?? "unknown image error"}");

        }

        byte[] imageBytes;

        try
        {

            imageBytes = Convert.FromBase64String(staged.Focus.Data);

        }
        catch (FormatException exception)
        {

            return Failure(
                $"The --with image '{relativePath}' could not be staged: {exception.Message}");

        }

        if (imageBytes.LongLength > maximumBytes)
        {

            return Failure(
                $"Physical single-image allocation boundary reached for '{relativePath}': measured "
                + $"{imageBytes.LongLength} bytes; limit {maximumBytes}. No staged work was saved. "
                + "Resize the image or send it in a provider-supported form and retry.");

        }

        string digest = Sha256(imageBytes);

        scryingFoci.Add(staged.Focus);

        metadata.Add(
            new RunAttachmentMetadata(
                relativePath,
                "image",
                imageBytes.LongLength,
                digest,
                1));

        diagnostics.Add(
            $"Staged image '{relativePath}' ({imageBytes.LongLength} bytes, SHA-256 {digest}).");

        return null;

    }

    private static RunAttachmentStageResult? AddTextSource(
        string source,
        string relativePath,
        string content,
        int maximumSourceBytes,
        List<AttachedFileDto> attachedFiles,
        List<RunAttachmentMetadata> metadata,
        List<string> diagnostics,
        ref long attachedUtf8Bytes)
    {

        int utf8Bytes = Encoding.UTF8.GetByteCount(content);

        if (utf8Bytes > maximumSourceBytes)
        {

            return Failure(
                $"Physical single-source allocation boundary reached for '{source}': measured "
                + $"{utf8Bytes} UTF-8 bytes; limit {maximumSourceBytes}. No staged work was saved. "
                + "Split the source and retry with smaller chunks.");

        }

        List<string> chunks = SplitUtf8(content, MaxAttachedFileChunkBytes);

        if (attachedUtf8Bytes + utf8Bytes > MaxAttachedTotalBytes)
        {

            return Failure(
                "Physical single-request allocation boundary reached for staged text: "
                + $"measured {attachedUtf8Bytes + utf8Bytes} UTF-8 bytes; limit {MaxAttachedTotalBytes}. "
                + "No staged work was saved. Send the remaining sources in a later turn "
                + "or persist and reference session attachments.");

        }

        for (int index = 0; index < chunks.Count; index++)
        {

            string chunkPath = ChunkPath(
                relativePath,
                index,
                chunks.Count);

            if (chunkPath.Length > ArcanumRuntimeDefaults.CliMaxAttachedFileRelativePathChars)
            {

                return Failure(
                    $"The generated attached-file path for '{source}' exceeds the server path limit "
                    + $"of {ArcanumRuntimeDefaults.CliMaxAttachedFileRelativePathChars} characters.");

            }

            attachedFiles.Add(
                new AttachedFileDto(
                    chunkPath,
                    chunks[index]));

        }

        attachedUtf8Bytes += utf8Bytes;

        string digest = Sha256(Encoding.UTF8.GetBytes(content));

        metadata.Add(
            new RunAttachmentMetadata(
                source,
                "text",
                utf8Bytes,
                digest,
                chunks.Count));

        diagnostics.Add(
            $"Staged text '{source}' ({utf8Bytes} UTF-8 bytes, {chunks.Count} part(s), SHA-256 {digest}).");

        return null;

    }

    private static async Task<TextSourceRead> ReadTextFileAsync(
        string fullPath,
        CancellationToken cancellationToken)
    {

        try
        {

            await using FileStream stream = new(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                FileReadBufferBytes,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            if (stream.Length > MaxTextFileSourceBytes + Utf8BomBytes)
            {

                return TextSourceRead.Failure(
                    $"file exceeds the {MaxTextFileSourceBytes}-byte UTF-8 limit.");

            }

            using MemoryStream bytes = new(
                capacity: (int)Math.Min(
                    stream.Length,
                    MaxTextFileSourceBytes + Utf8BomBytes));

            byte[] buffer = ArrayPool<byte>.Shared.Rent(FileReadBufferBytes);

            try
            {

                while (true)
                {

                    int read = await stream
                        .ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                        .ConfigureAwait(false);

                    if (read == 0)
                    {

                        break;

                    }

                    if (bytes.Length + read > MaxTextFileSourceBytes + Utf8BomBytes)
                    {

                        return TextSourceRead.Failure(
                            $"file exceeds the {MaxTextFileSourceBytes}-byte UTF-8 limit.");

                    }

                    await bytes
                        .WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                        .ConfigureAwait(false);

                }

            }
            finally
            {

                ArrayPool<byte>.Shared.Return(buffer);

            }

            byte[] value = bytes.ToArray();

            int offset = HasUtf8Bom(value)
                ? Utf8BomBytes
                : 0;

            int contentBytes = value.Length - offset;

            if (contentBytes > MaxTextFileSourceBytes)
            {

                return TextSourceRead.Failure(
                    $"file exceeds the {MaxTextFileSourceBytes}-byte UTF-8 limit.");

            }

            string content = StrictUtf8.GetString(
                value,
                offset,
                contentBytes);

            return TextSourceRead.Success(content);

        }
        catch (DecoderFallbackException exception)
        {

            return TextSourceRead.Failure(
                $"file is not valid UTF-8 text: {exception.Message}");

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or ArgumentException)
        {

            return TextSourceRead.Failure(exception.Message);

        }

    }

    private static bool HasUtf8Bom(byte[] bytes) =>
        bytes.Length >= Utf8BomBytes
        && bytes[0] == 0xEF
        && bytes[1] == 0xBB
        && bytes[2] == 0xBF;

    private static List<string> SplitUtf8(
        string content,
        int maximumBytes)
    {

        if (content.Length == 0)
        {

            return [string.Empty];

        }

        List<string> chunks = [];

        int chunkStart = 0;

        int chunkBytes = 0;

        int offset = 0;

        while (offset < content.Length)
        {

            OperationStatus status = Rune.DecodeFromUtf16(
                content.AsSpan(offset),
                out Rune rune,
                out int consumed);

            if (status != OperationStatus.Done)
            {

                rune = Rune.ReplacementChar;

                consumed = 1;

            }

            int runeBytes = rune.Utf8SequenceLength;

            if (chunkBytes > 0
                && chunkBytes + runeBytes > maximumBytes)
            {

                chunks.Add(content[chunkStart..offset]);

                chunkStart = offset;

                chunkBytes = 0;

            }

            chunkBytes += runeBytes;

            offset += consumed;

        }

        chunks.Add(content[chunkStart..]);

        return chunks;

    }

    private static string ChunkPath(
        string relativePath,
        int index,
        int count) =>
        count == 1
            ? relativePath
            : $"{relativePath}.part-{index + 1:D3}-of-{count:D3}";

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static RunAttachmentStageResult Failure(string error) =>
        new(
            false,
            [],
            [],
            [],
            [],
            error);

    private sealed record TextSourceRead(
        bool IsSuccess,
        string? Content,
        string? Error)
    {

        public static TextSourceRead Success(string content) =>
            new(true, content, null);

        public static TextSourceRead Failure(string error) =>
            new(false, null, error);

    }

}
