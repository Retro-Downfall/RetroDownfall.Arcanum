using Microsoft.Win32.SafeHandles;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Cli.Services;

/// <summary>
/// The one test of whether a path may be read as an attachment, shared by every surface that stages files
/// into a turn (the Command Center and <c>run --with</c>).
/// </summary>
/// <remarks>
/// A FIFO reports a length of 0, <c>File.Exists</c> says true for it, and opening it for reading blocks until
/// a writer appears, which nothing can interrupt. A device has no end. Only a regular file is a bounded
/// source, so everything else is refused before it is opened.
/// </remarks>
internal static class AttachableFile
{
    /// <summary>
    /// True when <paramref name="fullPath"/> (symbolic links followed) names a regular file. Otherwise
    /// <paramref name="reason"/> says why, in words that read after "is" and after "Cannot stage name:".
    /// </summary>
    internal static bool TryConfirmRegularFile(string fullPath, out string? reason)
    {
        reason = null;

        if (!FileHandleIdentityInterop.TryGetPathMetadata(fullPath, out FileHandleMetadata metadata))
        {
            reason = "not a file that could be inspected";
            return false;
        }

        if (!IsAttachableFileKind(
                metadata.Kind,
                OperatingSystem.IsWindows(),
                () => IsReparsePointWithoutTarget(fullPath)))
        {
            reason = "not a regular file";
            return false;
        }

        return true;
    }

    /// <summary>
    /// True when an entry of <paramref name="kind"/> may be read as an attachment. A regular file always
    /// may. On Unix every other kind (a FIFO, a socket, a device) may not. Windows is the exception: it
    /// reports every reparse point as <see cref="FileSystemObjectKind.Other"/>, including the ones that
    /// name no other location (a cloud placeholder, deduplicated or compressed data), which the operating
    /// system serves as an ordinary file and which <c>WorkspacePathPolicy</c> treats as one. So there
    /// <see cref="FileSystemObjectKind.Other"/> is attachable exactly when the entry is such a reparse
    /// point. Windows has no FIFO, and a directory is never attachable.
    /// </summary>
    /// <param name="kind">What the stat that follows symbolic links, or the opened handle, reported.</param>
    /// <param name="onWindows">Whether the platform is Windows.</param>
    /// <param name="isReparsePointWithoutTarget">
    /// Asked only when its answer decides the result: whether the entry is a reparse point that names no
    /// other location.
    /// </param>
    internal static bool IsAttachableFileKind(
        FileSystemObjectKind kind,
        bool onWindows,
        Func<bool> isReparsePointWithoutTarget)
    {
        ArgumentNullException.ThrowIfNull(isReparsePointWithoutTarget);

        return kind switch
        {
            FileSystemObjectKind.RegularFile => true,
            FileSystemObjectKind.Other => onWindows && isReparsePointWithoutTarget(),
            _ => false,
        };
    }

    /// <summary>
    /// Opens <paramref name="fullPath"/> for a sequential read and confirms that what was opened is
    /// attachable, so the read never depends on what the path named a moment earlier. Throws
    /// <see cref="IOException"/> when the open handle is not a regular file.
    /// </summary>
    /// <remarks>
    /// The stat in <see cref="TryConfirmRegularFile"/> and this open are two steps, and a path can change
    /// between them. Off Windows the open therefore never blocks: opening a FIFO for reading waits for a
    /// writer, and nothing can interrupt that wait, so the link chain is resolved to its final target and
    /// that target is opened non-blocking, no-follow, and judged by its own handle. Windows has no FIFO,
    /// so there an ordinary open is followed by the same judgement of the handle.
    /// </remarks>
    internal static FileStream OpenForRead(string fullPath, int bufferSize)
    {
        ArgumentException.ThrowIfNullOrEmpty(fullPath);

        FileStream stream = OperatingSystem.IsWindows()
            ? new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan)
            : OpenUnixWithoutBlocking(fullPath, bufferSize);

        if (!FileHandleIdentityInterop.TryGetHandleMetadata(stream.SafeFileHandle, out FileHandleMetadata opened)
            || !IsAttachableHandleKind(opened.Kind, OperatingSystem.IsWindows()))
        {
            stream.Dispose();
            throw new IOException(NotRegularFileMessage);
        }

        return stream;
    }

    private const string NotRegularFileMessage = "The path is not a regular file.";

    private static FileStream OpenUnixWithoutBlocking(string fullPath, int bufferSize)
    {
        // The no-follow open below refuses a link, so follow the chain here and open what it ends at.
        string target = new FileInfo(fullPath).ResolveLinkTarget(returnFinalTarget: true)?.FullName
            ?? fullPath;

        SecureFileOpenStatus status = FileHandleIdentityInterop.TryOpenReadOnlyNoFollow(
            target,
            out SafeFileHandle? handle);

        if (status is not SecureFileOpenStatus.Success || handle is null)
        {
            handle?.Dispose();
            throw new IOException(
                status is SecureFileOpenStatus.NotFound
                    ? "The path no longer exists."
                    : NotRegularFileMessage);
        }

        return new FileStream(handle, FileAccess.Read, bufferSize, isAsync: false);
    }

    /// <summary>
    /// True when an opened handle of <paramref name="kind"/> may be read as an attachment. A handle is the
    /// file itself: the operating system followed every link to reach it, so its own kind is the whole
    /// answer and there is no second look at the path to race. A regular file may be read. On Windows a
    /// handle that still reports a reparse point is one that names no other location (a cloud
    /// placeholder, deduplicated or compressed data), which the operating system serves as an ordinary
    /// file. Every other kind may not.
    /// </summary>
    internal static bool IsAttachableHandleKind(FileSystemObjectKind kind, bool onWindows) =>
        kind switch
        {
            FileSystemObjectKind.RegularFile => true,
            FileSystemObjectKind.Other => onWindows,
            _ => false,
        };

    /// <summary>
    /// True when the final target of <paramref name="path"/> is a file that carries the reparse-point
    /// attribute and is not itself a link: Windows' own symbolic links and junctions are followed to
    /// their target, so what remains is a reparse point that names no other location. Any failure to
    /// inspect the entry is a refusal.
    /// </summary>
    internal static bool IsReparsePointWithoutTarget(string path)
    {
        try
        {
            FileInfo named = new(path);
            FileSystemInfo entry = named.ResolveLinkTarget(returnFinalTarget: true) ?? named;
            FileAttributes attributes = entry.Attributes;

            return (attributes & FileAttributes.ReparsePoint) != 0
                && (attributes & FileAttributes.Directory) == 0
                && entry.LinkTarget is null;
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException
                or System.Security.SecurityException)
        {
            return false;
        }
    }
}
