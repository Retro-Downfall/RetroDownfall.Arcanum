using System.Text;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Containment and size bound for prompt-test codex file reads.
/// </summary>
public readonly record struct CodexValidationResult
{

    public required string Path { get; init; }

    public required long MaxBytes { get; init; }

}

/// <summary>
/// Containment and size checks for prompt-test codex file reads.
/// </summary>
public static class CodexPathPolicy
{

    /// <summary>
    /// Validates containment and returns the canonical path plus the read bound. Callers must
    /// enforce the returned <see cref="CodexValidationResult.MaxBytes"/> limit when reading
    /// the file to avoid a TOCTOU race between the file-size check and the actual read.
    /// </summary>
    public static Result<CodexValidationResult> ValidateContainedFile(
        string codexPath,
        string containmentRootFullPath,
        long maxFileReadSizeBytes)
    {
        if (string.IsNullOrWhiteSpace(codexPath))
        {
            return Result<CodexValidationResult>.Failure(new Error("Prompt.CodexPathRequired", "Codex path is required."));
        }

        if (string.IsNullOrWhiteSpace(containmentRootFullPath))
        {
            return Result<CodexValidationResult>.Failure(
                new Error(
                    ErrorCodes.Prompt.CodexPathNotContained,
                    "A workspace or campaign root is required to read a codex file."));
        }

        string normalizedCodex;

        try
        {
            normalizedCodex = Path.GetFullPath(codexPath.Trim());
        }
        catch (Exception)
        {
            return Result<CodexValidationResult>.Failure(new Error("Prompt.CodexPathInvalid", "The codex path is invalid."));
        }

        string normalizedRoot;

        try
        {
            normalizedRoot = Path.GetFullPath(containmentRootFullPath.Trim());
        }
        catch (Exception)
        {
            return Result<CodexValidationResult>.Failure(
                new Error(
                    ErrorCodes.Prompt.CodexPathNotContained,
                    "A workspace or campaign root is required to read a codex file."));
        }

        if (!WorkspaceRootPolicy.IsUnderAnyAllowedRoot(normalizedCodex, [normalizedRoot]))
        {
            return Result<CodexValidationResult>.Failure(
                new Error(
                    ErrorCodes.Prompt.CodexPathNotContained,
                    "The codex path must be under the prompt campaign or working directory."));
        }

        if (!File.Exists(normalizedCodex))
        {
            return Result<CodexValidationResult>.Failure(new Error("Prompt.CodexPathNotFound", "The codex file was not found."));
        }

        return Result<CodexValidationResult>.Success(
            new CodexValidationResult
            {
                Path = normalizedCodex,
                MaxBytes = maxFileReadSizeBytes,
            });
    }

    /// <summary>
    /// Reads at most <paramref name="maxBytes"/> UTF-8 bytes from <paramref name="path"/>.
    /// </summary>
    /// <remarks>
    /// Operator cancellation propagates as <see cref="OperationCanceledException"/>; it is never rewritten
    /// into a domain failure. A file that exists but cannot be opened or read fails as
    /// <c>Prompt.CodexReadFailed</c> rather than <c>Prompt.CodexPathNotFound</c>, so a permission or I/O
    /// problem does not send the operator hunting for a path that is already known to resolve.
    /// </remarks>
    public static async Task<Result<string>> ReadCappedAsync(string path, long maxBytes, CancellationToken cancellationToken = default)
    {

        if (maxBytes < 0)
        {

            return Result<string>.Failure(
                new Error("Prompt.CodexReadTooLarge", "The codex read size bound is invalid."));

        }

        try
        {

            await using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            // maxBytes + 1 overflows to a negative length at long.MaxValue, so bound before widening.
            byte[] buffer = new byte[maxBytes >= 4096L ? 4096 : (int)maxBytes + 1];
            using MemoryStream accumulator = new();
            long bytesRead = 0L;

            while (true)
            {

                int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);

                if (read == 0)
                {
                    break;
                }

                bytesRead += read;

                if (bytesRead > maxBytes)
                {

                    return Result<string>.Failure(
                        new Error("Prompt.CodexPathTooLarge", "The codex file exceeds the maximum read size limit."));

                }

                await accumulator.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);

            }

            return Result<string>.Success(Encoding.UTF8.GetString(accumulator.ToArray()));

        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {

            return Result<string>.Failure(new Error("Prompt.CodexPathNotFound", "The codex file was not found."));

        }
        catch (Exception ex) when (ex is UnauthorizedAccessException
            or IOException
            or NotSupportedException
            or ArgumentException
            or PathTooLongException)
        {

            return Result<string>.Failure(
                new Error("Prompt.CodexReadFailed", "The codex file could not be read."));

        }

    }

}
