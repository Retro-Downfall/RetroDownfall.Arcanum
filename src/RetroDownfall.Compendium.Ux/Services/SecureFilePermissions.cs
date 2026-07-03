using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace RetroDownfall.Compendium.Ux.Services;

internal static class SecureFilePermissions
{

    public static void EnsureOwnerOnlyDirectoryExists(string path)
    {

        _ = Directory.CreateDirectory(path);

        ApplyOwnerOnlyDirectory(path);

    }

    public static void ApplyOwnerOnlyDirectory(string path)
    {

        if (OperatingSystem.IsWindows())
        {

            ApplyOwnerOnlyDirectoryAcl(path);

        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())

        {

            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        }

    }

    public static void ApplyOwnerOnlyFile(string path)
    {

        if (OperatingSystem.IsWindows())
        {

            ApplyOwnerOnlyFileAcl(path);

        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())

        {

            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        }

    }

    [SupportedOSPlatform("windows")]
    private static void ApplyOwnerOnlyDirectoryAcl(string path)
    {

        DirectoryInfo directory = new(path);

        DirectorySecurity security = new();

        SecurityIdentifier owner = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Could not resolve current Windows user.");

        security.SetOwner(owner);

        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        security.AddAccessRule(new FileSystemAccessRule(
            owner,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        directory.SetAccessControl(security);

    }

    [SupportedOSPlatform("windows")]
    private static void ApplyOwnerOnlyFileAcl(string path)
    {

        FileInfo file = new(path);

        FileSecurity security = new();

        SecurityIdentifier owner = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Could not resolve current Windows user.");

        security.SetOwner(owner);

        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        security.AddAccessRule(new FileSystemAccessRule(
            owner,
            FileSystemRights.Read | FileSystemRights.Write,
            AccessControlType.Allow));

        file.SetAccessControl(security);

    }

}
