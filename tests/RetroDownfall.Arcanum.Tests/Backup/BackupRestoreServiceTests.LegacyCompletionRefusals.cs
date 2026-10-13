using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Backup;

namespace RetroDownfall.Arcanum.Tests.Backup;

// These additive post-repair controls use the same real interrupted SQLCipher restore fixture.
// The separate LegacyCompletion theory compiles against the baseline for behavioral RED.
public sealed partial class BackupRestoreServiceTests
{
    [SkippableTheory]
    [InlineData("closed-connection")]
    [InlineData("wrong-database")]
    [InlineData("changed-journal")]
    [InlineData("changed-database")]
    [InlineData("cancelled")]
    [InlineData("missing-table")]
    [InlineData("disposed-lock")]
    [InlineData("wrong-lock")]
    public async Task Legacy_finalization_refuses_unproved_currentness_and_preserves_recovery_evidence(string shape)
    {
        Skip.If(OperatingSystem.IsWindows() && shape == "changed-database",
            "The pinned Windows SQLCipher VFS does not share an open database for deletion or rename.");

        (string staging, BackupRestoreJournalRecord journal, RecordingSecretStore secrets) =
            await InterruptCommittedLegacyForPhaseTestsAsync();

        var reports = await BackupRestoreRecovery.ResolveAsync(_installation, secrets, CancellationToken.None);

        Assert.Equal(BackupRestoreRecoveryOutcome.CommittedDatabaseVerificationRequired, Assert.Single(reports).Outcome);

        string path = Path.Combine(_installation, "arcanum.db");

        if (shape == "wrong-database")
        {
            string other = Path.Combine(_installation, "other.db");

            File.Copy(path, other);

            File.Copy(path + ".kdf", other + ".kdf");

            path = other;
        }

        await using SqliteConnection connection = await BackupRestoreDatabaseWorker.OpenAsync(path,
            secrets.GrimoireSecret!, readOnly: shape != "missing-table", CancellationToken.None);

        if (shape == "closed-connection") await connection.CloseAsync();

        if (shape == "changed-journal")
            BackupRestoreJournal.Write(staging, journal with { ArchivePath = Path.Combine(_archives, "different.arcbackup") });

        if (shape == "changed-database")
        {
            string old = path + ".before";

            File.Move(path, old);

            File.Copy(old, path);
        }

        if (shape == "missing-table")
        {
            await using SqliteCommand damage = connection.CreateCommand();

            damage.CommandText = "DROP TABLE long_rest_accepted_summaries;";

            _ = await damage.ExecuteNonQueryAsync();
        }

        byte[] journalBefore = await File.ReadAllBytesAsync(Path.Combine(staging, BackupRestoreJournal.FileName));

        using ArcanumMaintenanceLock held = ArcanumMaintenanceLock.TryAcquire(_installation)
            ?? throw new InvalidOperationException("fixture lock unavailable");

        using CancellationTokenSource cancellation = new();

        if (shape == "cancelled") cancellation.Cancel();

        if (shape == "wrong-lock")
        {
            using ArcanumMaintenanceLock other = ArcanumMaintenanceLock.TryAcquire(_archives)
                ?? throw new InvalidOperationException("other fixture lock unavailable");

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                BackupRestoreRecovery.CompleteLegacyCommittedBeforeReadinessAsync(other, _installation, reports,
                    connection, CancellationToken.None));
        }
        else if (shape == "disposed-lock")
        {
            held.Dispose();

            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                BackupRestoreRecovery.CompleteLegacyCommittedBeforeReadinessAsync(held, _installation, reports,
                    connection, CancellationToken.None));
        }
        else if (shape == "cancelled")
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                BackupRestoreRecovery.CompleteLegacyCommittedBeforeReadinessAsync(held, _installation, reports,
                    connection, cancellation.Token));
        }
        else
        {
            Result completed = await BackupRestoreRecovery.CompleteLegacyCommittedBeforeReadinessAsync(
                held, _installation, reports, connection, CancellationToken.None);

            Assert.True(completed.IsFailure);
        }

        Assert.True(Directory.Exists(staging));

        Assert.True(Directory.Exists(journal.DisplacedRoot));

        Assert.Equal(journalBefore, await File.ReadAllBytesAsync(Path.Combine(staging, BackupRestoreJournal.FileName)));

        Assert.Equal(shape == "closed-connection" ? System.Data.ConnectionState.Closed : System.Data.ConnectionState.Open,
            connection.State);
    }
}
