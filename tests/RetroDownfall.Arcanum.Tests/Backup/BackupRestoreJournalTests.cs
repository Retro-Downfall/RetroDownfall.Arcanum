using RetroDownfall.Arcanum.Core.Backup;

using RetroDownfall.Arcanum.Infrastructure.Backup;

namespace RetroDownfall.Arcanum.Tests.Backup;

public sealed class BackupRestoreJournalTests : IDisposable
{

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "arcanum-restore-journal-" + Guid.NewGuid().ToString("N"));

    public BackupRestoreJournalTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {

        if (Directory.Exists(_root))
        {

            Directory.Delete(_root, recursive: true);

        }

    }

    [Fact]
    public void Journal_round_trips_root_identities_phase_and_rollback_artifacts()
    {

        string stagingRoot = Path.Combine(_root, ".arcanum-restore-0123456789abcdef0123456789abcdef");

        Directory.CreateDirectory(stagingRoot);

        BackupRestoreJournalRecord written = BackupRestoreJournal.Write(
            stagingRoot,
            new BackupRestoreJournalRecord(
                BackupRestoreJournal.CurrentVersion,
                Guid.Parse("22222222-2222-2222-2222-222222222222"),
                BackupRestoreConflictMode.ReplaceInstallation,
                BackupRestorePhase.Stage,
                LiveRoot: Path.Combine(_root, "arcanum"),
                StagedRoot: Path.Combine(stagingRoot, "staged"),
                DisplacedRoot: Path.Combine(stagingRoot, "previous"),
                SafetyBackupPath: Path.Combine(_root, "safety.arcbackup"),
                ArchivePath: Path.Combine(_root, "source.arcbackup"),
                StagingVolumeId: 7,
                StagingFileId: 9));

        BackupRestoreJournalRecord read = Assert.IsType<BackupRestoreJournalRecord>(
            BackupRestoreJournal.TryRead(stagingRoot));

        Assert.Equal(written, read);

        Assert.True(BackupRestoreJournal.Exists(stagingRoot));

        BackupRestoreJournal.Delete(stagingRoot);

        Assert.Null(BackupRestoreJournal.TryRead(stagingRoot));

        Assert.False(BackupRestoreJournal.Exists(stagingRoot));

    }

    [Fact]
    public void Journal_advance_replaces_the_phase_atomically_without_losing_identities()
    {

        string stagingRoot = Path.Combine(_root, ".arcanum-restore-ffffffffffffffffffffffffffffffff");

        Directory.CreateDirectory(stagingRoot);

        BackupRestoreJournalRecord initial = BackupRestoreJournal.Write(
            stagingRoot,
            Record(stagingRoot, BackupRestorePhase.Validate));

        BackupRestoreJournalRecord advanced = BackupRestoreJournal.Advance(
            stagingRoot,
            initial,
            BackupRestorePhase.Commit);

        Assert.Equal(BackupRestorePhase.Commit, advanced.Phase);

        Assert.Equal(initial.LiveRoot, advanced.LiveRoot);

        Assert.Equal(initial.StagedRoot, advanced.StagedRoot);

        Assert.Equal(
            BackupRestorePhase.Commit,
            BackupRestoreJournal.TryRead(stagingRoot)?.Phase);

        Assert.Single(
            Directory.GetFiles(stagingRoot, "*", SearchOption.TopDirectoryOnly));

    }

    [Fact]
    public void Corrupt_journal_bytes_read_as_absent_rather_than_throwing()
    {

        string stagingRoot = Path.Combine(_root, ".arcanum-restore-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

        Directory.CreateDirectory(stagingRoot);

        _ = BackupRestoreJournal.Write(stagingRoot, Record(stagingRoot, BackupRestorePhase.Stage));

        File.WriteAllText(
            Path.Combine(stagingRoot, BackupRestoreJournal.FileName),
            "{not json");

        Assert.Null(BackupRestoreJournal.TryRead(stagingRoot));

    }

    [Fact]
    public void Unsupported_journal_version_reads_as_absent()
    {

        string stagingRoot = Path.Combine(_root, ".arcanum-restore-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");

        Directory.CreateDirectory(stagingRoot);

        _ = BackupRestoreJournal.Write(
            stagingRoot,
            Record(stagingRoot, BackupRestorePhase.Stage) with
            {

                Version = BackupRestoreJournal.CurrentVersion + 1,

            });

        Assert.Null(BackupRestoreJournal.TryRead(stagingRoot));

    }

    [Fact]
    public void Discover_finds_only_canonically_named_staging_roots_holding_a_journal()
    {

        string named = Path.Combine(_root, ".arcanum-restore-11111111111111111111111111111111");

        string unnamed = Path.Combine(_root, "not-a-restore-root");

        string journalless = Path.Combine(_root, ".arcanum-restore-22222222222222222222222222222222");

        Directory.CreateDirectory(named);

        Directory.CreateDirectory(unnamed);

        Directory.CreateDirectory(journalless);

        _ = BackupRestoreJournal.Write(named, Record(named, BackupRestorePhase.Commit));

        _ = BackupRestoreJournal.Write(unnamed, Record(unnamed, BackupRestorePhase.Commit));

        Assert.Equal(
            [named],
            BackupRestoreJournal.Discover(_root));

    }

    private BackupRestoreJournalRecord Record(
        string stagingRoot,
        BackupRestorePhase phase) =>
        new(
            BackupRestoreJournal.CurrentVersion,
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            BackupRestoreConflictMode.ReplaceInstallation,
            phase,
            Path.Combine(_root, "arcanum"),
            Path.Combine(stagingRoot, "staged"),
            Path.Combine(stagingRoot, "previous"),
            SafetyBackupPath: null,
            Path.Combine(_root, "source.arcbackup"),
            StagingVolumeId: 1,
            StagingFileId: 2);

}
