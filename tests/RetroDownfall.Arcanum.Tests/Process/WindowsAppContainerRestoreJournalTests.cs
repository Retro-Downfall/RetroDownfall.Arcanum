using System.Text.Json;
using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Process;

/// <summary>
/// The Windows broker grants a per-run AppContainer SID an inheritable Modify ACE on every declared
/// root — including the campaign workspace — and undoes it in a <c>finally</c>. The host kills the
/// broker with TerminateProcess on timeout, cancellation, or a Job Object kill, and managed finally
/// blocks do not run then: without a durable undo log the grant exists only in the dead broker's
/// memory and the workspace DACL keeps one orphaned ACE per killed run forever. The log records the
/// per-run SID, not a snapshot of the DACL: two runs sharing a root must each remove only their own
/// ACE, because restoring a snapshot deletes the other run's live grant and later re-adds a dead SID.
/// </summary>
public sealed class WindowsAppContainerRestoreJournalTests : IDisposable
{
    private const string RunA =
        "S-1-15-2-1111111111-2222222222-3333333333-1444444444-1555555555-1666666666-1777777777";

    private const string RunB =
        "S-1-15-2-1888888888-1999999999-1212121212-1343434343-1565656565-1787878787-1909090909";

    private readonly string _root;

    private readonly string _journal;

    public WindowsAppContainerRestoreJournalTests()
    {
        _root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "arcanum-acl-journal-" + Guid.NewGuid().ToString("N"))).FullName;
        _journal = Path.Combine(_root, "undo.journal");
        File.WriteAllBytes(_journal, []);
    }

    public void Dispose()
    {
        _ = TestDirectoryCleanup.TryDelete(_root, nameof(WindowsAppContainerRestoreJournalTests));
    }

    [Fact]
    public void Killed_broker_leaves_every_grant_and_the_profile_recoverable()
    {
        WindowsAppContainerRestoreJournal.RecordProfile(_journal, "RetroDownfall.Arcanum.Tool.abc");
        WindowsAppContainerRestoreJournal.RecordGrant(_journal, @"C:\workspace", RunA);
        WindowsAppContainerRestoreJournal.RecordGrant(_journal, @"C:\spell scripts", RunA);
        WindowsAppContainerRestoreJournal.RecordGrant(_journal, @"C:\Temp\arcanum-win-child", RunA);

        List<string> removed = [];
        List<string> deleted = [];

        bool complete = WindowsAppContainerRestoreJournal.Replay(
            _journal,
            (path, sid) =>
            {
                removed.Add(path + "=" + sid);
                return true;
            },
            profile =>
            {
                deleted.Add(profile);
                return true;
            });

        string[] expected =
        [
            @"C:\Temp\arcanum-win-child=" + RunA,
            @"C:\spell scripts=" + RunA,
            @"C:\workspace=" + RunA,
        ];

        Assert.True(complete);
        Assert.Equal(expected, removed);
        Assert.Equal(["RetroDownfall.Arcanum.Tool.abc"], deleted);
        Assert.Empty(WindowsAppContainerRestoreJournal.Read(_journal).Grants);
    }

    [Fact]
    public void Broker_that_completed_its_own_restore_leaves_nothing_to_replay()
    {
        WindowsAppContainerRestoreJournal.RecordProfile(_journal, "RetroDownfall.Arcanum.Tool.abc");
        WindowsAppContainerRestoreJournal.RecordGrant(_journal, @"C:\workspace", RunA);
        WindowsAppContainerRestoreJournal.Clear(_journal);

        List<string> removed = [];
        List<string> deleted = [];

        bool complete = WindowsAppContainerRestoreJournal.Replay(
            _journal,
            (path, sid) =>
            {
                removed.Add(path);
                return true;
            },
            profile =>
            {
                deleted.Add(profile);
                return true;
            });

        Assert.True(complete);
        Assert.Empty(removed);
        Assert.Empty(deleted);
    }

    [Fact]
    public void Record_torn_by_the_kill_is_ignored_and_complete_records_still_replay()
    {
        WindowsAppContainerRestoreJournal.RecordGrant(_journal, @"C:\workspace", RunA);

        // TerminateProcess can land mid-append, so only newline-terminated records are trustworthy.
        File.AppendAllText(
            _journal,
            "G " + Convert.ToBase64String("C:\\torn"u8.ToArray()) + " S-1-15"[..4]);

        List<string> removed = [];

        bool complete = WindowsAppContainerRestoreJournal.Replay(
            _journal,
            (path, sid) =>
            {
                removed.Add(path);
                return true;
            },
            static profile => true);

        Assert.True(complete);
        Assert.Equal([@"C:\workspace"], removed);
    }

    [Fact]
    public void Failed_restore_is_reported_and_keeps_the_log_for_the_operator()
    {
        WindowsAppContainerRestoreJournal.RecordGrant(_journal, @"C:\workspace", RunA);

        bool complete = WindowsAppContainerRestoreJournal.Replay(
            _journal,
            static (path, sid) => throw new UnauthorizedAccessException("denied"),
            static profile => true);

        Assert.False(complete);
        Assert.Single(WindowsAppContainerRestoreJournal.Read(_journal).Grants);
    }

    /// <summary>
    /// The broker refuses to run without an undo-log path, so the path has to survive the
    /// source-generated payload the host hands it — a dropped member would fail the jail closed.
    /// </summary>
    [Fact]
    public void Undo_log_path_reaches_the_broker_through_the_payload()
    {
        SandboxExecHelperPayload payload = new()
        {
            Target = @"C:\tool.exe",
            WindowsProfileName = "RetroDownfall.Arcanum.Tool.abc",
            WindowsRestoreJournalPath = _journal,
            WindowsJobAssignedSignalPath = @"C:\Temp\arcanum-win-job.signal",
        };

        string json = JsonSerializer.Serialize(
            payload,
            SandboxExecJsonContext.Default.SandboxExecHelperPayload);
        SandboxExecHelperPayload? restored = JsonSerializer.Deserialize(
            json,
            SandboxExecJsonContext.Default.SandboxExecHelperPayload);

        Assert.Equal(_journal, restored?.WindowsRestoreJournalPath);
        Assert.Equal(@"C:\Temp\arcanum-win-job.signal", restored?.WindowsJobAssignedSignalPath);
    }

    [Fact]
    public void Corrupt_record_does_not_block_the_records_around_it()
    {
        WindowsAppContainerRestoreJournal.RecordGrant(_journal, @"C:\workspace", RunA);
        File.AppendAllText(_journal, "G not-base64 ????\n");
        WindowsAppContainerRestoreJournal.RecordGrant(_journal, @"C:\Temp\arcanum-win-child", RunA);

        List<string> removed = [];

        bool complete = WindowsAppContainerRestoreJournal.Replay(
            _journal,
            (path, sid) =>
            {
                removed.Add(path);
                return true;
            },
            static profile => true);

        Assert.False(complete);
        Assert.Equal([@"C:\Temp\arcanum-win-child", @"C:\workspace"], removed);
    }

    [Fact]
    public void Grant_record_carries_the_run_sid_not_a_descriptor_snapshot()
    {
        WindowsAppContainerRestoreJournal.RecordGrant(_journal, @"C:\workspace", RunA);

        WindowsAppContainerRestorePlan plan = WindowsAppContainerRestoreJournal.Read(_journal);

        WindowsAppContainerGrantRecord grant = Assert.Single(plan.Grants);
        Assert.Equal(@"C:\workspace", grant.Path);
        Assert.Equal(RunA, grant.Sid);
        Assert.Equal(0, plan.UnreadableRecords);
    }

    [Fact]
    public void Overlapping_runs_on_one_root_each_remove_only_their_own_ace()
    {
        // A synthetic DACL: the SIDs holding an ACE on each root. The original state carries one entry
        // that neither run may disturb.
        const string Owner = "S-1-5-21-1-2-3-1001";
        const string Workspace = @"C:\workspace";
        Dictionary<string, List<string>> dacl = new(StringComparer.OrdinalIgnoreCase)
        {
            [Workspace] = [Owner],
        };
        string journalB = Path.Combine(_root, "undo-b.journal");
        File.WriteAllBytes(journalB, []);

        // Run A grants, then run B grants the same root while A is still running.
        WindowsAppContainerRestoreJournal.RecordGrant(_journal, Workspace, RunA);
        dacl[Workspace].Add(RunA);
        WindowsAppContainerRestoreJournal.RecordGrant(journalB, Workspace, RunB);
        dacl[Workspace].Add(RunB);

        bool RemoveFromFreshDacl(string path, string sid)
        {
            dacl[path].RemoveAll(entry => entry == sid);
            return true;
        }

        Assert.True(WindowsAppContainerRestoreJournal.Replay(_journal, RemoveFromFreshDacl, static _ => true));
        Assert.Equal([Owner, RunB], dacl[Workspace]);

        Assert.True(WindowsAppContainerRestoreJournal.Replay(journalB, RemoveFromFreshDacl, static _ => true));
        Assert.Equal([Owner], dacl[Workspace]);
    }

    [Fact]
    public void Snapshot_record_from_an_older_broker_is_residue_not_a_descriptor_to_reapply()
    {
        // The retired format carried a whole security descriptor; reapplying it would overwrite every
        // other run's live grant. It is reported as residue instead.
        File.AppendAllText(
            _journal,
            "A " + Convert.ToBase64String("C:\\workspace"u8.ToArray()) + " AQID\n");

        List<string> removed = [];

        bool complete = WindowsAppContainerRestoreJournal.Replay(
            _journal,
            (path, sid) =>
            {
                removed.Add(path);
                return true;
            },
            static profile => true);

        Assert.False(complete);
        Assert.Empty(removed);
        Assert.Equal(1, WindowsAppContainerRestoreJournal.Read(_journal).UnreadableRecords);
    }

    [Theory]
    [InlineData("S-1-5-32-544")]
    [InlineData("S-1-5-21-1-2-3-1001")]
    [InlineData("S-1-15-2-1")]
    [InlineData("S-1-15-2-2")]
    [InlineData("S-1-15-3-1024-1065365936-1281604716-3511738428-1654721687-432734479-3232135806-4053264122")]
    [InlineData("S-1-15-2-1-2-3-4-5-6")]
    [InlineData("S-1-15-2-1-2-3-4-5-6-7-8")]
    [InlineData("S-1-15-2-1-2-3-4294967296-5-6-7")]
    [InlineData("S-1-15-2-1-2-3-04-5-6-7")]
    public void Grant_for_anything_but_a_per_run_AppContainer_sid_is_refused_before_it_is_recorded(string sid)
    {
        // Replay removes every explicit ACE the recorded SID holds on the recorded root. A record naming
        // Administrators, the user, ALL APPLICATION PACKAGES or a capability would strip access this run
        // never granted, so only the shape a per-run profile SID has — S-1-15-2 plus seven 32-bit
        // sub-authorities — is ever written.
        Assert.Throws<ArgumentException>(() =>
            WindowsAppContainerRestoreJournal.RecordGrant(_journal, @"C:\workspace", sid));

        Assert.Empty(File.ReadAllBytes(_journal));
    }

    [Fact]
    public void Replay_never_removes_a_recorded_sid_that_is_not_a_per_run_AppContainer_sid()
    {
        // The log is owner-only, but a record that names a broad SID must still never reach the ACL
        // edit: it is residue to report, not an ACE to purge.
        string workspace = Convert.ToBase64String("C:\\workspace"u8.ToArray());
        File.AppendAllText(_journal, "G " + workspace + " S-1-5-32-544\n");
        File.AppendAllText(_journal, "G " + workspace + " S-1-15-2-1\n");
        WindowsAppContainerRestoreJournal.RecordGrant(_journal, @"C:\workspace", RunA);

        List<string> removed = [];

        bool complete = WindowsAppContainerRestoreJournal.Replay(
            _journal,
            (path, sid) =>
            {
                removed.Add(sid);
                return true;
            },
            static profile => true);

        Assert.False(complete);
        Assert.Equal([RunA], removed);
        Assert.Equal(2, WindowsAppContainerRestoreJournal.Read(_journal).UnreadableRecords);
    }

    [Fact]
    public void Record_with_a_malformed_sid_is_unreadable()
    {
        File.AppendAllText(
            _journal,
            "G " + Convert.ToBase64String("C:\\workspace"u8.ToArray()) + " not-a-sid\n");

        WindowsAppContainerRestorePlan plan = WindowsAppContainerRestoreJournal.Read(_journal);

        Assert.Equal(1, plan.UnreadableRecords);
        Assert.Empty(plan.Grants);
    }
}
