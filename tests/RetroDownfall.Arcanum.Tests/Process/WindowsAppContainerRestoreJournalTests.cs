using System.Text.Json;
using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

namespace RetroDownfall.Arcanum.Tests.Process;

/// <summary>
/// The Windows broker grants a per-run AppContainer SID an inheritable Modify ACE on every declared
/// root — including the campaign workspace — and undoes it in a <c>finally</c>. The host kills the
/// broker with TerminateProcess on timeout, cancellation, or a Job Object kill, and managed finally
/// blocks do not run then: without a durable undo log the original descriptors exist only in the
/// dead broker's memory and the workspace DACL keeps one orphaned ACE per killed run forever.
/// </summary>
public sealed class WindowsAppContainerRestoreJournalTests : IDisposable
{
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
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
        }
    }

    [Fact]
    public void Killed_broker_leaves_every_grant_and_the_profile_recoverable()
    {
        WindowsAppContainerRestoreJournal.RecordProfile(_journal, "RetroDownfall.Arcanum.Tool.abc");
        WindowsAppContainerRestoreJournal.RecordGrant(_journal, @"C:\workspace", [1, 2, 3]);
        WindowsAppContainerRestoreJournal.RecordGrant(_journal, @"C:\spell scripts", [4, 5]);
        WindowsAppContainerRestoreJournal.RecordGrant(_journal, @"C:\Temp\arcanum-win-child", [6]);

        List<string> restored = [];
        List<string> deleted = [];

        bool complete = WindowsAppContainerRestoreJournal.Replay(
            _journal,
            (path, descriptor) =>
            {
                restored.Add(path + "=" + Convert.ToBase64String(descriptor));
                return true;
            },
            profile =>
            {
                deleted.Add(profile);
                return true;
            });

        string[] expected =
        [
            @"C:\Temp\arcanum-win-child=" + Convert.ToBase64String([6]),
            @"C:\spell scripts=" + Convert.ToBase64String([4, 5]),
            @"C:\workspace=" + Convert.ToBase64String([1, 2, 3]),
        ];

        Assert.True(complete);
        Assert.Equal(expected, restored);
        Assert.Equal(["RetroDownfall.Arcanum.Tool.abc"], deleted);
        Assert.Empty(WindowsAppContainerRestoreJournal.Read(_journal).Grants);
    }

    [Fact]
    public void Broker_that_completed_its_own_restore_leaves_nothing_to_replay()
    {
        WindowsAppContainerRestoreJournal.RecordProfile(_journal, "RetroDownfall.Arcanum.Tool.abc");
        WindowsAppContainerRestoreJournal.RecordGrant(_journal, @"C:\workspace", [1, 2, 3]);
        WindowsAppContainerRestoreJournal.Clear(_journal);

        List<string> restored = [];
        List<string> deleted = [];

        bool complete = WindowsAppContainerRestoreJournal.Replay(
            _journal,
            (path, descriptor) =>
            {
                restored.Add(path);
                return true;
            },
            profile =>
            {
                deleted.Add(profile);
                return true;
            });

        Assert.True(complete);
        Assert.Empty(restored);
        Assert.Empty(deleted);
    }

    [Fact]
    public void Record_torn_by_the_kill_is_ignored_and_complete_records_still_replay()
    {
        WindowsAppContainerRestoreJournal.RecordGrant(_journal, @"C:\workspace", [1, 2, 3]);

        // TerminateProcess can land mid-append, so only newline-terminated records are trustworthy.
        File.AppendAllText(
            _journal,
            "A " + Convert.ToBase64String("C:\\torn"u8.ToArray()) + " AQID"[..2]);

        List<string> restored = [];

        bool complete = WindowsAppContainerRestoreJournal.Replay(
            _journal,
            (path, descriptor) =>
            {
                restored.Add(path);
                return true;
            },
            static profile => true);

        Assert.True(complete);
        Assert.Equal([@"C:\workspace"], restored);
    }

    [Fact]
    public void Failed_restore_is_reported_and_keeps_the_log_for_the_operator()
    {
        WindowsAppContainerRestoreJournal.RecordGrant(_journal, @"C:\workspace", [1, 2, 3]);

        bool complete = WindowsAppContainerRestoreJournal.Replay(
            _journal,
            static (path, descriptor) => throw new UnauthorizedAccessException("denied"),
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
        };

        string json = JsonSerializer.Serialize(
            payload,
            SandboxExecJsonContext.Default.SandboxExecHelperPayload);
        SandboxExecHelperPayload? restored = JsonSerializer.Deserialize(
            json,
            SandboxExecJsonContext.Default.SandboxExecHelperPayload);

        Assert.Equal(_journal, restored?.WindowsRestoreJournalPath);
    }

    [Fact]
    public void Corrupt_record_does_not_block_the_records_around_it()
    {
        WindowsAppContainerRestoreJournal.RecordGrant(_journal, @"C:\workspace", [1, 2, 3]);
        File.AppendAllText(_journal, "A not-base64 ????\n");
        WindowsAppContainerRestoreJournal.RecordGrant(_journal, @"C:\Temp\arcanum-win-child", [6]);

        List<string> restored = [];

        bool complete = WindowsAppContainerRestoreJournal.Replay(
            _journal,
            (path, descriptor) =>
            {
                restored.Add(path);
                return true;
            },
            static profile => true);

        Assert.False(complete);
        Assert.Equal([@"C:\Temp\arcanum-win-child", @"C:\workspace"], restored);
    }
}
