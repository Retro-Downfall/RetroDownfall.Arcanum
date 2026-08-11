using RetroDownfall.Arcanum.Core.Backup;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

internal enum BackupRestoreRecoveryOutcome
{

    /// <summary>Staging was found before any destructive step and was simply removed.</summary>
    Discarded = 0,

    /// <summary>An interrupted commit was reversed; the prior installation is back in place.</summary>
    RolledBack = 1,

    /// <summary>The commit had completed; only staging cleanup remained.</summary>
    CommitCompleted = 2,

    /// <summary>The commit landed but post-commit work is unverifiable; an operator must decide.</summary>
    ReconciliationRequired = 3,

}

internal sealed record BackupRestoreRecoveryReport(
    string StagingRoot,
    BackupRestoreRecoveryOutcome Outcome,
    BackupRestorePhase Phase,
    string Detail);

/// <summary>
/// Resolves restore staging left behind by a process that died mid-restore.
/// </summary>
/// <remarks>
/// The journal plus the filesystem's own evidence are jointly authoritative: the phase says how far
/// the restore intended to get, and the presence of the staged, live, and displaced roots says how
/// far it actually got. Every combination maps to exactly one action — discard, reverse, finish, or
/// stop and ask — so a restart never has to guess and never accepts a half-swapped tree.
/// <para>
/// Which roots get looked at is a separate question from what to do with them. Staging normally sits
/// beside the live root and is found by sweeping its parent; a new-profile restore has to stage
/// beside its destination instead, and <see cref="BackupRestoreStagingIndex"/> is what keeps those
/// roots reachable from here.
/// </para>
/// </remarks>
internal static class BackupRestoreRecovery
{

    public static IReadOnlyList<BackupRestoreRecoveryReport> Resolve(string grimoireDirectory)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(grimoireDirectory);

        string liveRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(grimoireDirectory));

        string? parent = Path.GetDirectoryName(liveRoot);

        if (parent is null)
        {

            return [];

        }

        IReadOnlyList<string> indexed = BackupRestoreStagingIndex.Read(grimoireDirectory);

        List<string> indexedRoots = [.. indexed.Select(TryNormalize).OfType<string>()];

        List<string> stagingRoots = [.. BackupRestoreJournal.Discover(parent)];

        HashSet<string> seen = new(stagingRoots, StringComparer.Ordinal);

        stagingRoots.AddRange(indexedRoots.Where(seen.Add));

        List<BackupRestoreRecoveryReport> reports = [];

        foreach (string stagingRoot in stagingRoots)
        {

            BackupRestoreJournalRecord? journal = BackupRestoreJournal.TryRead(stagingRoot);

            if (journal is null)
            {

                continue;

            }

            reports.Add(ResolveOne(stagingRoot, journal, liveRoot));

        }

        if (indexedRoots.Count > 0)
        {

            // Rewriting the whole set rather than removing entry by entry is what prunes an index
            // still naming staging that was never created, or that some other sweep already took.
            PruneIndex(grimoireDirectory, indexedRoots);

        }

        return reports;

    }

    private static void PruneIndex(string grimoireDirectory, List<string> indexedRoots)
    {

        try
        {

            BackupRestoreStagingIndex.Write(
                grimoireDirectory,
                [.. indexedRoots.Where(BackupRestoreJournal.Exists)]);

        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {

        }

    }

    /// <summary>
    /// A staging root named by the index, or <see langword="null"/> when the entry is unusable or is
    /// not one of ours. The canonical-name check is the same guard <see cref="BackupRestoreJournal"/>
    /// applies when sweeping a directory: a hand-written entry must never make recovery delete an
    /// arbitrary path.
    /// </summary>
    private static string? TryNormalize(string stagingRoot)
    {

        try
        {

            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagingRoot));

            return BackupRestoreJournal.IsCanonicalStagingName(Path.GetFileName(full))
                ? full
                : null;

        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {

            return null;

        }

    }

    private static BackupRestoreRecoveryReport ResolveOne(
        string stagingRoot,
        BackupRestoreJournalRecord journal,
        string liveRoot)
    {

        if (!string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(journal.LiveRoot)),
                liveRoot,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {

            return new BackupRestoreRecoveryReport(
                stagingRoot,
                BackupRestoreRecoveryOutcome.ReconciliationRequired,
                journal.Phase,
                "The journal describes a different installation root and was left untouched.");

        }

        bool stagedExists = Directory.Exists(journal.StagedRoot);

        bool displacedExists = Directory.Exists(journal.DisplacedRoot);

        bool liveExists = Directory.Exists(journal.LiveRoot);

        if (journal.Phase < BackupRestorePhase.Commit)
        {

            Discard(stagingRoot, journal);

            return new BackupRestoreRecoveryReport(
                stagingRoot,
                BackupRestoreRecoveryOutcome.Discarded,
                journal.Phase,
                "The restore was interrupted before any destructive step; staging was removed and the "
                + "installation was never modified.");

        }

        if (journal.Phase > BackupRestorePhase.Commit)
        {

            Discard(stagingRoot, journal);

            return new BackupRestoreRecoveryReport(
                stagingRoot,
                BackupRestoreRecoveryOutcome.CommitCompleted,
                journal.Phase,
                "The restore had already committed; only staging cleanup remained.");

        }

        if (stagedExists && liveExists && !displacedExists)
        {

            Discard(stagingRoot, journal);

            return new BackupRestoreRecoveryReport(
                stagingRoot,
                BackupRestoreRecoveryOutcome.RolledBack,
                journal.Phase,
                "The commit had not begun; the prior installation was already in place.");

        }

        if (!liveExists && displacedExists)
        {

            try
            {

                Directory.Move(journal.DisplacedRoot, journal.LiveRoot);

                Discard(stagingRoot, journal);

                return new BackupRestoreRecoveryReport(
                    stagingRoot,
                    BackupRestoreRecoveryOutcome.RolledBack,
                    journal.Phase,
                    "The commit was interrupted between renames; the prior installation was restored.");

            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {

                return new BackupRestoreRecoveryReport(
                    stagingRoot,
                    BackupRestoreRecoveryOutcome.ReconciliationRequired,
                    journal.Phase,
                    "The prior installation could not be moved back into place. It is preserved at "
                    + journal.DisplacedRoot);

            }

        }

        if (!stagedExists && liveExists && displacedExists)
        {

            return new BackupRestoreRecoveryReport(
                stagingRoot,
                BackupRestoreRecoveryOutcome.ReconciliationRequired,
                journal.Phase,
                "The restored generation is committed but its local secret protection may not have "
                + "been rebuilt. Re-run the restore, or verify the Grimoire opens. The prior "
                + "installation is preserved at " + journal.DisplacedRoot);

        }

        Discard(stagingRoot, journal);

        return new BackupRestoreRecoveryReport(
            stagingRoot,
            BackupRestoreRecoveryOutcome.CommitCompleted,
            journal.Phase,
            "The commit had completed; only staging cleanup remained.");

    }

    private static void Discard(string stagingRoot, BackupRestoreJournalRecord journal)
    {

        BackupRestoreJournal.Delete(stagingRoot);

        _ = OwnedTemporaryDirectory.TryDelete(
            stagingRoot,
            journal.StagingVolumeId,
            journal.StagingFileId);

    }

}
