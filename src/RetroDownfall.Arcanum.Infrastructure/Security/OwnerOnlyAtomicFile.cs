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

    private static void Publish(string tempPath, string path)
    {
        File.Move(tempPath, path, overwrite: true);

        SecureFilePermissions.ApplyOwnerOnlyFile(path);
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
        catch (IOException)
        {
            // Best-effort cleanup of an owner-only temporary file.
        }
    }
}
