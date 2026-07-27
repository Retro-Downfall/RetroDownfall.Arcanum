using System.Runtime.InteropServices;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static class HardLinkTestSupport
{

    internal static bool TryCreate(string linkPath, string existingPath)
    {

        if (OperatingSystem.IsWindows())
        {

            return CreateHardLink(linkPath, existingPath, IntPtr.Zero);

        }

        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
        {

            return link(existingPath, linkPath) == 0;

        }

        return false;

    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateHardLinkW",
        SetLastError = true,
        CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(
        string lpFileName,
        string lpExistingFileName,
        IntPtr lpSecurityAttributes);

    [DllImport("libc", SetLastError = true)]
    private static extern int link(string existingPath, string newPath);

}
