using System.Buffers;

using RetroDownfall.Arcanum.Core.Intelligence.Models;

namespace RetroDownfall.Arcanum.Cli.Commands;

/// <summary>
/// Scrying — reads an image file from disk, detects its MIME type, validates the code-owned size
/// envelope and <c>Arcanum:Security:AllowedImageMimeTypes</c>, then base64-encodes it into a
/// <see cref="ScryingFocusDto"/>. Runtime enablement comes from <c>Arcanum:Features:Scrying</c>.
/// Shared by the <c>chat</c> REPL's <c>@path</c> inline staging and the <c>ask --image</c> flag.
/// Staging is entirely in-memory and ephemeral — no temp files, no disk writes.
/// </summary>
public static class ScryingFocusStager
{

    private const int FileReadBufferBytes = 81920;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png",
        ".jpg",
        ".jpeg",
        ".gif",
        ".webp",
        ".bmp",
    };

    /// <summary>
    /// True when <paramref name="path"/> has a recognized image extension. Used by the <c>chat</c>
    /// REPL to decide whether an inline <c>@path</c> token should route to image staging instead of
    /// the existing text-file staging path.
    /// </summary>
    public static bool IsImagePath(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// Result of a staging attempt. Exactly one of <see cref="Focus"/> / <see cref="Error"/> is set.
    /// <see cref="FileSizeBytes"/> is populated whenever the file could be stat'd, even on failure,
    /// for themed size-aware error messages.
    /// </summary>
    public sealed record StagingResult(ScryingFocusDto? Focus, long? FileSizeBytes, string? Error)
    {
        public bool IsSuccess => Focus is not null;
    }

    /// <summary>
    /// Stats <paramref name="fullPath"/> and checks its size against <paramref name="maxImageBytes"/>
    /// without reading file contents — used at <c>@path</c> match time in the <c>chat</c> REPL so an
    /// oversized image is rejected immediately (matching the existing text-file staging UX) while
    /// the actual read/base64-encode is deferred to turn submission.
    /// </summary>
    public static StagingResult CheckSize(string fullPath, long maxImageBytes)
    {

        long length;

        try
        {
            length = new FileInfo(fullPath).Length;
        }
        catch (IOException ex)
        {
            return new StagingResult(null, null, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new StagingResult(null, null, ex.Message);
        }

        if (length > maxImageBytes)
        {
            return new StagingResult(
                null,
                length,
                $"Image exceeds the maximum size of {maxImageBytes} bytes.");
        }

        return new StagingResult(null, length, null);

    }

    /// <summary>
    /// Reads, MIME-sniffs, validates, and base64-encodes an image file into a
    /// <see cref="ScryingFocusDto"/>. Re-checks size (the caller may have only stat'd the file
    /// earlier via <see cref="CheckSize"/>, and the file could have changed since). The read stops
    /// after one sentinel byte beyond the limit so growth and special streams remain memory-bounded.
    /// </summary>
    public static StagingResult Stage(
        string fullPath,
        long maxImageBytes,
        string[] allowedMimeTypes) =>
        Stage(
            fullPath,
            maxImageBytes,
            allowedMimeTypes,
            static path => new FileInfo(path).Length,
            static path => new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                FileReadBufferBytes,
                FileOptions.SequentialScan));

    internal static StagingResult Stage(
        string fullPath,
        long maxImageBytes,
        string[] allowedMimeTypes,
        Func<string, long> getFileLength,
        Func<string, Stream> openRead)
    {

        long length;

        byte[] bytes;

        try
        {
            length = getFileLength(fullPath);

            if (length > maxImageBytes)
            {
                return new StagingResult(
                    null,
                    length,
                    $"Image exceeds the maximum size of {maxImageBytes} bytes.");
            }

            using Stream stream = openRead(fullPath);

            using MemoryStream content = new(
                capacity: length is > 0 and <= int.MaxValue
                    ? (int)length
                    : 0);

            byte[] buffer = ArrayPool<byte>.Shared.Rent(FileReadBufferBytes);

            long observedBytes = 0;

            try
            {

                while (true)
                {

                    long remainingBytes = maxImageBytes - observedBytes;

                    int requestedBytes = remainingBytes >= buffer.Length
                        ? buffer.Length
                        : (int)remainingBytes + 1;

                    int read = stream.Read(buffer, 0, requestedBytes);

                    if (read == 0)
                    {

                        break;

                    }

                    observedBytes += read;

                    if (observedBytes > maxImageBytes)
                    {

                        return new StagingResult(
                            null,
                            Math.Max(length, observedBytes),
                            $"Image exceeds the maximum size of {maxImageBytes} bytes.");

                    }

                    content.Write(buffer, 0, read);

                }

            }
            finally
            {

                ArrayPool<byte>.Shared.Return(buffer);

            }

            bytes = content.ToArray();

            length = bytes.LongLength;
        }
        catch (IOException ex)
        {
            return new StagingResult(null, null, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new StagingResult(null, null, ex.Message);
        }

        string mimeType = DetectMimeType(bytes, Path.GetExtension(fullPath));

        if (!IsAllowedMimeType(mimeType, allowedMimeTypes))
        {
            return new StagingResult(
                null,
                length,
                $"Image type '{mimeType}' is not supported. Allowed types: {string.Join(", ", allowedMimeTypes)}.");
        }

        string base64 = Convert.ToBase64String(bytes);

        return new StagingResult(new ScryingFocusDto(base64, mimeType), length, null);

    }

    private static bool IsAllowedMimeType(string mimeType, string[] allowedMimeTypes)
    {

        foreach (string allowed in allowedMimeTypes)
        {
            if (string.Equals(allowed, mimeType, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;

    }

    /// <summary>
    /// Detects MIME type primarily from magic bytes (thorough — catches a mislabeled extension);
    /// falls back to the file extension when the leading bytes are inconclusive or too short.
    /// </summary>
    private static string DetectMimeType(byte[] bytes, string extension)
    {

        if (bytes.Length >= 8
            && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            return "image/png";
        }

        if (bytes.Length >= 3
            && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (bytes.Length >= 6
            && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38)
        {
            return "image/gif";
        }

        if (bytes.Length >= 12
            && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
            && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
        {
            return "image/webp";
        }

        if (bytes.Length >= 2
            && bytes[0] == 0x42 && bytes[1] == 0x4D)
        {
            return "image/bmp";
        }

        return DetectMimeTypeFromExtension(extension);

    }

    private static string DetectMimeTypeFromExtension(string extension) =>
        extension.ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            _ => "application/octet-stream",
        };

    /// <summary>Human-readable byte count for themed staging feedback lines.</summary>
    public static string FormatByteCount(long bytes)
    {
        if (bytes >= 1024 * 1024)
        {
            return $"{bytes / (1024.0 * 1024.0):0.#} MiB";
        }

        if (bytes >= 1024)
        {
            return $"{bytes / 1024.0:0.#} KiB";
        }

        return $"{bytes} B";
    }

}
