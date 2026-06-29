using System.Runtime.InteropServices;

using Microsoft.Win32.SafeHandles;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

/// <summary>
/// Stable volume + file identity for post-open sandbox revalidation (dev/ino on Unix, volume serial + file index on Windows).
/// </summary>
internal readonly record struct FileHandleIdentity(ulong VolumeId, ulong FileId)
{

    internal static bool IdentitiesMatch(FileHandleIdentity expected, FileHandleIdentity actual) =>
        expected.VolumeId == actual.VolumeId && expected.FileId == actual.FileId;

}

/// <summary>
/// Platform interop for resolving file identities from paths and open handles.
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

        if (OperatingSystem.IsWindows())
        {

            return TryGetWindowsPathIdentity(path, out identity);

        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {

            return TryGetUnixPathIdentity(path, out identity);

        }

        return false;

    }

    internal static bool TryGetHandleIdentity(SafeFileHandle handle, out FileHandleIdentity identity)
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

        if (OperatingSystem.IsWindows())
        {

            return TryGetWindowsHandleIdentity(handle, out identity);

        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {

            return TryGetUnixHandleIdentity(handle, out identity);

        }

        return false;

    }

    private static bool TryGetWindowsPathIdentity(string path, out FileHandleIdentity identity)
    {

        identity = default;

        try
        {

            using SafeFileHandle handle = File.OpenHandle(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                FileOptions.None);

            return TryGetWindowsHandleIdentity(handle, out identity);

        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {

            return false;

        }

    }

    private static bool TryGetWindowsHandleIdentity(SafeFileHandle handle, out FileHandleIdentity identity)
    {

        identity = default;

        if (!GetFileInformationByHandle(handle, out BY_HANDLE_FILE_INFORMATION info))
        {

            return false;

        }

        ulong fileId = ((ulong)info.nFileIndexHigh << 32) | info.nFileIndexLow;

        identity = new FileHandleIdentity(info.dwVolumeSerialNumber, fileId);

        return true;

    }

    private static bool TryGetUnixPathIdentity(string path, out FileHandleIdentity identity)
    {

        identity = default;

        unsafe
        {

            Span<byte> buffer = stackalloc byte[StatBufferSize];

            fixed (byte* bufferPtr = buffer)
            {

                if (stat(path, bufferPtr) != 0)
                {

                    return false;

                }

                ReadUnixFileIdentity(bufferPtr, out identity);

            }

            return true;

        }

    }

    private static bool TryGetUnixHandleIdentity(SafeFileHandle handle, out FileHandleIdentity identity)
    {

        identity = default;

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

                ReadUnixFileIdentity(bufferPtr, out identity);

            }

            return true;

        }

    }

    private static unsafe void ReadUnixFileIdentity(byte* bufferPtr, out FileHandleIdentity identity)
    {

        if (OperatingSystem.IsMacOS())
        {

            MacOsStat* stat = (MacOsStat*)bufferPtr;

            identity = new FileHandleIdentity(stat->st_dev, stat->st_ino);

            return;

        }

        LinuxStat64* stat64 = (LinuxStat64*)bufferPtr;

        identity = new FileHandleIdentity(stat64->st_dev, stat64->st_ino);

    }

    private const int StatBufferSize = 256;

    /// <summary>
    /// macOS 64-bit struct stat layout. Offsets verified against Darwin headers:
    /// st_dev (4 bytes) at 0, st_mode (2) + st_nlink (2) at 4-8, st_ino (8 bytes) at 8.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct MacOsStat
    {

        [FieldOffset(0)] public uint st_dev;

        [FieldOffset(8)] public ulong st_ino;

    }

    /// <summary>
    /// glibc struct stat64 layout. Offsets verified against glibc headers:
    /// st_dev (8 bytes) at 0, st_ino (8 bytes) at 8.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct LinuxStat64
    {

        [FieldOffset(0)] public ulong st_dev;

        [FieldOffset(8)] public ulong st_ino;

    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {

        public uint dwFileAttributes;

        public long ftCreationTime;

        public long ftLastAccessTime;

        public long ftLastWriteTime;

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

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int stat(string path, byte* buf);

    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial int fstat(int fd, byte* buf);

}
