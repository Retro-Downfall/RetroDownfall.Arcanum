using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.Security;

using SQLitePCL;

namespace RetroDownfall.Arcanum.Tests.Backup;

public sealed class BackupDatabaseSnapshotterTests : IDisposable
{

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "arcanum-backup-snapshot-" + Guid.NewGuid().ToString("N"));

    public BackupDatabaseSnapshotterTests()
    {

        Batteries_V2.Init();

        Directory.CreateDirectory(_root);

    }

    public void Dispose()
    {

        BackupDatabaseSnapshotter.AfterSourceIdentityCapturedForTests = null;

        BackupDatabaseSnapshotter.AfterTemporaryCreatedForTests = null;

        SqliteConnection.ClearAllPools();

        if (Directory.Exists(_root))
        {

            Directory.Delete(_root, recursive: true);

        }

    }

    [Fact]
    public async Task Online_snapshot_captures_a_consistent_committed_WAL_generation()
    {

        string sourcePath = Path.Combine(_root, "live.db");

        string snapshotPath = Path.Combine(_root, "snapshot.db");

        string connectionString = ConnectionString(sourcePath);

        await using SqliteConnection setup = new(connectionString);

        await setup.OpenAsync();

        await ExecuteAsync(setup, "PRAGMA journal_mode=WAL;");

        await ExecuteAsync(setup, "CREATE TABLE Items (Id INTEGER PRIMARY KEY, Value TEXT NOT NULL);");

        await ExecuteAsync(
            setup,
            "CREATE TABLE LongRunningOperations (Id TEXT PRIMARY KEY, Kind TEXT NOT NULL, State INTEGER NOT NULL);");

        await ExecuteAsync(setup, "INSERT INTO Items(Value) VALUES ('committed');");

        await ExecuteAsync(
            setup,
            "INSERT INTO LongRunningOperations VALUES ('active-inference', 'inference-run', 1);");

        await using SqliteConnection writer = new(connectionString);

        await writer.OpenAsync();

        await using SqliteTransaction transaction =
            (SqliteTransaction)await writer.BeginTransactionAsync();

        await using (SqliteCommand insert = writer.CreateCommand())
        {

            insert.Transaction = transaction;

            insert.CommandText = "INSERT INTO Items(Value) VALUES ('not-yet-committed');";

            _ = await insert.ExecuteNonQueryAsync();

        }

        BackupDatabaseSnapshotter snapshotter = new();

        await snapshotter.CreateAsync(
            sourcePath,
            snapshotPath,
            databasePassphrase: string.Empty,
            CancellationToken.None);

        await transaction.CommitAsync();

        Assert.True(File.Exists(snapshotPath));

        if (!OperatingSystem.IsWindows())
        {

            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(snapshotPath));

        }

        await using SqliteConnection snapshot = new(ConnectionString(snapshotPath));

        await snapshot.OpenAsync();

        await using SqliteCommand count = snapshot.CreateCommand();

        count.CommandText = "SELECT COUNT(*) FROM Items;";

        Assert.Equal(1L, await count.ExecuteScalarAsync());

        await using SqliteCommand activeInference = snapshot.CreateCommand();

        activeInference.CommandText = """
            SELECT COUNT(*)
            FROM LongRunningOperations
            WHERE Kind = 'inference-run' AND State = 1;
            """;

        Assert.Equal(1L, await activeInference.ExecuteScalarAsync());

        await using SqliteCommand integrity = snapshot.CreateCommand();

        integrity.CommandText = "PRAGMA quick_check;";

        Assert.Equal("ok", await integrity.ExecuteScalarAsync());

    }

    [Fact]
    public async Task Cancelled_snapshot_leaves_no_destination_or_plaintext_staging()
    {

        string sourcePath = Path.Combine(_root, "cancel-source.db");

        string snapshotPath = Path.Combine(_root, "cancel-snapshot.db");

        await using (SqliteConnection setup = new(ConnectionString(sourcePath)))
        {

            await setup.OpenAsync();

            await ExecuteAsync(setup, "CREATE TABLE Items (Id INTEGER PRIMARY KEY);");

        }

        using CancellationTokenSource cancellation = new();

        cancellation.Cancel();

        BackupDatabaseSnapshotter snapshotter = new();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => snapshotter.CreateAsync(
                sourcePath,
                snapshotPath,
                databasePassphrase: string.Empty,
                cancellation.Token));

        Assert.False(File.Exists(snapshotPath));

    }

    [Fact]
    public async Task Cancellation_after_online_copy_begins_stops_before_publication_and_cleans_staging()
    {

        string sourcePath = Path.Combine(_root, "mid-copy-cancel-source.db");

        string snapshotPath = Path.Combine(_root, "mid-copy-cancel-snapshot.db");

        await using (SqliteConnection setup = new(ConnectionString(sourcePath)))
        {

            await setup.OpenAsync();

            await ExecuteAsync(
                setup,
                "CREATE TABLE Items (Id INTEGER PRIMARY KEY, Value BLOB NOT NULL);");

            await ExecuteAsync(
                setup,
                "INSERT INTO Items(Value) VALUES (zeroblob(8 * 1024 * 1024));");

        }

        using CancellationTokenSource cancellation = new();

        string? temporaryPath = null;

        int completedSteps = 0;

        int remainingPagesAfterFirstStep = 0;

        BackupDatabaseSnapshotter.AfterTemporaryCreatedForTests = path =>
            temporaryPath = path;

        BackupDatabaseSnapshotter snapshotter = new()
        {

            AfterBackupStepForTests = (remainingPages, totalPages) =>
            {

                completedSteps++;

                Assert.True(totalPages > 0);

                remainingPagesAfterFirstStep = remainingPages;

                cancellation.Cancel();

            },

        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => snapshotter.CreateAsync(
                sourcePath,
                snapshotPath,
                databasePassphrase: string.Empty,
                cancellation.Token));

        Assert.Equal(1, completedSteps);

        Assert.True(remainingPagesAfterFirstStep > 0);

        Assert.NotNull(temporaryPath);

        Assert.False(File.Exists(temporaryPath));

        Assert.False(File.Exists(snapshotPath));

        Assert.Empty(Directory.GetFiles(_root, ".*.tmp.*"));

    }

    [Fact]
    public async Task Source_path_replacement_is_rejected_without_publishing_a_snapshot()
    {

        if (OperatingSystem.IsWindows())
        {

            return;

        }

        string sourcePath = Path.Combine(_root, "source-authority.db");

        string replacementPath = Path.Combine(_root, "replacement.db");

        string movedPath = Path.Combine(_root, "moved-source.db");

        string snapshotPath = Path.Combine(_root, "rejected-snapshot.db");

        await using (SqliteConnection source = new(ConnectionString(sourcePath)))
        {

            await source.OpenAsync();

            await ExecuteAsync(source, "CREATE TABLE Expected (Id INTEGER PRIMARY KEY);");

        }

        await using (SqliteConnection replacement = new(ConnectionString(replacementPath)))
        {

            await replacement.OpenAsync();

            await ExecuteAsync(replacement, "CREATE TABLE Unrelated (Id INTEGER PRIMARY KEY);");

        }

        BackupDatabaseSnapshotter.AfterSourceIdentityCapturedForTests = path =>
        {

            BackupDatabaseSnapshotter.AfterSourceIdentityCapturedForTests = null;

            File.Move(path, movedPath);

            File.CreateSymbolicLink(path, replacementPath);

        };

        BackupDatabaseSnapshotter snapshotter = new();

        await Assert.ThrowsAsync<IOException>(
            () => snapshotter.CreateAsync(
                sourcePath,
                snapshotPath,
                databasePassphrase: string.Empty,
                CancellationToken.None));

        Assert.False(File.Exists(snapshotPath));

    }

    [Fact]
    public async Task Temporary_path_replacement_is_preserved_when_snapshot_creation_fails()
    {

        if (OperatingSystem.IsWindows())
        {

            return;

        }

        string sourcePath = Path.Combine(_root, "temporary-source.db");

        string snapshotPath = Path.Combine(_root, "temporary-snapshot.db");

        await using (SqliteConnection source = new(ConnectionString(sourcePath)))
        {

            await source.OpenAsync();

            await ExecuteAsync(source, "CREATE TABLE Expected (Id INTEGER PRIMARY KEY);");

        }

        string? replacementPath = null;

        BackupDatabaseSnapshotter.AfterTemporaryCreatedForTests = path =>
        {

            BackupDatabaseSnapshotter.AfterTemporaryCreatedForTests = null;

            File.Move(path, path + ".owned");

            File.WriteAllText(path, "replacement-must-survive");

            replacementPath = path;

        };

        BackupDatabaseSnapshotter snapshotter = new();

        await Assert.ThrowsAsync<IOException>(
            () => snapshotter.CreateAsync(
                sourcePath,
                snapshotPath,
                databasePassphrase: string.Empty,
                CancellationToken.None));

        Assert.NotNull(replacementPath);

        Assert.Equal(
            "replacement-must-survive",
            File.ReadAllText(replacementPath));

        Assert.False(File.Exists(snapshotPath));

    }

    private static string ConnectionString(string path) =>
        new SqliteConnectionStringBuilder
        {

            DataSource = path,

            Pooling = false,

        }.ToString();

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync();

    }

}
