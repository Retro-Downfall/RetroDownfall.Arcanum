using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

using Microsoft.Win32.SafeHandles;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

/// <summary>
/// Asks the kernel where an open handle lives, independent of the path string it was opened with.
/// </summary>
internal static partial class FileHandleIdentityInterop
{
    /// <summary>
    /// <c>PROC_PIDFDVNODEPATHINFO</c> from <c>sys/proc_info.h</c>.
    /// </summary>
    private const int MacOsProcPidFdVnodePathInfo = 2;

    /// <summary>
    /// <c>sizeof(struct vnode_fdinfowithpath)</c>, which <c>proc_pidfdinfo</c> returns on success.
    /// </summary>
    private const int MacOsVnodeFdInfoWithPathSize = 1200;

    /// <summary>
    /// <c>offsetof(struct vnode_fdinfowithpath, pvip.vip_path)</c>.
    /// </summary>
    private const int MacOsVnodeFdInfoPathOffset = 176;

    /// <summary>
    /// <c>MAXPATHLEN</c>, the size of <c>vip_path</c>.
    /// </summary>
    private const int MacOsMaxPathLength = 1024;

    private const uint WindowsFileNameNormalized = 0;

    private const string WindowsExtendedPathPrefix = @"\\?\";

    private const string WindowsExtendedUncPrefix = @"\\?\UNC\";

    /// <summary>
    /// Resolves the absolute path the kernel holds for <paramref name="handle"/>: <c>proc_pidfdinfo</c>
    /// (<c>PROC_PIDFDVNODEPATHINFO</c>, the same answer as <c>F_GETPATH</c>) on macOS,
    /// <c>/proc/self/fd</c> on Linux and <c>GetFinalPathNameByHandleW</c> on Windows. The answer has every
    /// symbolic link resolved, so it is the canonical location of the open object rather than the
    /// spelling it was opened through. Returns <see langword="false"/> when the kernel cannot name it
    /// (for example after the object was unlinked).
    /// </summary>
    internal static bool TryGetHandleKernelPath(
        SafeFileHandle handle,
        [NotNullWhen(true)] out string? path)
    {
        path = null;

        if (handle is null || handle.IsInvalid || handle.IsClosed)
        {
            return false;
        }

        bool referenceAdded = false;

        try
        {
            handle.DangerousAddRef(ref referenceAdded);

            if (OperatingSystem.IsMacOS())
            {
                return TryGetMacOsHandleKernelPath(handle, out path);
            }

            if (OperatingSystem.IsLinux())
            {
                return TryGetLinuxHandleKernelPath(handle, out path);
            }

            if (OperatingSystem.IsWindows())
            {
                return TryGetWindowsHandleKernelPath(handle, out path);
            }

            return false;
        }
        catch (Exception exception) when (
            exception is ObjectDisposedException
                or IOException
                or UnauthorizedAccessException
                or ArgumentException)
        {
            path = null;

            return false;
        }
        finally
        {
            if (referenceAdded)
            {
                handle.DangerousRelease();
            }
        }
    }

    private static unsafe bool TryGetMacOsHandleKernelPath(
        SafeFileHandle handle,
        [NotNullWhen(true)] out string? path)
    {
        path = null;

        int descriptor = handle.DangerousGetHandle().ToInt32();

        if (descriptor < 0)
        {
            return false;
        }

        byte[] buffer = new byte[MacOsVnodeFdInfoWithPathSize];

        int written;

        fixed (byte* bufferPointer = buffer)
        {
            written = ProcPidFdInfo(
                Environment.ProcessId,
                descriptor,
                MacOsProcPidFdVnodePathInfo,
                bufferPointer,
                buffer.Length);
        }

        if (written != MacOsVnodeFdInfoWithPathSize)
        {
            return false;
        }

        ReadOnlySpan<byte> pathBytes = buffer.AsSpan(
            MacOsVnodeFdInfoPathOffset,
            MacOsMaxPathLength);

        int terminator = pathBytes.IndexOf((byte)0);

        if (terminator <= 0)
        {
            return false;
        }

        string candidate = Encoding.UTF8.GetString(pathBytes[..terminator]);

        if (!Path.IsPathFullyQualified(candidate))
        {
            return false;
        }

        path = candidate;

        return true;
    }

    private static bool TryGetLinuxHandleKernelPath(
        SafeFileHandle handle,
        [NotNullWhen(true)] out string? path)
    {
        path = null;

        int descriptor = handle.DangerousGetHandle().ToInt32();

        if (descriptor < 0)
        {
            return false;
        }

        string? target = new FileInfo(
                "/proc/self/fd/" + descriptor.ToString(CultureInfo.InvariantCulture))
            .LinkTarget;

        // An unlinked object reads back as "<old path> (deleted)", and anonymous objects as
        // "pipe:[...]"-style names; neither names a location that can be contained.
        if (string.IsNullOrEmpty(target)
            || !Path.IsPathFullyQualified(target)
            || target.EndsWith(" (deleted)", StringComparison.Ordinal))
        {
            return false;
        }

        path = target;

        return true;
    }

    private static unsafe bool TryGetWindowsHandleKernelPath(
        SafeFileHandle handle,
        [NotNullWhen(true)] out string? path)
    {
        path = null;

        char[] buffer = new char[512];

        while (true)
        {
            uint length;

            fixed (char* bufferPointer = buffer)
            {
                length = GetFinalPathNameByHandle(
                    handle,
                    bufferPointer,
                    (uint)buffer.Length,
                    WindowsFileNameNormalized);
            }

            if (length == 0)
            {
                return false;
            }

            if (length < buffer.Length)
            {
                path = NormalizeWindowsFinalPath(new string(buffer, 0, (int)length));

                return Path.IsPathFullyQualified(path);
            }

            if (length > short.MaxValue)
            {
                return false;
            }

            buffer = new char[length + 1];
        }
    }

    /// <summary>
    /// Strips the extended-length prefix <c>GetFinalPathNameByHandleW</c> always returns, so the result
    /// compares against an ordinary <see cref="Path.GetFullPath(string)"/> spelling.
    /// </summary>
    internal static string NormalizeWindowsFinalPath(string finalPath)
    {
        if (finalPath.StartsWith(WindowsExtendedUncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + finalPath[WindowsExtendedUncPrefix.Length..];
        }

        if (finalPath.StartsWith(WindowsExtendedPathPrefix, StringComparison.Ordinal))
        {
            return finalPath[WindowsExtendedPathPrefix.Length..];
        }

        return finalPath;
    }

    [LibraryImport("libc", EntryPoint = "proc_pidfdinfo", SetLastError = true)]
    private static unsafe partial int ProcPidFdInfo(
        int processId,
        int descriptor,
        int flavor,
        byte* buffer,
        int bufferSize);

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "GetFinalPathNameByHandleW",
        SetLastError = true)]
    private static unsafe partial uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        char* filePath,
        uint filePathLength,
        uint flags);
}
