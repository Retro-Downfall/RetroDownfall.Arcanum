using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Cli.Commands;

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
