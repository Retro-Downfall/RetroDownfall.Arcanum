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
