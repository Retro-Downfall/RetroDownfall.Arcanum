using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace RetroDownfall.TheForge.Core.IO;

/// <summary>
/// Owner-only file/directory permission helpers for The Forge-local persistence.
/// Does not depend on <c>RetroDownfall.Arcanum.Infrastructure</c>.
/// </summary>
public static class TheForgeOwnerOnlyPermissions
{

    public static void TrySetFile(string path)
    {

        if (OperatingSystem.IsWindows())
        {

            TryApplyFileAcl(path);

            return;

        }

        try
        {

            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

    }

    public static void TrySetDirectory(string path)
    {

        if (OperatingSystem.IsWindows())
        {

            TryApplyDirectoryAcl(path);

            return;

        }

        try
        {

            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

    }

    /// <summary>
    /// Mirrors <c>RetroDownfall.Compendium.Ux.Services.SecureFilePermissions.ApplyOwnerOnlyFileAcl</c>,
    /// adapted to this class's best-effort (never-throw) contract: that sibling throws when the
    /// current user cannot be resolved or the ACL write fails, matching its callers, which can
    /// surface an error; <see cref="TrySetFile"/>'s Unix branch has never thrown here, so this stays
    /// consistent and swallows the same fault shapes instead.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void TryApplyFileAcl(string path)
    {

        try
        {

            SecurityIdentifier? owner = WindowsIdentity.GetCurrent().User;

            if (owner is null)
            {

                return;

            }

            FileSecurity security = new();

            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            security.AddAccessRule(new FileSystemAccessRule(
                owner,
                FileSystemRights.Read | FileSystemRights.Write,
                AccessControlType.Allow));

            new FileInfo(path).SetAccessControl(security);

        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
        }

    }

    /// <summary>Directory counterpart of <see cref="TryApplyFileAcl"/>; see its remarks.</summary>
    [SupportedOSPlatform("windows")]
    private static void TryApplyDirectoryAcl(string path)
    {

        try
        {

            SecurityIdentifier? owner = WindowsIdentity.GetCurrent().User;

            if (owner is null)
            {

                return;

            }

            DirectorySecurity security = new();

            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            security.AddAccessRule(new FileSystemAccessRule(
                owner,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));

            new DirectoryInfo(path).SetAccessControl(security);

        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
        }

    }

}
