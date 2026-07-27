using System.Security.Cryptography;

using RetroDownfall.Arcanum.Infrastructure.Security;

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
    /// <param name="beforeMove">
    /// Optional final hook invoked after destination revalidation and immediately before the atomic
    /// rename. This exists for deterministic race testing; normal callers leave it unset.
    /// </param>
    /// <param name="afterMoveBeforeVerify">
    /// Optional hook invoked after the rename but before mandatory staged identity/content
    /// verification. This exists for deterministic race testing; normal callers leave it unset.
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
        Func<bool>? afterReplace = null,
        Action? beforeMove = null,
        Action? afterMoveBeforeVerify = null)
    {

        bool replaced = false;

        string? backupPath = null;

        bool destinationExisted = false;

        FileHandleMetadata expectedDestinationMetadata = default;

        FileHandleIdentity stagedIdentity = default;

        StagedFileFingerprint stagedFingerprint = default;

        UnixFileMode? expectedUnixMode = null;

        try
        {

            destinationExisted = File.Exists(destinationPath);

            if (destinationExisted)
            {

                if (!TryCaptureMutationMetadata(
                        destinationPath,
                        out expectedDestinationMetadata,
                        out expectedUnixMode))
                {

                    return AtomicReplaceStatus.Aborted;

                }

            }
            else if (Directory.Exists(destinationPath))
            {

                return AtomicReplaceStatus.Aborted;

            }

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

            if (!TryRevalidateMutationTarget(
                    destinationPath,
                    destinationExisted,
                    expectedDestinationMetadata)
                || !TryApplyPreservedUnixMode(tempPath, expectedUnixMode)
                || !FileHandleIdentityInterop.TryGetPathMetadata(
                    tempPath,
                    out FileHandleMetadata stagedMetadata)
                || stagedMetadata.HardLinkCount != 1
                || stagedMetadata.Kind != FileSystemObjectKind.RegularFile)
            {

                return AtomicReplaceStatus.Aborted;

            }

            stagedIdentity = stagedMetadata.Identity;

            if (!TryCaptureFileFingerprint(
                    tempPath,
                    stagedIdentity,
                    out stagedFingerprint))
            {

                return AtomicReplaceStatus.Aborted;

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

                if (!TryApplyPreservedUnixMode(backupPath, expectedUnixMode))
                {

                    return AtomicReplaceStatus.Aborted;

                }

            }

            if (!TryRevalidateMutationTarget(
                    destinationPath,
                    destinationExisted,
                    expectedDestinationMetadata))
            {

                return AtomicReplaceStatus.Aborted;

            }

            beforeMove?.Invoke();

            File.Move(
                tempPath,
                destinationPath,
                overwrite: destinationExisted);

            replaced = true;

            afterMoveBeforeVerify?.Invoke();

            if (!TryVerifyFileFingerprint(
                    destinationPath,
                    stagedFingerprint))
            {

                if (TryRestoreOrQuarantine(
                        destinationPath,
                        backupPath,
                        stagedIdentity,
                        retainQuarantine: true))
                {

                    backupPath = null;

                    return AtomicReplaceStatus.RolledBack;

                }

                backupPath = null;

                return AtomicReplaceStatus.ReplacedButUnverified;

            }

            if (afterReplace is not null && !afterReplace())
            {

                if (TryRestoreOrQuarantine(
                        destinationPath,
                        backupPath,
                        stagedIdentity,
                        retainQuarantine: false))
                {

                    backupPath = null;

                    return AtomicReplaceStatus.RolledBack;

                }

                // Keep the backup file for operator recovery; do not delete it in finally.
                backupPath = null;

                return AtomicReplaceStatus.ReplacedButUnverified;

            }

            if (!TryVerifyFileFingerprint(
                    destinationPath,
                    stagedFingerprint))
            {

                if (TryRestoreOrQuarantine(
                        destinationPath,
                        backupPath,
                        stagedIdentity,
                        retainQuarantine: true))
                {

                    backupPath = null;

                    return AtomicReplaceStatus.RolledBack;

                }

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

    private static bool TryCaptureMutationMetadata(
        string destinationPath,
        out FileHandleMetadata metadata,
        out UnixFileMode? unixMode)
    {

        metadata = default;

        unixMode = null;

        if (!FileHandleIdentityInterop.TryGetPathMetadata(destinationPath, out metadata)
            || metadata.HardLinkCount != 1
            || metadata.Kind != FileSystemObjectKind.RegularFile)
        {

            return false;

        }

        if (OperatingSystem.IsWindows())
        {

            return true;

        }

        try
        {

            unixMode = File.GetUnixFileMode(destinationPath);

            return true;

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or PlatformNotSupportedException)
        {

            return false;

        }

    }

    private static bool TryRevalidateMutationTarget(
        string destinationPath,
        bool destinationExisted,
        FileHandleMetadata expectedMetadata)
    {

        if (!destinationExisted)
        {

            return !File.Exists(destinationPath)
                && !Directory.Exists(destinationPath);

        }

        return File.Exists(destinationPath)
            && FileHandleIdentityInterop.TryGetPathMetadata(
                destinationPath,
                out FileHandleMetadata currentMetadata)
            && currentMetadata.HardLinkCount == 1
            && FileHandleIdentity.IdentitiesMatch(
                expectedMetadata.Identity,
                currentMetadata.Identity);

    }

    private static bool TryApplyPreservedUnixMode(
        string tempPath,
        UnixFileMode? unixMode)
    {

        if (OperatingSystem.IsWindows() || unixMode is null)
        {

            return true;

        }

        try
        {

            File.SetUnixFileMode(tempPath, unixMode.Value);

            return true;

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or PlatformNotSupportedException)
        {

            return false;

        }

    }

    /// <summary>
    /// Best-effort recovery after a failed post-move check: restore the pre-move backup when one
    /// exists, otherwise quarantine (rename aside) or delete the unverified destination.
    /// </summary>
    /// <returns><see langword="true"/> when the destination no longer holds unverified content.</returns>
    private static bool TryRestoreOrQuarantine(
        string destinationPath,
        string? backupPath,
        FileHandleIdentity stagedIdentity,
        bool retainQuarantine)
    {

        if (!FileHandleIdentityInterop.TryGetPathMetadata(
                destinationPath,
                out FileHandleMetadata destinationMetadata)
            || destinationMetadata.HardLinkCount != 1
            || destinationMetadata.Kind != FileSystemObjectKind.RegularFile
            || !FileHandleIdentity.IdentitiesMatch(
                stagedIdentity,
                destinationMetadata.Identity))
        {

            return false;

        }

        string quarantinePath = Path.Combine(
            Path.GetDirectoryName(destinationPath) ?? string.Empty,
            $".arcanum-quarantine-{Guid.NewGuid():N}");

        try
        {

            File.Move(destinationPath, quarantinePath, overwrite: false);

            if (!FileHandleIdentityInterop.TryGetPathMetadata(
                    quarantinePath,
                    out FileHandleMetadata quarantinedMetadata)
                || quarantinedMetadata.HardLinkCount != 1
                || quarantinedMetadata.Kind != FileSystemObjectKind.RegularFile
                || !FileHandleIdentity.IdentitiesMatch(
                    stagedIdentity,
                    quarantinedMetadata.Identity))
            {

                return false;

            }

            if (backupPath is null)
            {

                return true;

            }

            File.Move(backupPath, destinationPath, overwrite: false);

            if (!retainQuarantine)
            {

                TryDeleteTempFile(quarantinePath);

            }

            return true;

        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {

            return false;

        }

    }

    private static bool TryCaptureFileFingerprint(
        string path,
        FileHandleIdentity expectedIdentity,
        out StagedFileFingerprint fingerprint)
    {

        fingerprint = default;

        try
        {

            if (!FileHandleIdentityInterop.TryGetPathMetadata(
                    path,
                    out FileHandleMetadata pathMetadata)
                || pathMetadata.HardLinkCount != 1
                || pathMetadata.Kind != FileSystemObjectKind.RegularFile
                || !FileHandleIdentity.IdentitiesMatch(
                    expectedIdentity,
                    pathMetadata.Identity))
            {

                return false;

            }

            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.SequentialScan);

            if (!FileHandleIdentityInterop.TryGetHandleMetadata(
                    stream.SafeFileHandle,
                    out FileHandleMetadata openedMetadata)
                || openedMetadata.HardLinkCount != 1
                || openedMetadata.Kind != FileSystemObjectKind.RegularFile
                || !FileHandleIdentity.IdentitiesMatch(
                    expectedIdentity,
                    openedMetadata.Identity))
            {

                return false;

            }

            long length = stream.Length;

            string contentSha256 =
                Convert.ToHexString(SHA256.HashData(stream));

            if (stream.Length != length
                || !FileHandleIdentityInterop.TryGetHandleMetadata(
                    stream.SafeFileHandle,
                    out FileHandleMetadata finalHandleMetadata)
                || !FileHandleIdentityInterop.TryGetPathMetadata(
                    path,
                    out FileHandleMetadata finalPathMetadata)
                || finalHandleMetadata.HardLinkCount != 1
                || finalPathMetadata.HardLinkCount != 1
                || finalHandleMetadata.Kind
                    != FileSystemObjectKind.RegularFile
                || finalPathMetadata.Kind
                    != FileSystemObjectKind.RegularFile
                || !FileHandleIdentity.IdentitiesMatch(
                    expectedIdentity,
                    finalHandleMetadata.Identity)
                || !FileHandleIdentity.IdentitiesMatch(
                    expectedIdentity,
                    finalPathMetadata.Identity))
            {

                return false;

            }

            fingerprint = new StagedFileFingerprint(
                expectedIdentity,
                length,
                contentSha256);

            return true;

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or PlatformNotSupportedException)
        {

            return false;

        }

    }

    private static bool TryVerifyFileFingerprint(
        string path,
        StagedFileFingerprint expected) =>
        TryCaptureFileFingerprint(
            path,
            expected.Identity,
            out StagedFileFingerprint current)
        && current == expected;

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

    private readonly record struct StagedFileFingerprint(
        FileHandleIdentity Identity,
        long Length,
        string ContentSha256);

}
