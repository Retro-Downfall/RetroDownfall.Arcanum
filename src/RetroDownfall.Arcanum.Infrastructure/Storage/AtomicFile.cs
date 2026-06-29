namespace RetroDownfall.Arcanum.Infrastructure.Storage;

/// <summary>
/// Durable, atomic file replacement shared by every Arcanum write path. Content is written to a
/// caller-provided, same-directory temporary file (flushed to disk with write-through), then the
/// destination is atomically replaced via <see cref="File.Move(string, string, bool)"/>, so a crash
/// mid-write can never leave a partially written or truncated destination. The temporary file is
/// removed on any failure that occurs before the move completes.
/// </summary>
/// <remarks>
/// The caller is responsible for ensuring the destination directory exists (each call site applies
/// the directory-creation policy it needs, e.g. owner-only permissions) and for supplying a
/// <paramref name="tempPath"/> in the SAME directory as <paramref name="destinationPath"/> — that
/// same-filesystem placement is what makes the rename atomic and crash-safe. The
/// <paramref name="writeAsync"/> callback must write to the supplied stream but MUST NOT dispose or
/// close it: the helper owns the stream lifetime and performs the durability flush.
/// </remarks>
internal static class AtomicFile
{

    /// <summary>
    /// Writes <paramref name="writeAsync"/>'s content to <paramref name="tempPath"/> and atomically
    /// replaces <paramref name="destinationPath"/> with it.
    /// </summary>
    /// <param name="beforeReplace">
    /// Optional gate invoked after the temp file is fully written, flushed, and closed but before the
    /// rename. Returning <see langword="false"/> aborts the replace, deletes the temp file, and makes
    /// this method return <see langword="false"/> (the destination is left untouched).
    /// </param>
    /// <param name="afterReplace">
    /// Optional hook invoked immediately after a successful rename. Use it for post-move side effects
    /// such as permission hardening (perform the side effect and return <see langword="true"/>), or as
    /// a fail-closed post-move validation (returning <see langword="false"/> makes this method return
    /// <see langword="false"/> even though the destination has already been replaced).
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the destination was replaced and both hooks (if supplied) approved;
    /// otherwise <see langword="false"/>.
    /// </returns>
    public static async Task<bool> ReplaceAsync(
        string destinationPath,
        string tempPath,
        Func<Stream, CancellationToken, Task> writeAsync,
        CancellationToken cancellationToken,
        Func<bool>? beforeReplace = null,
        Func<bool>? afterReplace = null)
    {

        bool replaced = false;

        try
        {

            await using (FileStream stream = new(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {

                await writeAsync(stream, cancellationToken).ConfigureAwait(false);

                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

            }

            if (beforeReplace is not null && !beforeReplace())
            {

                return false;

            }

            File.Move(tempPath, destinationPath, overwrite: true);

            replaced = true;

            if (afterReplace is not null && !afterReplace())
            {

                return false;

            }

            return true;

        }
        finally
        {

            if (!replaced)
            {

                TryDeleteTempFile(tempPath);

            }

        }

    }

    private static void TryDeleteTempFile(string tempPath)
    {

        try
        {

            if (File.Exists(tempPath))
            {

                File.Delete(tempPath);

            }

        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {

            // Best-effort cleanup: a leftover temp file is harmless and will be overwritten on retry.

        }

    }

}
