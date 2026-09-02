using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using RetroDownfall.TheForge.Core.IO;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

/// <summary>
/// Windows-only: ACL semantics (owner-only DACL, protected from inheritance) have no Unix
/// equivalent to assert here, so every test in this class is a runtime no-op off Windows —
/// [SupportedOSPlatform("windows")] quiets CA1416 for the ACL-reading calls the same way
/// TheForgeOwnerOnlyPermissions itself quiets it for the ACL-writing calls, and the runtime
/// OperatingSystem.IsWindows() guard is what actually skips the body elsewhere.
/// </summary>
public sealed class TheForgeOwnerOnlyPermissionsTests
{

    [Fact]
    [SupportedOSPlatform("windows")]
    public void TrySetFile_OnWindows_RestrictsTheDaclToTheCurrentUserOnly()
    {

        if (!OperatingSystem.IsWindows())
        {

            return;

        }

        string path = Path.Combine(Path.GetTempPath(), $"forge-acl-file-{Guid.NewGuid():N}.tmp");

        File.WriteAllText(path, "content");

        try
        {

            TheForgeOwnerOnlyPermissions.TrySetFile(path);

            FileSecurity security = new FileInfo(path).GetAccessControl();

            AssertOwnerOnlyProtectedDacl(security, security.GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                targetType: typeof(SecurityIdentifier)));

        }
        finally
        {

            File.Delete(path);

        }

    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void TrySetDirectory_OnWindows_RestrictsTheDaclToTheCurrentUserOnly()
    {

        if (!OperatingSystem.IsWindows())
        {

            return;

        }

        string path = Path.Combine(Path.GetTempPath(), $"forge-acl-dir-{Guid.NewGuid():N}");

        Directory.CreateDirectory(path);

        try
        {

            TheForgeOwnerOnlyPermissions.TrySetDirectory(path);

            DirectorySecurity security = new DirectoryInfo(path).GetAccessControl();

            AssertOwnerOnlyProtectedDacl(security, security.GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                targetType: typeof(SecurityIdentifier)));

        }
        finally
        {

            Directory.Delete(path);

        }

    }

    [SupportedOSPlatform("windows")]
    private static void AssertOwnerOnlyProtectedDacl(
        FileSystemSecurity security,
        AuthorizationRuleCollection rules)
    {

        Assert.True(security.AreAccessRulesProtected, "The DACL must not inherit rules from the parent.");

        SecurityIdentifier currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Could not resolve current Windows user.");

        Assert.NotEmpty(rules);

        foreach (FileSystemAccessRule rule in rules)
        {

            Assert.Equal(currentUser, rule.IdentityReference);

            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);

        }

    }

}
