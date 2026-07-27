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

    private const int LinuxX64StatMinimumSize = 28;

    private const int LinuxArm64StatMinimumSize = 24;

    private const uint FileReadAttributes = 0x0080;

    private const uint OpenExisting = 3;

    private const uint FileFlagBackupSemantics = 0x02000000;

    private const uint FileFlagOpenReparsePoint = 0x00200000;

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

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int stat(string path, byte* buf);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int lstat(string path, byte* buf);

    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial int fstat(int fd, byte* buf);

}
