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
    /// this method return <see cref="AtomicReplaceStatus.Aborted"/> (the destination is left untouched).
    /// </param>
    /// <param name="afterReplace">
    /// Optional hook invoked immediately after a successful rename. Use it for post-move side effects
    /// such as permission hardening (perform the side effect and return <see langword="true"/>), or as
    /// a fail-closed post-move validation. Returning <see langword="false"/> triggers best-effort
    /// restore-from-backup or quarantine of the destination; the method then returns
    /// <see cref="AtomicReplaceStatus.RolledBack"/> or
    /// <see cref="AtomicReplaceStatus.ReplacedButUnverified"/> rather than a generic pre-move failure.
    /// </param>
    /// <returns>
    /// An <see cref="AtomicReplaceStatus"/> describing whether the destination was replaced and
    /// whether post-move verification (and any rollback) succeeded.
    /// </returns>
    public static async Task<AtomicReplaceStatus> ReplaceAsync(
        string destinationPath,
        string tempPath,
        Func<Stream, CancellationToken, Task> writeAsync,
        CancellationToken cancellationToken,
        Func<bool>? beforeReplace = null,
        Func<bool>? afterReplace = null)
    {

        bool replaced = false;

        string? backupPath = null;

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

                return AtomicReplaceStatus.Aborted;

            }

            if (File.Exists(destinationPath))
            {

                backupPath = Path.Combine(
                    Path.GetDirectoryName(destinationPath) ?? string.Empty,
                    $".arcanum-bak-{Guid.NewGuid():N}");

                File.Copy(destinationPath, backupPath, overwrite: false);

            }

            File.Move(tempPath, destinationPath, overwrite: true);

            replaced = true;

            if (afterReplace is not null && !afterReplace())
            {

                if (TryRestoreOrQuarantine(destinationPath, backupPath))
                {

                    backupPath = null;

                    return AtomicReplaceStatus.RolledBack;

                }

                // Keep the backup file for operator recovery; do not delete it in finally.
                backupPath = null;

                return AtomicReplaceStatus.ReplacedButUnverified;

            }

            return AtomicReplaceStatus.Succeeded;

        }
        finally
        {

            if (!replaced)
            {

                TryDeleteTempFile(tempPath);

            }

            if (backupPath is not null)
            {

                TryDeleteTempFile(backupPath);

            }

        }

    }

    /// <summary>
    /// Best-effort recovery after a failed post-move check: restore the pre-move backup when one
    /// exists, otherwise quarantine (rename aside) or delete the unverified destination.
    /// </summary>
    /// <returns><see langword="true"/> when the destination no longer holds unverified content.</returns>
    private static bool TryRestoreOrQuarantine(string destinationPath, string? backupPath)
    {

        if (backupPath is not null && File.Exists(backupPath))
        {

            try
            {

                File.Move(backupPath, destinationPath, overwrite: true);

                return true;

            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {

                // Fall through to quarantine/delete.
            }

        }

        if (!File.Exists(destinationPath))
        {

            return true;

        }

        string quarantinePath = Path.Combine(
            Path.GetDirectoryName(destinationPath) ?? string.Empty,
            $".arcanum-quarantine-{Guid.NewGuid():N}");

        try
        {

            File.Move(destinationPath, quarantinePath, overwrite: false);

            return true;

        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {

        }

        try
        {

            File.Delete(destinationPath);

            return true;

        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {

            return false;

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
