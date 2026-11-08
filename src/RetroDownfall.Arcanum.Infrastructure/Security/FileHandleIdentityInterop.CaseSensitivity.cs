using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

internal static partial class FileHandleIdentityInterop
{
    /// <summary>Queries the opened directory itself; unknown filesystem policy is never guessed.</summary>
    internal static bool TryGetDirectoryCaseSensitivity(SafeFileHandle directory, out bool caseSensitive)
    {
        caseSensitive = false;

        bool retained = false;

        try
        {
            directory.DangerousAddRef(ref retained);

            if (OperatingSystem.IsMacOS())
            {
                // Darwin sys/unistd.h: _PC_CASE_SENSITIVE = 11.
                long value = DirectoryPathConf(directory.DangerousGetHandle().ToInt32(), 11);

                caseSensitive = value == 1;

                return value is 0 or 1;
            }

            if (OperatingSystem.IsWindows()
                && GetDirectoryCaseSensitiveInfo(directory, 23, out uint flags, sizeof(uint))
                && (flags & ~1U) == 0)
            {
                caseSensitive = (flags & 1) != 0;

                return true;
            }

            return false;
        }
        catch (Exception exception) when (IsKernelPathQueryFailure(exception))
        {
            return false;
        }
        finally
        {
            if (retained)
            {
                directory.DangerousRelease();
            }
        }
    }

    [LibraryImport("libc", EntryPoint = "fpathconf", SetLastError = true)]
    private static partial long DirectoryPathConf(int descriptor, int name);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDirectoryCaseSensitiveInfo(SafeFileHandle handle, int informationClass, out uint flags, uint size);
}
