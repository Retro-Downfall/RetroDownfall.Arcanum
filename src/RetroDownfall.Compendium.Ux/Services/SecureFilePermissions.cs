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

    /// <summary>
    /// Creates and restricts <paramref name="path"/>, reporting what it could not do instead of
    /// throwing. Callers that cannot survive an exception — anything running during construction or
    /// after a durable write has already committed — must use this rather than
    /// <see cref="EnsureOwnerOnlyDirectoryExists"/>, and report <paramref name="error"/> on a surface
    /// the operator can actually read.
    /// </summary>
    public static bool TryEnsureOwnerOnlyDirectoryExists(string path, out string? error)
    {

        error = null;

        try
        {

            _ = Directory.CreateDirectory(path);

            ApplyOwnerOnlyDirectory(path);

            return true;

        }
        catch (Exception exception) when (IsPermissionFault(exception))
        {

            error = $"Could not restrict {path} to the current user: {exception.Message}";

            return false;

        }

    }

    /// <summary>
    /// The faults a permission attempt can raise on a path the process does not own, on a filesystem
    /// with no mode or ACL support, or on a Windows session with no resolvable user. Anything else is
    /// a defect in this code and keeps propagating.
    /// </summary>
    public static bool IsPermissionFault(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or PlatformNotSupportedException
            or InvalidOperationException;

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

        SecurityIdentifier owner = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Could not resolve current Windows user.");

        // Create a new DACL with only the owner's access rule
        DirectorySecurity security = new();

        // Set protection to prevent inheritance and remove inherited rules
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        // Add owner's full control rule with inheritance for subdirectories and files
        security.AddAccessRule(new FileSystemAccessRule(
            owner,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        // Apply the new ACL in a single operation
        directory.SetAccessControl(security);

    }

    [SupportedOSPlatform("windows")]
    private static void ApplyOwnerOnlyFileAcl(string path)
    {

        FileInfo file = new(path);

        SecurityIdentifier owner = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Could not resolve current Windows user.");

        // Create a new DACL with only the owner's access rule
        FileSecurity security = new();

        // Set protection to prevent inheritance and remove inherited rules
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        // Add owner's read/write rule
        security.AddAccessRule(new FileSystemAccessRule(
            owner,
            FileSystemRights.Read | FileSystemRights.Write,
            AccessControlType.Allow));

        // Apply the new ACL in a single operation
        file.SetAccessControl(security);

    }

}