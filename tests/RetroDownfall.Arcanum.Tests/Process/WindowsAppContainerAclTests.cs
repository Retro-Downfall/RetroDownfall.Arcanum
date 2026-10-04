using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

namespace RetroDownfall.Arcanum.Tests.Process;

/// <summary>
/// Real-DACL round trips for the broker's grant and restore. Restoring a captured descriptor let two
/// runs sharing a root corrupt each other: run A restoring its snapshot deleted run B's live ACE, and
/// run B restoring its snapshot re-added run A's dead SID. Restore now removes only this run's SID
/// from a freshly read DACL. Windows-only; the journal half runs on every host in
/// <see cref="WindowsAppContainerRestoreJournalTests"/>.
/// </summary>
public sealed class WindowsAppContainerAclTests : IDisposable
{
    private const string RunA =
        "S-1-15-2-1111111111-2222222222-3333333333-4444444444-1555555555-1666666666-1777777777";

    private const string RunB =
        "S-1-15-2-1888888888-1999999999-1212121212-1343434343-1565656565-1787878787-1909090909";

    private readonly string _root;

    public WindowsAppContainerAclTests()
    {
        _root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "arcanum-acl-" + Guid.NewGuid().ToString("N"))).FullName;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
        }
    }

    [SkippableFact]
    [SupportedOSPlatform("windows")]
    public void Overlapping_grants_restore_without_removing_the_other_runs_ace()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "The AppContainer ACL grant is Windows-only.");

        string shared = Directory.CreateDirectory(Path.Combine(_root, "workspace")).FullName;
        string journalA = Path.Combine(_root, "a.journal");
        string journalB = Path.Combine(_root, "b.journal");
        File.WriteAllBytes(journalA, []);
        File.WriteAllBytes(journalB, []);
        string original = Sddl(shared);

        WindowsAppContainerLauncher.Grant(
            journalA,
            shared,
            new SecurityIdentifier(RunA),
            FileSystemRights.Modify | FileSystemRights.ReadAndExecute);
        WindowsAppContainerLauncher.Grant(
            journalB,
            shared,
            new SecurityIdentifier(RunB),
            FileSystemRights.Modify | FileSystemRights.ReadAndExecute);

        Assert.True(WindowsAppContainerLauncher.RemoveGrant(shared, RunA));

        List<string> remaining = ExplicitSids(shared);

        Assert.DoesNotContain(RunA, remaining);
        Assert.Contains(RunB, remaining);

        Assert.True(WindowsAppContainerLauncher.RemoveGrant(shared, RunB));

        Assert.Equal(original, Sddl(shared));
    }

    [SupportedOSPlatform("windows")]
    private static string Sddl(string path) =>
        new DirectoryInfo(path)
            .GetAccessControl(AccessControlSections.Access)
            .GetSecurityDescriptorSddlForm(AccessControlSections.Access);

    [SupportedOSPlatform("windows")]
    private static List<string> ExplicitSids(string path) =>
        [
            .. new DirectoryInfo(path)
                .GetAccessControl(AccessControlSections.Access)
                .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .Select(static rule => rule.IdentityReference.Value),
        ];
}
