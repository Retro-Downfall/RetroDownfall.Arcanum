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

    private const string UnreinstatedGrimoireSecret =
        "The prior installation's local secrets could not all be reinstated: the Grimoire encryption "
        + "secret (IOException).";

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

    /// <summary>
    /// A Commit-phase journal whose staged generation is still in staging while neither a live nor a
    /// displaced root exists is not a completed commit, so the staged generation is kept.
    /// </summary>
    /// <remarks>
    /// The fall-through used to read every unlisted shape as "the commit had completed; only staging
    /// cleanup remained" and delete staging — here the only complete tree left on disk. A completed
    /// commit is exactly staged gone, live present, nothing displaced; anything else unlisted has to be
    /// decided by an operator.
    /// </remarks>
    [Fact]
    public void A_commit_phase_journal_with_no_live_and_no_displaced_root_demands_reconciliation()
    {
        Interrupted interrupted = Interrupt(
            BackupRestorePhase.Commit,
            live: false,
            staged: true,
            displaced: false);

        BackupRestoreRecoveryReport report = Assert.Single(BackupRestoreRecovery.Resolve(_live));

        Assert.Equal(BackupRestoreRecoveryOutcome.ReconciliationRequired, report.Outcome);

        Assert.True(Directory.Exists(interrupted.StagingRoot));

        Assert.Equal("staged", File.ReadAllText(Path.Combine(interrupted.StagedRoot, "marker.txt")));
    }

    /// <summary>
    /// The one shape the fall-through used to stand for is still a completed commit: a first-time
    /// restore with nothing to displace leaves staged gone, live present, and no displaced root.
    /// </summary>
    [Fact]
    public void A_commit_phase_journal_with_only_the_live_root_left_is_a_completed_commit()
    {
        Interrupted interrupted = Interrupt(
            BackupRestorePhase.Commit,
            live: true,
            staged: false,
            displaced: false);

        BackupRestoreRecoveryReport report = Assert.Single(BackupRestoreRecovery.Resolve(_live));

        Assert.Equal(BackupRestoreRecoveryOutcome.CommitCompleted, report.Outcome);

        Assert.False(Directory.Exists(interrupted.StagingRoot));

        Assert.Equal("live", File.ReadAllText(Path.Combine(_live, "marker.txt")));
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

    /// <summary>
    /// A <c>new-profile-root</c> restore stages beside its destination so commit stays a same-volume
    /// rename, which puts staging outside the live root's parent. Recovery must still find it, or a
    /// process death leaks decrypted archive contents that nothing ever sweeps.
    /// </summary>
    [Fact]
    public void Staging_beside_a_foreign_destination_is_resolved_from_the_staging_index()
    {
        string elsewhere = Path.Combine(_root, "another-volume");

        Directory.CreateDirectory(elsewhere);

        Interrupted interrupted = Interrupt(
            BackupRestorePhase.Stage,
            live: true,
            staged: true,
            displaced: false,
            stagingParent: elsewhere,
            conflictMode: BackupRestoreConflictMode.NewProfileRoot);

        BackupRestoreRecoveryReport report = Assert.Single(BackupRestoreRecovery.Resolve(_live));

        Assert.Equal(BackupRestoreRecoveryOutcome.Discarded, report.Outcome);

        Assert.Equal(interrupted.StagingRoot, report.StagingRoot);

        Assert.False(Directory.Exists(interrupted.StagingRoot));

        Assert.Empty(BackupRestoreStagingIndex.Read(_live));
    }

    /// <summary>
    /// A journal recording that its restore could not reinstate a secret demands reconciliation over a
    /// tree whose shape alone reads as a commit that never began.
    /// </summary>
    /// <remarks>
    /// <c>staged/</c> and the live root present with nothing displaced is what a verified reversal
    /// leaves, and it is also what an interruption before the first rename leaves. Only the journal can
    /// tell them apart, and for the first the secret store still holds what the restore installed.
    /// </remarks>
    [Fact]
    public void A_commit_journal_recording_unreinstated_secrets_demands_reconciliation_over_a_returned_tree()
    {
        string safety = Path.Combine(_root, "safety.arcbackup");

        Interrupted interrupted = Interrupt(
            BackupRestorePhase.Commit,
            live: true,
            staged: true,
            displaced: false,
            secretReinstatementFailure: UnreinstatedGrimoireSecret,
            safetyBackupPath: safety);

        BackupRestoreRecoveryReport report = Assert.Single(BackupRestoreRecovery.Resolve(_live));

        Assert.Equal(BackupRestoreRecoveryOutcome.ReconciliationRequired, report.Outcome);

        Assert.Contains(UnreinstatedGrimoireSecret, report.Detail, StringComparison.Ordinal);

        Assert.Contains(safety, report.Detail, StringComparison.Ordinal);

        Assert.Equal("live", File.ReadAllText(Path.Combine(_live, "marker.txt")));

        Assert.True(Directory.Exists(interrupted.StagedRoot));

        Assert.NotNull(BackupRestoreJournal.TryRead(interrupted.StagingRoot));
    }

    /// <summary>
    /// The same journal over a tree whose prior installation is still displaced gets the ordinary
    /// repair, the move back, and still demands reconciliation rather than reporting a rollback.
    /// </summary>
    [Fact]
    public void A_commit_journal_recording_unreinstated_secrets_moves_the_displaced_installation_back_and_keeps_staging()
    {
        Interrupted interrupted = Interrupt(
            BackupRestorePhase.Commit,
            live: false,
            staged: true,
            displaced: true,
            secretReinstatementFailure: UnreinstatedGrimoireSecret);

        BackupRestoreRecoveryReport report = Assert.Single(BackupRestoreRecovery.Resolve(_live));

        Assert.Equal(BackupRestoreRecoveryOutcome.ReconciliationRequired, report.Outcome);

        Assert.Contains("No pre-restore safety backup was taken", report.Detail, StringComparison.Ordinal);

        Assert.Equal("live", File.ReadAllText(Path.Combine(_live, "marker.txt")));

        Assert.False(Directory.Exists(interrupted.DisplacedRoot));

        Assert.NotNull(BackupRestoreJournal.TryRead(interrupted.StagingRoot));
    }

    /// <summary>
    /// A journal written before the secret failure was recorded still reads, and its returned tree still
    /// resolves as the rollback it always was.
    /// </summary>
    [Fact]
    public void A_journal_without_the_secret_failure_member_still_resolves_its_returned_tree_as_rolled_back()
    {
        Interrupted interrupted = Interrupt(
            BackupRestorePhase.Commit,
            live: true,
            staged: true,
            displaced: false);

        string journalPath = Path.Combine(interrupted.StagingRoot, BackupRestoreJournal.FileName);

        string written = File.ReadAllText(journalPath);

        string legacy = written.Replace(",\"secretReinstatementFailure\":null", string.Empty, StringComparison.Ordinal);

        Assert.NotEqual(written, legacy);

        File.WriteAllText(journalPath, legacy);

        BackupRestoreRecoveryReport report = Assert.Single(BackupRestoreRecovery.Resolve(_live));

        Assert.Equal(BackupRestoreRecoveryOutcome.RolledBack, report.Outcome);

        Assert.False(Directory.Exists(interrupted.StagingRoot));
    }

    [Fact]
    public void A_staging_index_entry_whose_staging_root_is_already_gone_is_pruned()
    {
        Directory.CreateDirectory(_live);

        BackupRestoreStagingIndex.Add(
            _live,
            Path.Combine(_root, "gone", BackupRestoreJournal.CreateStagingName()));

        Assert.Empty(BackupRestoreRecovery.Resolve(_live));

        Assert.Empty(BackupRestoreStagingIndex.Read(_live));
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
        string? liveRootOverride = null,
        string? stagingParent = null,
        BackupRestoreConflictMode conflictMode = BackupRestoreConflictMode.ReplaceInstallation,
        string? secretReinstatementFailure = null,
        string? safetyBackupPath = null)
    {
        string stagingRoot = Path.Combine(
            stagingParent ?? _root,
            BackupRestoreJournal.CreateStagingName());

        BackupRestoreStagingIndex.Add(_live, stagingRoot);

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
                conflictMode,
                phase,
                liveRootOverride ?? _live,
                stagedRoot,
                displacedRoot,
                safetyBackupPath,
                Path.Combine(_root, "source.arcbackup"),
                owned.VolumeId,
                owned.FileId,
                secretReinstatementFailure));

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
