using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;
using RetroDownfall.Arcanum.Tests.Support;

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
        "S-1-15-2-1111111111-2222222222-3333333333-1444444444-1555555555-1666666666-1777777777";

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
        _ = TestDirectoryCleanup.TryDelete(_root, nameof(WindowsAppContainerAclTests));
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
        InitializeModernInheritance(shared);
        string original = Sddl(shared);

        WindowsAppContainerLauncher.Grant(
            journalA,
            shared,
            new SecurityIdentifier(RunA),
            FileSystemRights.Modify | FileSystemRights.ReadAndExecute,
            WindowsAppContainerRootLockBudget.StartPerRun());
        WindowsAppContainerLauncher.Grant(
            journalB,
            shared,
            new SecurityIdentifier(RunB),
            FileSystemRights.Modify | FileSystemRights.ReadAndExecute,
            WindowsAppContainerRootLockBudget.StartPerRun());

        Assert.True(WindowsAppContainerLauncher.RemoveGrant(shared, RunA, WindowsAppContainerRootLockBudget.StartPerRun()));

        List<string> remaining = ExplicitSids(shared);

        Assert.DoesNotContain(RunA, remaining);
        Assert.Contains(RunB, remaining);

        Assert.True(WindowsAppContainerLauncher.RemoveGrant(shared, RunB, WindowsAppContainerRootLockBudget.StartPerRun()));

        Assert.Equal(original, Sddl(shared));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    [SupportedOSPlatform("windows")]
    public void Grants_preserve_protection_and_propagate_to_existing_children(bool protectedDacl)
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "The AppContainer ACL grant is Windows-only.");

        string shared = Directory.CreateDirectory(Path.Combine(_root, "workspace")).FullName;

        string child = Directory.CreateDirectory(Path.Combine(shared, "existing-child")).FullName;

        string file = Path.Combine(child, "existing-file.txt");

        File.WriteAllText(file, "existing content");

        InitializeModernInheritance(shared);

        DirectoryInfo directory = new(shared);

        DirectorySecurity security = directory.GetAccessControl(AccessControlSections.Access);

        security.SetAccessRuleProtection(protectedDacl, preserveInheritance: true);

        directory.SetAccessControl(security);

        string original = Sddl(shared);

        string childOriginal = Sddl(child);

        string fileOriginal = Sddl(file);

        ControlFlags originalFlags = Descriptor(shared).ControlFlags;

        Assert.True((originalFlags & ControlFlags.DiscretionaryAclAutoInherited) != 0);

        Assert.Equal(protectedDacl, (originalFlags & ControlFlags.DiscretionaryAclProtected) != 0);

        string journal = Path.Combine(_root, "children.journal");

        File.WriteAllBytes(journal, []);

        WindowsAppContainerLauncher.Grant(journal, shared, new SecurityIdentifier(RunA),
            FileSystemRights.Modify | FileSystemRights.ReadAndExecute, WindowsAppContainerRootLockBudget.StartPerRun());

        WindowsAppContainerLauncher.Grant(journal, shared, new SecurityIdentifier(RunB),
            FileSystemRights.ReadAndExecute, WindowsAppContainerRootLockBudget.StartPerRun());

        Assert.Equal(originalFlags, Descriptor(shared).ControlFlags);

        AssertInheritedSid(child, RunA);

        AssertInheritedSid(file, RunA);

        AssertInheritedSid(child, RunB);

        AssertInheritedSid(file, RunB);

        Assert.True(WindowsAppContainerLauncher.RemoveGrant(shared, RunA, WindowsAppContainerRootLockBudget.StartPerRun()));

        foreach (string path in new[] { shared, child, file })
        {
            Assert.DoesNotContain(RunA, AccessSids(path));

            Assert.Contains(RunB, AccessSids(path));
        }

        Assert.Equal(originalFlags, Descriptor(shared).ControlFlags);

        Assert.True(WindowsAppContainerLauncher.RemoveGrant(shared, RunB, WindowsAppContainerRootLockBudget.StartPerRun()));

        Assert.Equal(original, Sddl(shared));

        Assert.Equal(childOriginal, Sddl(child));

        Assert.Equal(fileOriginal, Sddl(file));
    }

    [SkippableFact]
    [SupportedOSPlatform("windows")]
    public void Fresh_root_round_trip_preserves_exact_aces_and_protection_while_windows_initializes_inheritance()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "The AppContainer ACL grant is Windows-only.");

        string shared = Directory.CreateDirectory(Path.Combine(_root, "fresh-workspace")).FullName;

        string journal = Path.Combine(_root, "fresh.journal");

        File.WriteAllBytes(journal, []);

        RawSecurityDescriptor original = Descriptor(shared);

        byte[] originalAcl = AclBytes(original);

        WindowsAppContainerLauncher.Grant(journal, shared, new SecurityIdentifier(RunA),
            FileSystemRights.ReadAndExecute, WindowsAppContainerRootLockBudget.StartPerRun());

        Assert.True(WindowsAppContainerLauncher.RemoveGrant(shared, RunA, WindowsAppContainerRootLockBudget.StartPerRun()));

        RawSecurityDescriptor restored = Descriptor(shared);

        Assert.Equal(originalAcl, AclBytes(restored));

        // Windows imposes its current inheritance model on the first SetSecurityInfo DACL write.
        // Every other control flag and every ACE remains exactly the captured value.
        Assert.Equal(original.ControlFlags | ControlFlags.DiscretionaryAclAutoInherited, restored.ControlFlags);
    }

    [SkippableFact]
    [SupportedOSPlatform("windows")]
    public void Replaying_an_absent_run_sid_twice_leaves_the_complete_dacl_unchanged()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "The AppContainer ACL removal is Windows-only.");

        string shared = Directory.CreateDirectory(Path.Combine(_root, "ungranted-workspace")).FullName;

        string original = Sddl(shared);

        Assert.DoesNotContain(RunA, ExplicitSids(shared));

        // The journal is written before the grant. A killed broker may therefore ask replay to remove
        // a SID that was never present; repeating that replay must remain a complete descriptor no-op.
        Assert.True(WindowsAppContainerLauncher.RemoveGrant(shared, RunA, WindowsAppContainerRootLockBudget.StartPerRun()));

        Assert.Equal(original, Sddl(shared));

        Assert.True(WindowsAppContainerLauncher.RemoveGrant(shared, RunA, WindowsAppContainerRootLockBudget.StartPerRun()));

        Assert.Equal(original, Sddl(shared));
    }

    [SkippableFact]
    [SupportedOSPlatform("windows")]
    public void Removing_a_sid_that_is_not_a_per_run_AppContainer_sid_leaves_the_dacl_alone()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "The AppContainer ACL removal is Windows-only.");

        string shared = Directory.CreateDirectory(Path.Combine(_root, "workspace")).FullName;
        string original = Sddl(shared);
        string currentUser = WindowsIdentity.GetCurrent().User!.Value;

        // The owner's own explicit ACE and the built-in Administrators group are exactly what a tampered
        // undo record could name; the purge must refuse both rather than strip them.
        Assert.False(WindowsAppContainerLauncher.RemoveGrant(shared, currentUser, WindowsAppContainerRootLockBudget.StartPerRun()));
        Assert.False(WindowsAppContainerLauncher.RemoveGrant(shared, "S-1-5-32-544", WindowsAppContainerRootLockBudget.StartPerRun()));
        Assert.False(WindowsAppContainerLauncher.RemoveGrant(shared, "S-1-15-2-1", WindowsAppContainerRootLockBudget.StartPerRun()));
        Assert.Equal(original, Sddl(shared));
    }

    [SkippableFact]
    [SupportedOSPlatform("windows")]
    public void Contended_root_locks_in_one_run_share_one_budget_instead_of_stacking_timeouts()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "The per-root ACL mutex is Windows-only.");

        string first = Directory.CreateDirectory(Path.Combine(_root, "first")).FullName;
        string second = Directory.CreateDirectory(Path.Combine(_root, "second")).FullName;
        string journal = Path.Combine(_root, "contended.journal");
        File.WriteAllBytes(journal, []);
        using ManualResetEventSlim held = new();
        using ManualResetEventSlim release = new();

        // Another run stuck mid-update holds both roots' locks for the whole test.
        Thread holder = new(() =>
        {
            using Mutex firstLock = new(initiallyOwned: false, WindowsAppContainerLauncher.RootLockName(first));
            using Mutex secondLock = new(initiallyOwned: false, WindowsAppContainerLauncher.RootLockName(second));
            firstLock.WaitOne();
            secondLock.WaitOne();
            held.Set();
            release.Wait();
            secondLock.ReleaseMutex();
            firstLock.ReleaseMutex();
        });
        holder.Start();
        Assert.True(held.Wait(TimeSpan.FromSeconds(10)));

        WindowsAppContainerRootLockBudget budget = new(TimeSpan.FromSeconds(2), static () => DateTime.UtcNow);
        System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // Two grants and a removal: with a timeout per wait this takes three budgets.
            Assert.Throws<TimeoutException>(() => WindowsAppContainerLauncher.Grant(
                journal, first, new SecurityIdentifier(RunA), FileSystemRights.ReadAndExecute, budget));
            Assert.Throws<TimeoutException>(() => WindowsAppContainerLauncher.Grant(
                journal, second, new SecurityIdentifier(RunA), FileSystemRights.ReadAndExecute, budget));
            Assert.Throws<TimeoutException>(() => WindowsAppContainerLauncher.RemoveGrant(first, RunA, budget));
        }
        finally
        {
            elapsed.Stop();
            release.Set();
            holder.Join();
        }

        Assert.True(
            elapsed.Elapsed < TimeSpan.FromSeconds(4),
            $"Three contended waits took {elapsed.Elapsed}; one shared 2 s budget should bound them.");
    }

    [SupportedOSPlatform("windows")]
    internal static void InitializeModernInheritance(string path)
    {
        DirectoryInfo directory = new(path);

        DirectorySecurity security = directory.GetAccessControl(AccessControlSections.Access);

        // Persisting the current protection choice initializes Windows' current inheritance model
        // without changing the ACEs or protection. A first SetSecurityInfo write legitimately adds
        // SE_DACL_AUTO_INHERITED; the round trip below compares the complete descriptor afterward.
        security.SetAccessRuleProtection(security.AreAccessRulesProtected, preserveInheritance: true);

        directory.SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static string Sddl(string path) =>
        Security(path).GetSecurityDescriptorSddlForm(AccessControlSections.Access);

    [SupportedOSPlatform("windows")]
    private static FileSystemSecurity Security(string path) =>
        Directory.Exists(path)
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Access);

    [SupportedOSPlatform("windows")]
    private static RawSecurityDescriptor Descriptor(string path) => new(Sddl(path));

    [SupportedOSPlatform("windows")]
    private static byte[] AclBytes(RawSecurityDescriptor descriptor)
    {
        RawAcl acl = descriptor.DiscretionaryAcl!;

        byte[] bytes = new byte[acl.BinaryLength];

        acl.GetBinaryForm(bytes, 0);

        return bytes;
    }

    [SupportedOSPlatform("windows")]
    private static void AssertInheritedSid(string path, string sid) =>
        Assert.Contains(Security(path)
            .GetAccessRules(includeExplicit: false, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>(), rule => rule.IdentityReference.Value == sid && rule.IsInherited);

    [SupportedOSPlatform("windows")]
    private static List<string> AccessSids(string path) =>
        [
            .. Security(path)
                .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .Select(static rule => rule.IdentityReference.Value),
        ];

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
