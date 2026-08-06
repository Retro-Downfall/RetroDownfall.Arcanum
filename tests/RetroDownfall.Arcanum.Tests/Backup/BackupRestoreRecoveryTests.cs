using RetroDownfall.Arcanum.Core.Backup;

using RetroDownfall.Arcanum.Infrastructure.Backup;

namespace RetroDownfall.Arcanum.Tests.Backup;

/// <summary>
/// Fault injection at every commit boundary. Each case leaves the filesystem in the exact shape a
/// killed process would, then asserts recovery resolves it to one complete tree — never a mixture.
/// </summary>
public sealed class BackupRestoreRecoveryTests : IDisposable
{

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "arcanum-restore-recovery-" + Guid.NewGuid().ToString("N"));

    private readonly string _live;

    public BackupRestoreRecoveryTests()
    {

        _live = Path.Combine(_root, "arcanum");

        Directory.CreateDirectory(_root);

    }

    public void Dispose()
    {

        if (Directory.Exists(_root))
        {

            Directory.Delete(_root, recursive: true);

        }

    }

    [Theory]
    [InlineData(BackupRestorePhase.Stage)]
    [InlineData(BackupRestorePhase.Migrate)]
    [InlineData(BackupRestorePhase.RemapPaths)]
    [InlineData(BackupRestorePhase.Validate)]
    [InlineData(BackupRestorePhase.SafetyPoint)]
    public void An_interruption_before_commit_discards_staging_and_keeps_the_installation(
        BackupRestorePhase phase)
    {

        Interrupted interrupted = Interrupt(phase, live: true, staged: true, displaced: false);

        BackupRestoreRecoveryReport report = Assert.Single(BackupRestoreRecovery.Resolve(_live));

        Assert.Equal(BackupRestoreRecoveryOutcome.Discarded, report.Outcome);

        Assert.Equal("live", File.ReadAllText(Path.Combine(_live, "marker.txt")));

        Assert.False(Directory.Exists(interrupted.StagingRoot));

    }

    [Fact]
    public void An_interruption_between_the_two_renames_restores_the_prior_installation()
    {

        Interrupted interrupted = Interrupt(
            BackupRestorePhase.Commit,
            live: false,
            staged: true,
            displaced: true);

        BackupRestoreRecoveryReport report = Assert.Single(BackupRestoreRecovery.Resolve(_live));

        Assert.Equal(BackupRestoreRecoveryOutcome.RolledBack, report.Outcome);

        Assert.Equal("live", File.ReadAllText(Path.Combine(_live, "marker.txt")));

        Assert.False(Directory.Exists(interrupted.StagingRoot));

    }

    [Fact]
    public void An_interruption_before_the_first_rename_is_a_no_op_rollback()
    {

        Interrupted interrupted = Interrupt(
            BackupRestorePhase.Commit,
            live: true,
            staged: true,
            displaced: false);

        BackupRestoreRecoveryReport report = Assert.Single(BackupRestoreRecovery.Resolve(_live));

        Assert.Equal(BackupRestoreRecoveryOutcome.RolledBack, report.Outcome);

        Assert.Equal("live", File.ReadAllText(Path.Combine(_live, "marker.txt")));

        Assert.False(Directory.Exists(interrupted.StagingRoot));

    }

    [Fact]
    public void An_interruption_after_both_renames_keeps_the_new_tree_and_demands_reconciliation()
    {

        Interrupted interrupted = Interrupt(
            BackupRestorePhase.Commit,
            live: true,
            staged: false,
            displaced: true);

        BackupRestoreRecoveryReport report = Assert.Single(BackupRestoreRecovery.Resolve(_live));

        Assert.Equal(BackupRestoreRecoveryOutcome.ReconciliationRequired, report.Outcome);

        Assert.Equal("live", File.ReadAllText(Path.Combine(_live, "marker.txt")));

        Assert.True(Directory.Exists(interrupted.DisplacedRoot));

    }

    [Theory]
    [InlineData(BackupRestorePhase.Reconcile)]
    [InlineData(BackupRestorePhase.Cleanup)]
    public void An_interruption_after_commit_only_needs_staging_cleanup(BackupRestorePhase phase)
    {

        Interrupted interrupted = Interrupt(phase, live: true, staged: false, displaced: true);

        BackupRestoreRecoveryReport report = Assert.Single(BackupRestoreRecovery.Resolve(_live));

        Assert.Equal(BackupRestoreRecoveryOutcome.CommitCompleted, report.Outcome);

        Assert.False(Directory.Exists(interrupted.StagingRoot));

        Assert.Equal("live", File.ReadAllText(Path.Combine(_live, "marker.txt")));

    }

    [Fact]
    public void A_journal_describing_another_installation_is_left_alone()
    {

        Interrupted interrupted = Interrupt(
            BackupRestorePhase.Commit,
            live: true,
            staged: true,
            displaced: false,
            liveRootOverride: Path.Combine(_root, "some-other-install"));

        BackupRestoreRecoveryReport report = Assert.Single(BackupRestoreRecovery.Resolve(_live));

        Assert.Equal(BackupRestoreRecoveryOutcome.ReconciliationRequired, report.Outcome);

        Assert.True(Directory.Exists(interrupted.StagingRoot));

    }

    [Fact]
    public void Staging_without_a_journal_is_never_touched()
    {

        Directory.CreateDirectory(_live);

        string orphan = Path.Combine(_root, ".arcanum-restore-00000000000000000000000000000000");

        Directory.CreateDirectory(orphan);

        Assert.Empty(BackupRestoreRecovery.Resolve(_live));

        Assert.True(Directory.Exists(orphan));

    }

    private Interrupted Interrupt(
        BackupRestorePhase phase,
        bool live,
        bool staged,
        bool displaced,
        string? liveRootOverride = null)
    {

        string stagingRoot = Path.Combine(_root, BackupRestoreJournal.CreateStagingName());

        OwnedTemporaryDirectory owned = OwnedTemporaryDirectory.Create(stagingRoot);

        string stagedRoot = Path.Combine(stagingRoot, BackupRestoreJournal.StagedDirectoryName);

        string displacedRoot = Path.Combine(stagingRoot, BackupRestoreJournal.DisplacedDirectoryName);

        if (live)
        {

            Write(_live, "live");

        }

        if (staged)
        {

            Write(stagedRoot, "staged");

        }

        if (displaced)
        {

            Write(displacedRoot, "live");

        }

        _ = BackupRestoreJournal.Write(
            stagingRoot,
            new BackupRestoreJournalRecord(
                BackupRestoreJournal.CurrentVersion,
                Guid.NewGuid(),
                BackupRestoreConflictMode.ReplaceInstallation,
                phase,
                liveRootOverride ?? _live,
                stagedRoot,
                displacedRoot,
                SafetyBackupPath: null,
                Path.Combine(_root, "source.arcbackup"),
                owned.VolumeId,
                owned.FileId));

        return new Interrupted(stagingRoot, stagedRoot, displacedRoot);

    }

    private static void Write(string directory, string marker)
    {

        Directory.CreateDirectory(directory);

        File.WriteAllText(Path.Combine(directory, "marker.txt"), marker);

    }

    private sealed record Interrupted(
        string StagingRoot,
        string StagedRoot,
        string DisplacedRoot);

}
