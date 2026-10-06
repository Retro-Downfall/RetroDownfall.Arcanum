namespace RetroDownfall.Arcanum.Infrastructure.Security;

/// <summary>
/// The one "stage an owner-only temp file, flush it to disk, then atomically replace the target"
/// writer shared by every secret-bearing file Arcanum publishes: the Data Protection credential
/// mirrors and the Grimoire <c>.kdf</c> sidecar. A rename must never be able to outrun the bytes it
/// publishes, so the temp file is fsynced before the move.
/// </summary>
internal static class OwnerOnlyAtomicFile
{
    private static readonly AsyncLocal<Action<string>?> TempFileDeleteOverride = new();

    /// <summary>
    /// Test seam that replaces the best-effort temp-file delete for the current async flow only, so
    /// a test can reproduce a filesystem that refuses the cleanup without affecting parallel tests.
    /// </summary>
    internal static Action<string>? TempFileDeleteForTests
    {
        get => TempFileDeleteOverride.Value;

        set => TempFileDeleteOverride.Value = value;
    }

    internal static void Write(string path, ReadOnlySpan<byte> content)
    {
        string tempPath = TempPathFor(path);

        try
        {
            using (FileStream stream = SecureFilePermissions.CreateOwnerOnlyTempFile(tempPath))
            {
                stream.Write(content);

                stream.Flush(flushToDisk: true);
            }

            Publish(tempPath, path);
        }
        finally
        {
            DeleteTempFile(tempPath);
        }
    }

    internal static async Task WriteAsync(
        string path,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        string tempPath = TempPathFor(path);

        try
        {
            await using (FileStream stream = SecureFilePermissions.CreateOwnerOnlyTempFile(tempPath))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);

                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

                // FlushAsync only drains the managed buffer to the OS. Durable flush before the atomic
                // replace so an unclean power loss cannot leave a present-but-empty file behind the
                // committed rename.
                stream.Flush(flushToDisk: true);
            }

            Publish(tempPath, path);
        }
        finally
        {
            DeleteTempFile(tempPath);
        }
    }

    private static string TempPathFor(string path) => path + ".tmp." + Guid.NewGuid().ToString("N");

    /// <summary>
    /// Requires the staged file's owner-only posture, then replaces the target with it. A
    /// secret-bearing file whose posture cannot be verified fails the write before anything is
    /// published: verifying after the rename reported a failed save whose new bytes were already live.
    /// The rename keeps the staged file's mode, owner and ACL, so what was verified is what is published.
    /// </summary>
    private static void Publish(string tempPath, string path)
    {
        SecureFilePermissions.RequireOwnerOnlyFile(tempPath);

        File.Move(tempPath, path, overwrite: true);
    }

    private static void DeleteTempFile(string tempPath)
    {
        if (!File.Exists(tempPath))
        {
            return;
        }

        try
        {
            (TempFileDeleteForTests ?? File.Delete)(tempPath);
        }
        catch (Exception cleanupFailure)
            when (cleanupFailure is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup of an owner-only temporary file. UnauthorizedAccessException belongs
            // here as much as IOException: Windows raises it for a delete the filesystem refuses, and
            // an uncaught throw from this finally would replace the exception that explains why the
            // write failed with one about tidying up afterwards.
        }
    }
}
