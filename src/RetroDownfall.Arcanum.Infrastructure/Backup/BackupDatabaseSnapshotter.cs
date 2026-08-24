using Microsoft.Data.Sqlite;
using SQLitePCL;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

public sealed class BackupDatabaseSnapshotter
{

    private const int PagesPerBackupStep = 64;

    // Instance members, matching AfterBackupStepForTests below. As settable statics these were
    // shared by every snapshotter in the process, so a test's destructive hook fired on whichever
    // caller reached the invoke point first — moving and symlinking another concurrently running
    // test's live database or temp snapshot rather than its own.
    internal Action<string>? AfterSourceIdentityCapturedForTests { get; init; }

    internal Action<string>? AfterTemporaryCreatedForTests { get; init; }

    internal Action<int, int>? AfterBackupStepForTests { get; init; }

    public async Task CreateAsync(
        string sourcePath,
        string destinationPath,
        string databasePassphrase,
        CancellationToken cancellationToken)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        ArgumentNullException.ThrowIfNull(databasePassphrase);

        cancellationToken.ThrowIfCancellationRequested();

        SqliteNativeRuntime.Instance.Initialize();

        string fullSourcePath = Path.GetFullPath(sourcePath);

        string fullDestinationPath = Path.GetFullPath(destinationPath);

        if (!IdentityOwnedFileSystemCleanup.TryCapturePath(
                fullSourcePath,
                FileSystemObjectKind.RegularFile,
                out IdentityOwnedFileSystemArtifact sourceAuthority))
        {

            throw new IOException("The Grimoire database is missing or is not a regular file.");

        }

        AfterSourceIdentityCapturedForTests?.Invoke(fullSourcePath);

        if (File.Exists(fullDestinationPath) || Directory.Exists(fullDestinationPath))
        {

            throw new IOException("The database snapshot destination already exists.");

        }

        string directory = Path.GetDirectoryName(fullDestinationPath)
            ?? throw new InvalidOperationException("The snapshot destination has no parent directory.");

        SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(directory);

        string tempPath = Path.Combine(
            directory,
            "." + Path.GetFileName(fullDestinationPath) + ".tmp." + Guid.NewGuid().ToString("N"));

        OwnedTemporaryFile? temporarySnapshot = null;

        try
        {

            temporarySnapshot = OwnedTemporaryFile.Create(
                tempPath,
                out FileStream createdStream);

            await using (FileStream created = createdStream)
            {

                await created.FlushAsync(cancellationToken).ConfigureAwait(false);

            }

            AfterTemporaryCreatedForTests?.Invoke(tempPath);

            if (!temporarySnapshot.MatchesPathIdentity())
            {

                throw new IOException(
                    "The protected database snapshot path changed during creation.");

            }

            string sourceConnectionString = BuildConnectionString(
                fullSourcePath,
                databasePassphrase,
                SqliteOpenMode.ReadOnly);

            string destinationConnectionString = BuildConnectionString(
                tempPath,
                databasePassphrase,
                SqliteOpenMode.ReadWrite);

            await using (SqliteConnection source = new(sourceConnectionString))
            await using (SqliteConnection destination = new(destinationConnectionString))
            {

                await source.OpenAsync(cancellationToken).ConfigureAwait(false);

                await destination.OpenAsync(cancellationToken).ConfigureAwait(false);

                await CopyOnlineSnapshotAsync(
                    source,
                    destination,
                    cancellationToken).ConfigureAwait(false);

                await destination.CloseAsync().ConfigureAwait(false);

                await source.CloseAsync().ConfigureAwait(false);

            }

            if (!IdentityOwnedFileSystemCleanup.TryCapturePath(
                    fullSourcePath,
                    FileSystemObjectKind.RegularFile,
                    out IdentityOwnedFileSystemArtifact currentSource)
                || currentSource.Metadata != sourceAuthority.Metadata)
            {

                throw new IOException(
                    "The Grimoire database path identity changed during snapshot capture.");

            }

            SecureFilePermissions.ApplyOwnerOnlyFile(tempPath);

            await VerifySnapshotAsync(
                tempPath,
                databasePassphrase,
                cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            if (!temporarySnapshot.MatchesPathIdentity())
            {

                throw new IOException(
                    "The protected database snapshot path changed before publication.");

            }

            File.Move(tempPath, fullDestinationPath, overwrite: false);

            SecureFilePermissions.ApplyOwnerOnlyFile(fullDestinationPath);

        }
        finally
        {

            _ = temporarySnapshot?.TryDelete();

        }

    }

    private async Task CopyOnlineSnapshotAsync(
        SqliteConnection source,
        SqliteConnection destination,
        CancellationToken cancellationToken)
    {

        sqlite3 sourceHandle = source.Handle
            ?? throw new InvalidOperationException(
                "The source database connection is not open.");

        sqlite3 destinationHandle = destination.Handle
            ?? throw new InvalidOperationException(
                "The destination database connection is not open.");

        using sqlite3_backup backup = raw.sqlite3_backup_init(
            destinationHandle,
            "main",
            sourceHandle,
            "main");

        if (backup.IsInvalid)
        {

            int initializationResult = raw.sqlite3_errcode(destinationHandle);

            SqliteException.ThrowExceptionForRC(
                initializationResult,
                destinationHandle);

        }

        while (true)
        {

            cancellationToken.ThrowIfCancellationRequested();

            int result = raw.sqlite3_backup_step(
                backup,
                PagesPerBackupStep);

            if (result is raw.SQLITE_OK or raw.SQLITE_DONE)
            {

                AfterBackupStepForTests?.Invoke(
                    raw.sqlite3_backup_remaining(backup),
                    raw.sqlite3_backup_pagecount(backup));

                cancellationToken.ThrowIfCancellationRequested();

                if (result == raw.SQLITE_DONE)
                {

                    return;

                }

                continue;

            }

            if (result is raw.SQLITE_BUSY or raw.SQLITE_LOCKED)
            {

                await Task.Delay(
                    TimeSpan.FromMilliseconds(1),
                    cancellationToken).ConfigureAwait(false);

                continue;

            }

            SqliteException.ThrowExceptionForRC(
                result,
                destinationHandle);

        }

    }

    private static async Task VerifySnapshotAsync(
        string path,
        string passphrase,
        CancellationToken cancellationToken)
    {

        await using SqliteConnection connection = new(
            BuildConnectionString(path, passphrase, SqliteOpenMode.ReadOnly));

        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using SqliteCommand quickCheck = connection.CreateCommand();

        quickCheck.CommandText = "PRAGMA quick_check;";

        object? result = await quickCheck.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        if (!string.Equals(result as string, "ok", StringComparison.OrdinalIgnoreCase))
        {

            throw new InvalidDataException("The online Grimoire snapshot failed SQLite quick_check.");

        }

        await using SqliteCommand foreignKeyCheck = connection.CreateCommand();

        foreignKeyCheck.CommandText = "PRAGMA foreign_key_check;";

        await using SqliteDataReader reader = await foreignKeyCheck
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            throw new InvalidDataException("The online Grimoire snapshot failed foreign_key_check.");

        }

    }

    private static string BuildConnectionString(
        string path,
        string passphrase,
        SqliteOpenMode mode)
    {

        SqliteConnectionStringBuilder builder = new()
        {

            DataSource = path,

            Mode = mode,

            Pooling = false,

        };

        if (!string.IsNullOrEmpty(passphrase))
        {

            builder.Password = passphrase;

        }

        return builder.ToString();

    }

}
