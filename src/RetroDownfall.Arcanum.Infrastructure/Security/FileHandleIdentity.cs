using System.Buffers.Binary;
using System.Runtime.InteropServices;

using Microsoft.Win32.SafeHandles;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

/// <summary>
/// Stable volume + file identity for post-open sandbox revalidation
/// (dev/ino on Unix, volume serial + file index on Windows).
/// </summary>
internal readonly record struct FileHandleIdentity(ulong VolumeId, ulong FileId)
{

    internal static bool IdentitiesMatch(FileHandleIdentity expected, FileHandleIdentity actual) =>
        expected == actual;

}

internal enum FileSystemObjectKind
{
    RegularFile,
    Directory,
    Other,
}

/// <summary>
/// Stable identity plus the hard-link count observed by the same metadata query.
/// Mutation callers fail closed when this metadata is unavailable and reject regular
/// files with more than one link. Traversal de-duplication cannot protect aliases
/// outside the enumerated workspace.
/// </summary>
internal readonly record struct FileHandleMetadata(
    FileHandleIdentity Identity,
    ulong HardLinkCount,
    FileSystemObjectKind Kind = FileSystemObjectKind.RegularFile);

internal readonly record struct WindowsFileInformationLayout(
    int Size,
    int CreationTimeOffset,
    int LastAccessTimeOffset,
    int LastWriteTimeOffset,
    int VolumeSerialNumberOffset,
    int NumberOfLinksOffset,
    int FileIndexHighOffset,
    int FileIndexLowOffset);

/// <summary>
/// Platform interop for resolving file identities and hard-link counts from paths and handles.
/// </summary>
internal static partial class FileHandleIdentityInterop
{

    /// <summary>
    /// Test seam for path identity resolution branches.
    /// </summary>
    internal static Func<string, FileHandleIdentity?>? TryGetPathIdentityForTests { get; set; }

    /// <summary>
    /// Test seam for handle identity resolution branches.
    /// </summary>
    internal static Func<SafeFileHandle, FileHandleIdentity?>? TryGetHandleIdentityForTests { get; set; }

    /// <summary>
    /// Test seam for path identity and hard-link metadata branches.
    /// </summary>
    internal static Func<string, FileHandleMetadata?>? TryGetPathMetadataForTests { get; set; }

    /// <summary>
    /// Test seam for no-follow path metadata branches.
    /// </summary>
    internal static Func<string, FileHandleMetadata?>?
        TryGetPathMetadataNoFollowForTests
    { get; set; }

    /// <summary>
    /// Test seam for handle identity and hard-link metadata branches.
    /// </summary>
    internal static Func<SafeFileHandle, FileHandleMetadata?>? TryGetHandleMetadataForTests { get; set; }

    internal static WindowsFileInformationLayout GetWindowsFileInformationLayoutForTests() =>
        new(
            Marshal.SizeOf<BY_HANDLE_FILE_INFORMATION>(),
            Marshal.OffsetOf<BY_HANDLE_FILE_INFORMATION>(
                nameof(BY_HANDLE_FILE_INFORMATION.ftCreationTime)).ToInt32(),
            Marshal.OffsetOf<BY_HANDLE_FILE_INFORMATION>(
                nameof(BY_HANDLE_FILE_INFORMATION.ftLastAccessTime)).ToInt32(),
            Marshal.OffsetOf<BY_HANDLE_FILE_INFORMATION>(
                nameof(BY_HANDLE_FILE_INFORMATION.ftLastWriteTime)).ToInt32(),
            Marshal.OffsetOf<BY_HANDLE_FILE_INFORMATION>(
                nameof(BY_HANDLE_FILE_INFORMATION.dwVolumeSerialNumber)).ToInt32(),
            Marshal.OffsetOf<BY_HANDLE_FILE_INFORMATION>(
                nameof(BY_HANDLE_FILE_INFORMATION.nNumberOfLinks)).ToInt32(),
            Marshal.OffsetOf<BY_HANDLE_FILE_INFORMATION>(
                nameof(BY_HANDLE_FILE_INFORMATION.nFileIndexHigh)).ToInt32(),
            Marshal.OffsetOf<BY_HANDLE_FILE_INFORMATION>(
                nameof(BY_HANDLE_FILE_INFORMATION.nFileIndexLow)).ToInt32());

    internal static bool TryParseUnixFileMetadataForTests(
        ReadOnlySpan<byte> buffer,
        bool isMacOS,
        Architecture architecture,
        out FileHandleMetadata metadata) =>
        TryReadUnixFileMetadata(buffer, isMacOS, architecture, out metadata);

    internal static bool TryGetUnixOwnerUserId(
        string path,
        out uint ownerUserId)
    {

        ownerUserId = default;

        if (OperatingSystem.IsWindows()
            || !BitConverter.IsLittleEndian)
        {

            return false;
        }

        unsafe
        {

            byte[] buffer = new byte[StatBufferSize];

            fixed (byte* bufferPtr = buffer)
            {

                if (stat(path, bufferPtr) != 0)
                {

                    return false;
                }

            }

            int offset = OperatingSystem.IsMacOS()
                ? 16
                : RuntimeInformation.ProcessArchitecture == Architecture.X64
                    ? 28
                    : RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                        ? 24
                        : -1;

            if (offset < 0 || buffer.Length < offset + sizeof(uint))
            {

                return false;
            }

            ownerUserId = BinaryPrimitives.ReadUInt32LittleEndian(
                buffer.AsSpan(offset));
            return true;
        }
    }

    internal static bool TryGetPathIdentity(string path, out FileHandleIdentity identity)
    {

        identity = default;

        if (TryGetPathIdentityForTests is not null)
        {

            FileHandleIdentity? testIdentity = TryGetPathIdentityForTests(path);

            if (testIdentity is null)
            {

                return false;

            }

            identity = testIdentity.Value;

            return true;

        }

        if (!TryGetPathMetadata(path, out FileHandleMetadata metadata))
        {

            return false;

        }

        identity = metadata.Identity;

        return true;

    }

    internal static bool TryGetHandleIdentity(
        SafeFileHandle handle,
        out FileHandleIdentity identity)
    {

        identity = default;

        if (handle is null || handle.IsInvalid)
        {

            return false;

        }

        if (TryGetHandleIdentityForTests is not null)
        {

            FileHandleIdentity? testIdentity = TryGetHandleIdentityForTests(handle);

            if (testIdentity is null)
            {

                return false;

            }

            identity = testIdentity.Value;

            return true;

        }

        if (!TryGetHandleMetadata(handle, out FileHandleMetadata metadata))
        {

            return false;

        }

        identity = metadata.Identity;

        return true;

    }

    internal static bool TryGetPathMetadata(string path, out FileHandleMetadata metadata)
    {

        metadata = default;

        if (TryGetPathMetadataForTests is not null)
        {

            FileHandleMetadata? testMetadata = TryGetPathMetadataForTests(path);

            if (testMetadata is null)
            {

                return false;

            }

            metadata = testMetadata.Value;

            return true;

        }

        if (OperatingSystem.IsWindows())
        {

            return TryGetWindowsPathMetadata(path, out metadata);

        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {

            return TryGetUnixPathMetadata(path, out metadata);

        }

        return false;

    }

    internal static bool TryGetPathMetadataNoFollow(
        string path,
        out FileHandleMetadata metadata)
    {
        metadata = default;

        if (TryGetPathMetadataNoFollowForTests is not null)
        {
            FileHandleMetadata? testMetadata =
                TryGetPathMetadataNoFollowForTests(path);

            if (testMetadata is null)
            {
                return false;
            }

            metadata = testMetadata.Value;

            return true;
        }

        return TryGetPathMetadataNoFollowIgnoringTestSeam(
            path,
            out metadata);
    }

    /// <summary>
    /// The real platform lookup, bypassing <see cref="TryGetPathMetadataNoFollowForTests"/>.
    ///
    /// A test seam that fakes one path still has to answer honestly for every other path, and the
    /// only way to do that used to be to null the seam, call back in, and restore it. That is a
    /// write to process-global state performed on whichever thread happened to ask — so a
    /// concurrently running test could observe the seam missing, and two such calls could interleave
    /// their save/restore and clear it permanently. Both showed up as an unrelated test failing
    /// intermittently. Seam implementations call this instead and never touch the global.
    /// </summary>
    internal static bool TryGetPathMetadataNoFollowIgnoringTestSeam(
        string path,
        out FileHandleMetadata metadata)
    {
        metadata = default;

        if (OperatingSystem.IsWindows())
        {
            return TryGetWindowsPathMetadataNoFollow(
                path,
                out metadata);
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            return TryGetUnixPathMetadataNoFollow(
                path,
                out metadata);
        }

        return false;
    }

    internal static bool TryGetHandleMetadata(
        SafeFileHandle handle,
        out FileHandleMetadata metadata)
    {

        metadata = default;

        if (handle is null || handle.IsInvalid)
        {

            return false;

        }

        if (TryGetHandleMetadataForTests is not null)
        {

            FileHandleMetadata? testMetadata = TryGetHandleMetadataForTests(handle);

            if (testMetadata is null)
            {

                return false;

            }

            metadata = testMetadata.Value;

            return true;

        }

        return TryGetHandleMetadataIgnoringTestSeam(handle, out metadata);

    }

    private static bool TryGetHandleMetadataIgnoringTestSeam(
        SafeFileHandle handle,
        out FileHandleMetadata metadata)
    {

        metadata = default;

        if (handle is null || handle.IsInvalid || handle.IsClosed)
        {

            return false;

        }

        if (OperatingSystem.IsWindows())
        {

            return TryGetWindowsHandleMetadata(handle, out metadata);

        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {

            return TryGetUnixHandleMetadata(handle, out metadata);

        }

        return false;

    }

    internal static bool TryGetUnixHandleAccessMetadata(
        SafeFileHandle handle,
        out UnixFileMode mode,
        out uint ownerUserId)
    {

        mode = default;

        ownerUserId = default;

        if ((!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            || !BitConverter.IsLittleEndian
            || handle is null
            || handle.IsInvalid)
        {

            return false;

        }

        int fd = handle.DangerousGetHandle().ToInt32();

        if (fd < 0)
        {

            return false;

        }

        unsafe
        {

            Span<byte> buffer = stackalloc byte[StatBufferSize];

            fixed (byte* bufferPtr = buffer)
            {

                if (fstat(fd, bufferPtr) != 0)
                {

                    return false;

                }

                uint rawMode;

                if (OperatingSystem.IsMacOS())
                {

                    if (buffer.Length < MacOsAccessMetadataMinimumSize)
                    {

                        return false;

                    }

                    rawMode = BinaryPrimitives.ReadUInt16LittleEndian(buffer[4..]);

                    ownerUserId = BinaryPrimitives.ReadUInt32LittleEndian(buffer[16..]);

                }
                else
                {

                    switch (RuntimeInformation.ProcessArchitecture)
                    {
                        case Architecture.X64
                            when buffer.Length >= LinuxX64AccessMetadataMinimumSize:

                            rawMode = BinaryPrimitives.ReadUInt32LittleEndian(buffer[24..]);

                            ownerUserId = BinaryPrimitives.ReadUInt32LittleEndian(buffer[28..]);

                            break;

                        case Architecture.Arm64
                            when buffer.Length >= LinuxArm64AccessMetadataMinimumSize:

                            rawMode = BinaryPrimitives.ReadUInt32LittleEndian(buffer[16..]);

                            ownerUserId = BinaryPrimitives.ReadUInt32LittleEndian(buffer[24..]);

                            break;

                        default:

                            return false;
                    }

                }

                mode = (UnixFileMode)(rawMode & UnixPermissionBits);

                return true;

            }

        }

    }

    internal static bool TryOpenDirectoryMetadata(
        string path,
        out SafeFileHandle handle,
        out FileHandleMetadata metadata)
    {
        handle = new SafeFileHandle(new IntPtr(-1), ownsHandle: true);
        metadata = default;

        try
        {
            if (OperatingSystem.IsWindows())
            {
                handle = CreateFile(
                    path,
                    FileReadAttributes,
                    FileShare.Read | FileShare.Write | FileShare.Delete,
                    IntPtr.Zero,
                    OpenExisting,
                    FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                    IntPtr.Zero);
            }
            else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                int flags = OperatingSystem.IsMacOS()
                    ? MacOsOpenDirectory | MacOsOpenNoFollow | MacOsOpenCloseOnExec
                    : LinuxOpenDirectory | LinuxOpenNoFollow | LinuxOpenCloseOnExec;
                int fileDescriptor = OpenUnix(path, flags, mode: 0);
                if (fileDescriptor < 0)
                {
                    return false;
                }

                handle.Dispose();
                handle = new SafeFileHandle(
                    new IntPtr(fileDescriptor),
                    ownsHandle: true);
            }
            else
            {
                return false;
            }

            if (handle.IsInvalid
                || !TryGetHandleMetadataIgnoringTestSeam(handle, out metadata)
                || metadata.Kind != FileSystemObjectKind.Directory)
            {
                handle.Dispose();
                handle = new SafeFileHandle(new IntPtr(-1), ownsHandle: true);
                metadata = default;
                return false;
            }

            return true;
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
        {
            handle.Dispose();
            handle = new SafeFileHandle(new IntPtr(-1), ownsHandle: true);
            metadata = default;
            return false;
        }
    }

    internal static bool TryOpenDirectoryMetadataRelative(
        SafeFileHandle parentDirectory,
        string leaf,
        bool requestReadControl,
        out SafeFileHandle handle,
        out FileHandleMetadata metadata)
    {

        handle = new SafeFileHandle(new IntPtr(-1), ownsHandle: true);

        metadata = default;

        if (parentDirectory is null
            || parentDirectory.IsInvalid
            || parentDirectory.IsClosed
            || string.IsNullOrEmpty(leaf)
            || leaf is "." or ".."
            || leaf.IndexOfAny('/', '\\', '\0') >= 0)
        {

            return false;

        }

        bool parentReferenceAdded = false;

        try
        {

            parentDirectory.DangerousAddRef(ref parentReferenceAdded);

            if (OperatingSystem.IsWindows())
            {

                if (!TryOpenWindowsDirectoryRelative(
                        parentDirectory,
                        leaf,
                        requestReadControl,
                        out handle))
                {

                    return false;

                }

            }
            else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {

                int parentDescriptor = parentDirectory.DangerousGetHandle().ToInt32();

                if (parentDescriptor < 0)
                {

                    return false;

                }

                int flags = OperatingSystem.IsMacOS()
                    ? MacOsOpenDirectory | MacOsOpenNoFollow | MacOsOpenCloseOnExec
                    : LinuxOpenDirectory | LinuxOpenNoFollow | LinuxOpenCloseOnExec;

                int fileDescriptor = OpenAtUnix(
                    parentDescriptor,
                    leaf,
                    flags,
                    mode: 0);

                if (fileDescriptor < 0)
                {

                    return false;

                }

                handle.Dispose();

                handle = new SafeFileHandle(
                    new IntPtr(fileDescriptor),
                    ownsHandle: true);

            }
            else
            {

                return false;

            }

            if (handle.IsInvalid
                || !TryGetHandleMetadata(handle, out metadata)
                || metadata.Kind is not FileSystemObjectKind.Directory)
            {

                handle.Dispose();

                handle = new SafeFileHandle(new IntPtr(-1), ownsHandle: true);

                metadata = default;

                return false;

            }

            return true;

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or ObjectDisposedException
                or NotSupportedException)
        {

            handle.Dispose();

            handle = new SafeFileHandle(new IntPtr(-1), ownsHandle: true);

            metadata = default;

            return false;

        }
        finally
        {

            if (parentReferenceAdded)
            {

                parentDirectory.DangerousRelease();

            }

        }

    }

    private static unsafe bool TryOpenWindowsDirectoryRelative(
        SafeFileHandle parentDirectory,
        string leaf,
        bool requestReadControl,
        out SafeFileHandle handle)
    {

        handle = new SafeFileHandle(new IntPtr(-1), ownsHandle: true);

        fixed (char* leafBuffer = leaf)
        {

            UNICODE_STRING objectName = new()
            {
                Length = checked((ushort)(leaf.Length * sizeof(char))),
                MaximumLength = checked((ushort)(leaf.Length * sizeof(char))),
                Buffer = leafBuffer,
            };

            OBJECT_ATTRIBUTES objectAttributes = new()
            {
                Length = (uint)sizeof(OBJECT_ATTRIBUTES),
                RootDirectory = parentDirectory.DangerousGetHandle(),
                ObjectName = &objectName,
                Attributes = ObjectAttributeCaseInsensitive,
            };

            IO_STATUS_BLOCK statusBlock = default;

            uint desiredAccess = FileReadAttributes | FileTraverse;

            if (requestReadControl)
            {

                desiredAccess |= ReadControl;

            }

            int status = NtOpenFile(
                out IntPtr opened,
                desiredAccess,
                &objectAttributes,
                &statusBlock,
                (uint)(FileShare.Read | FileShare.Write | FileShare.Delete),
                FileDirectoryFile | FileOpenReparsePoint);

            if (status < 0 || opened == IntPtr.Zero || opened == new IntPtr(-1))
            {

                if (opened != IntPtr.Zero && opened != new IntPtr(-1))
                {

                    new SafeFileHandle(opened, ownsHandle: true).Dispose();

                }

                return false;

            }

            handle.Dispose();

            handle = new SafeFileHandle(opened, ownsHandle: true);

            return true;

        }

    }

    internal static SecureFileOpenStatus TryOpenReadOnlyNoFollow(
        string path,
        out SafeFileHandle? handle)
    {
        if (OperatingSystem.IsWindows())
        {
            handle = CreateFile(
                path,
                GenericRead,
                // Delete alongside Read so a caller holding this handle can still rename or unlink the
                // entry it opened. Windows refuses both to everyone while any handle omits
                // FILE_SHARE_DELETE, so without it the marker quarantine rename and the compare-delete
                // both fail against a marker this function opened. Write sharing stays denied, which is
                // the exclusion that matters: nobody may change the bytes this handle proved.
                FileShare.Read | FileShare.Delete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagOpenReparsePoint
                    | FileFlagOverlapped
                    | FileFlagSequentialScan
                    | FileFlagBackupSemantics,
                IntPtr.Zero);

            if (!handle.IsInvalid)
            {
                return SecureFileOpenStatus.Success;
            }

            int error = Marshal.GetLastPInvokeError();

            handle.Dispose();

            handle = null;

            return error switch
            {
                ErrorFileNotFound or ErrorPathNotFound =>
                    SecureFileOpenStatus.NotFound,
                ErrorAccessDenied =>
                    SecureFileOpenStatus.AccessDenied,
                _ => SecureFileOpenStatus.IoError,
            };
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            bool isMacOS = OperatingSystem.IsMacOS();

            int flags = isMacOS
                ? MacOsOpenNoFollow
                    | MacOsOpenNonBlocking
                    | MacOsOpenCloseOnExec
                : LinuxOpenNoFollow
                    | LinuxOpenNonBlocking
                    | LinuxOpenCloseOnExec;

            int fileDescriptor = OpenUnix(
                path,
                flags,
                mode: 0);

            if (fileDescriptor >= 0)
            {
                handle = new SafeFileHandle(
                    new IntPtr(fileDescriptor),
                    ownsHandle: true);

                return SecureFileOpenStatus.Success;
            }

            handle = null;

            int error = Marshal.GetLastPInvokeError();

            return error switch
            {
                UnixErrorNoEntry or UnixErrorNotDirectory =>
                    SecureFileOpenStatus.NotFound,
                UnixErrorPermissionDenied
                    or UnixErrorOperationNotPermitted =>
                    SecureFileOpenStatus.AccessDenied,
                LinuxErrorTooManySymbolicLinks
                    when !isMacOS =>
                    SecureFileOpenStatus.Rejected,
                MacOsErrorTooManySymbolicLinks
                    when isMacOS =>
                    SecureFileOpenStatus.Rejected,
                _ => SecureFileOpenStatus.IoError,
            };
        }

        handle = null;

        return SecureFileOpenStatus.Rejected;
    }

    internal static SecureFileOpenStatus TryOpenReadOnlyNoFollowRelative(
        SafeFileHandle parentDirectory,
        string leaf,
        out SafeFileHandle? handle)
    {

        handle = null;

        if (parentDirectory is null
            || parentDirectory.IsInvalid
            || parentDirectory.IsClosed
            || string.IsNullOrEmpty(leaf)
            || leaf is "." or ".."
            || leaf.IndexOfAny('/', '\\', '\0') >= 0)
        {

            return SecureFileOpenStatus.Rejected;

        }

        bool parentReferenceAdded = false;

        SafeFileHandle? opened = null;

        try
        {

            parentDirectory.DangerousAddRef(ref parentReferenceAdded);

            SecureFileOpenStatus status;

            if (OperatingSystem.IsWindows())
            {

                status = TryOpenWindowsReadOnlyRelative(
                    parentDirectory,
                    leaf,
                    out opened);

            }
            else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {

                int parentDescriptor = parentDirectory.DangerousGetHandle().ToInt32();

                if (parentDescriptor < 0)
                {

                    return SecureFileOpenStatus.Rejected;

                }

                bool isMacOS = OperatingSystem.IsMacOS();

                int flags = isMacOS
                    ? MacOsOpenNoFollow
                        | MacOsOpenNonBlocking
                        | MacOsOpenCloseOnExec
                    : LinuxOpenNoFollow
                        | LinuxOpenNonBlocking
                        | LinuxOpenCloseOnExec;

                int fileDescriptor = OpenAtUnix(
                    parentDescriptor,
                    leaf,
                    flags,
                    mode: 0);

                if (fileDescriptor < 0)
                {

                    int error = Marshal.GetLastPInvokeError();

                    return MapUnixOpenError(error, isMacOS);

                }

                opened = new SafeFileHandle(
                    new IntPtr(fileDescriptor),
                    ownsHandle: true);

                status = SecureFileOpenStatus.Success;

            }
            else
            {

                return SecureFileOpenStatus.Rejected;

            }

            if (status is not SecureFileOpenStatus.Success || opened is null)
            {

                opened?.Dispose();

                return status;

            }

            if (opened.IsInvalid
                || !TryGetHandleMetadataIgnoringTestSeam(
                    opened,
                    out FileHandleMetadata metadata))
            {

                opened.Dispose();

                return SecureFileOpenStatus.IoError;

            }

            if (metadata.Kind is not FileSystemObjectKind.RegularFile)
            {

                opened.Dispose();

                return SecureFileOpenStatus.Rejected;

            }

            handle = opened;

            opened = null;

            return SecureFileOpenStatus.Success;

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or ObjectDisposedException
                or NotSupportedException)
        {

            return SecureFileOpenStatus.IoError;

        }
        finally
        {

            opened?.Dispose();

            if (parentReferenceAdded)
            {

                parentDirectory.DangerousRelease();

            }

        }

    }

    private static SecureFileOpenStatus MapUnixOpenError(
        int error,
        bool isMacOS) =>
        error switch
        {
            UnixErrorNoEntry or UnixErrorNotDirectory =>
                SecureFileOpenStatus.NotFound,
            UnixErrorPermissionDenied
                or UnixErrorOperationNotPermitted =>
                SecureFileOpenStatus.AccessDenied,
            LinuxErrorTooManySymbolicLinks
                when !isMacOS =>
                SecureFileOpenStatus.Rejected,
            MacOsErrorTooManySymbolicLinks
                when isMacOS =>
                SecureFileOpenStatus.Rejected,
            _ => SecureFileOpenStatus.IoError,
        };

    private static unsafe SecureFileOpenStatus TryOpenWindowsReadOnlyRelative(
        SafeFileHandle parentDirectory,
        string leaf,
        out SafeFileHandle? handle)
    {

        handle = null;

        fixed (char* leafBuffer = leaf)
        {

            UNICODE_STRING objectName = new()
            {
                Length = checked((ushort)(leaf.Length * sizeof(char))),
                MaximumLength = checked((ushort)(leaf.Length * sizeof(char))),
                Buffer = leafBuffer,
            };

            OBJECT_ATTRIBUTES objectAttributes = new()
            {
                Length = (uint)sizeof(OBJECT_ATTRIBUTES),
                RootDirectory = parentDirectory.DangerousGetHandle(),
                ObjectName = &objectName,
                Attributes = ObjectAttributeCaseInsensitive,
            };

            IO_STATUS_BLOCK statusBlock = default;

            IntPtr opened = IntPtr.Zero;

            int status = NtOpenFile(
                out opened,
                FileReadData | FileReadAttributes | Synchronize,
                &objectAttributes,
                &statusBlock,
                (uint)(FileShare.Read | FileShare.Write | FileShare.Delete),
                FileNonDirectoryFile | FileOpenReparsePoint);

            if (status < 0 || opened == IntPtr.Zero || opened == new IntPtr(-1))
            {

                if (opened != IntPtr.Zero && opened != new IntPtr(-1))
                {

                    new SafeFileHandle(opened, ownsHandle: true).Dispose();

                }

                uint error = RtlNtStatusToDosError(status);

                return error switch
                {
                    ErrorFileNotFound or ErrorPathNotFound =>
                        SecureFileOpenStatus.NotFound,
                    ErrorAccessDenied =>
                        SecureFileOpenStatus.AccessDenied,
                    ErrorStoppedOnSymlink
                        or ErrorCantAccessFile
                        or ErrorDirectory =>
                        SecureFileOpenStatus.Rejected,
                    _ => SecureFileOpenStatus.IoError,
                };

            }

            handle = new SafeFileHandle(opened, ownsHandle: true);

            return SecureFileOpenStatus.Success;

        }

    }

    private static bool TryGetWindowsPathMetadata(
        string path,
        out FileHandleMetadata metadata)
    {

        metadata = default;

        try
        {

            using SafeFileHandle handle = CreateFile(
                path,
                FileReadAttributes,
                FileShare.Read | FileShare.Write | FileShare.Delete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics,
                IntPtr.Zero);

            return !handle.IsInvalid
                && TryGetWindowsHandleMetadata(handle, out metadata);

        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
        {

            return false;

        }

    }

    private static bool TryGetWindowsPathMetadataNoFollow(
        string path,
        out FileHandleMetadata metadata)
    {
        metadata = default;

        try
        {
            using SafeFileHandle handle = CreateFile(
                path,
                FileReadAttributes,
                FileShare.Read | FileShare.Write | FileShare.Delete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                IntPtr.Zero);

            return !handle.IsInvalid
                && TryGetWindowsHandleMetadata(
                    handle,
                    out metadata);
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryGetWindowsHandleMetadata(
        SafeFileHandle handle,
        out FileHandleMetadata metadata)
    {

        metadata = default;

        if (!GetFileInformationByHandle(handle, out BY_HANDLE_FILE_INFORMATION info))
        {

            return false;

        }

        ulong fileId = ((ulong)info.nFileIndexHigh << 32) | info.nFileIndexLow;

        metadata = new FileHandleMetadata(
            new FileHandleIdentity(info.dwVolumeSerialNumber, fileId),
            info.nNumberOfLinks,
            ClassifyWindowsAttributes(info.dwFileAttributes));

        return true;

    }

    private static bool TryGetUnixPathMetadata(
        string path,
        out FileHandleMetadata metadata)
    {

        metadata = default;

        unsafe
        {

            Span<byte> buffer = stackalloc byte[StatBufferSize];

            fixed (byte* bufferPtr = buffer)
            {

                if (stat(path, bufferPtr) != 0)
                {

                    return false;

                }

                return TryReadUnixFileMetadata(
                    buffer,
                    OperatingSystem.IsMacOS(),
                    RuntimeInformation.ProcessArchitecture,
                    out metadata);

            }

        }

    }

    private static bool TryGetUnixPathMetadataNoFollow(
        string path,
        out FileHandleMetadata metadata)
    {
        metadata = default;

        unsafe
        {
            Span<byte> buffer = stackalloc byte[StatBufferSize];

            fixed (byte* bufferPtr = buffer)
            {
                if (lstat(path, bufferPtr) != 0)
                {
                    return false;
                }

                return TryReadUnixFileMetadata(
                    buffer,
                    OperatingSystem.IsMacOS(),
                    RuntimeInformation.ProcessArchitecture,
                    out metadata);
            }
        }
    }

    private static bool TryGetUnixHandleMetadata(
        SafeFileHandle handle,
        out FileHandleMetadata metadata)
    {

        metadata = default;

        int fd = handle.DangerousGetHandle().ToInt32();

        if (fd < 0)
        {

            return false;

        }

        unsafe
        {

            Span<byte> buffer = stackalloc byte[StatBufferSize];

            fixed (byte* bufferPtr = buffer)
            {

                if (fstat(fd, bufferPtr) != 0)
                {

                    return false;

                }

                return TryReadUnixFileMetadata(
                    buffer,
                    OperatingSystem.IsMacOS(),
                    RuntimeInformation.ProcessArchitecture,
                    out metadata);

            }

        }

    }

    private static bool TryReadUnixFileMetadata(
        ReadOnlySpan<byte> buffer,
        bool isMacOS,
        Architecture architecture,
        out FileHandleMetadata metadata)
    {

        metadata = default;

        if (!BitConverter.IsLittleEndian)
        {

            return false;

        }

        if (isMacOS)
        {

            if (architecture is not (Architecture.X64 or Architecture.Arm64)
                || buffer.Length < MacOsStatMinimumSize)
            {

                return false;

            }

            metadata = new FileHandleMetadata(
                new FileHandleIdentity(
                    BinaryPrimitives.ReadUInt32LittleEndian(buffer),
                    BinaryPrimitives.ReadUInt64LittleEndian(buffer[8..])),
                BinaryPrimitives.ReadUInt16LittleEndian(buffer[6..]),
                ClassifyUnixMode(
                    BinaryPrimitives.ReadUInt16LittleEndian(buffer[4..])));

            return true;

        }

        switch (architecture)
        {
            case Architecture.X64 when buffer.Length >= LinuxX64StatMinimumSize:
                metadata = new FileHandleMetadata(
                    new FileHandleIdentity(
                        BinaryPrimitives.ReadUInt64LittleEndian(buffer),
                        BinaryPrimitives.ReadUInt64LittleEndian(buffer[8..])),
                    BinaryPrimitives.ReadUInt64LittleEndian(buffer[16..]),
                    ClassifyUnixMode(
                        BinaryPrimitives.ReadUInt32LittleEndian(buffer[24..])));
                return true;
            case Architecture.Arm64 when buffer.Length >= LinuxArm64StatMinimumSize:
                metadata = new FileHandleMetadata(
                    new FileHandleIdentity(
                        BinaryPrimitives.ReadUInt64LittleEndian(buffer),
                        BinaryPrimitives.ReadUInt64LittleEndian(buffer[8..])),
                    BinaryPrimitives.ReadUInt32LittleEndian(buffer[20..]),
                    ClassifyUnixMode(
                        BinaryPrimitives.ReadUInt32LittleEndian(buffer[16..])));
                return true;
            default:
                return false;
        }

    }

    private static FileSystemObjectKind ClassifyWindowsAttributes(
        uint attributes)
    {

        const uint directory = 0x10;

        const uint device = 0x40;

        const uint reparsePoint = 0x400;

        if ((attributes & reparsePoint) != 0)
        {
            return FileSystemObjectKind.Other;
        }

        if ((attributes & directory) != 0)
        {

            return FileSystemObjectKind.Directory;

        }

        return (attributes & device) != 0
            ? FileSystemObjectKind.Other
            : FileSystemObjectKind.RegularFile;

    }

    private static FileSystemObjectKind ClassifyUnixMode(uint mode) =>
        (mode & 0xF000U) switch
        {
            0x8000U => FileSystemObjectKind.RegularFile,
            0x4000U => FileSystemObjectKind.Directory,
            _ => FileSystemObjectKind.Other,
        };

    private const int StatBufferSize = 256;

    private const int MacOsStatMinimumSize = 16;

    private const int MacOsAccessMetadataMinimumSize = 20;

    private const int LinuxX64StatMinimumSize = 28;

    private const int LinuxX64AccessMetadataMinimumSize = 32;

    private const int LinuxArm64StatMinimumSize = 24;

    private const int LinuxArm64AccessMetadataMinimumSize = 28;

    private const uint UnixPermissionBits = 0x0FFF;

    private const uint FileReadAttributes = 0x0080;

    private const uint FileReadData = 0x0001;

    private const uint FileTraverse = 0x0020;

    private const uint ReadControl = 0x00020000;

    private const uint GenericRead = 0x80000000;

    private const uint Synchronize = 0x00100000;

    private const uint OpenExisting = 3;

    private const uint FileFlagBackupSemantics = 0x02000000;

    private const uint FileFlagOpenReparsePoint = 0x00200000;

    private const uint FileFlagSequentialScan = 0x08000000;

    private const uint FileFlagOverlapped = 0x40000000;

    private const uint FileDirectoryFile = 0x00000001;

    private const uint FileNonDirectoryFile = 0x00000040;

    private const uint FileOpenReparsePoint = 0x00200000;

    private const uint ObjectAttributeCaseInsensitive = 0x00000040;

    private const int ErrorFileNotFound = 2;

    private const int ErrorPathNotFound = 3;

    private const int ErrorAccessDenied = 5;

    private const uint ErrorDirectory = 267;

    private const uint ErrorStoppedOnSymlink = 681;

    private const uint ErrorCantAccessFile = 1920;

    private const int LinuxOpenDirectory = 0x00010000;

    private const int LinuxOpenNonBlocking = 0x00000800;

    private const int LinuxOpenCloseOnExec = 0x00080000;

    private const int LinuxOpenNoFollow = 0x00020000;

    private const int MacOsOpenDirectory = 0x00100000;

    private const int MacOsOpenNonBlocking = 0x00000004;

    private const int MacOsOpenCloseOnExec = 0x01000000;

    private const int MacOsOpenNoFollow = 0x00000100;

    private const int UnixErrorOperationNotPermitted = 1;

    private const int UnixErrorNoEntry = 2;

    private const int UnixErrorPermissionDenied = 13;

    private const int UnixErrorNotDirectory = 20;

    private const int LinuxErrorTooManySymbolicLinks = 40;

    private const int MacOsErrorTooManySymbolicLinks = 62;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {

        public uint dwLowDateTime;

        public uint dwHighDateTime;

    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {

        public uint dwFileAttributes;

        public NativeFileTime ftCreationTime;

        public NativeFileTime ftLastAccessTime;

        public NativeFileTime ftLastWriteTime;

        public uint dwVolumeSerialNumber;

        public uint nFileSizeHigh;

        public uint nFileSizeLow;

        public uint nNumberOfLinks;

        public uint nFileIndexHigh;

        public uint nFileIndexLow;

    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct UNICODE_STRING
    {

        public ushort Length;

        public ushort MaximumLength;

        public char* Buffer;

    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct OBJECT_ATTRIBUTES
    {

        public uint Length;

        public IntPtr RootDirectory;

        public UNICODE_STRING* ObjectName;

        public uint Attributes;

        public IntPtr SecurityDescriptor;

        public IntPtr SecurityQualityOfService;

    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_STATUS_BLOCK
    {

        public IntPtr Status;

        public nuint Information;

    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(
        SafeFileHandle hFile,
        out BY_HANDLE_FILE_INFORMATION lpFileInformation);

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        FileShare dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [LibraryImport("ntdll.dll")]
    private static unsafe partial int NtOpenFile(
        out IntPtr fileHandle,
        uint desiredAccess,
        OBJECT_ATTRIBUTES* objectAttributes,
        IO_STATUS_BLOCK* ioStatusBlock,
        uint shareAccess,
        uint openOptions);

    [LibraryImport("ntdll.dll")]
    private static partial uint RtlNtStatusToDosError(int status);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int stat(string path, byte* buf);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int lstat(string path, byte* buf);

    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial int fstat(int fd, byte* buf);

    [LibraryImport(
        "libc",
        EntryPoint = "open",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenUnix(
        string path,
        int flags,
        uint mode);

    [LibraryImport(
        "libc",
        EntryPoint = "openat",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenAtUnix(
        int directoryFileDescriptor,
        string path,
        int flags,
        uint mode);

}
