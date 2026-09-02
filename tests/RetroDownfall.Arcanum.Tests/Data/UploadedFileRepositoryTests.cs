using Microsoft.EntityFrameworkCore;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data;

[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class UploadedFileRepositoryTests : IAsyncLifetime
{

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private string _filesRoot = string.Empty;

    private ArcanumDbContext? _db;

    private UploadedFileRepository? _repo;

    public UploadedFileRepositoryTests(GrimoireFixture fixture)
    {

        _fixture = fixture;

    }

    public Task InitializeAsync()
    {

        _dbPath = _fixture.CopyDatabase();

        _filesRoot = Path.Combine(
            Path.GetTempPath(),
            "arcanum-uploaded-file-delete-tests-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(_filesRoot);

        _db = _fixture.CreateContext(_dbPath);

        _repo = new UploadedFileRepository(_db, _filesRoot);

        return Task.CompletedTask;

    }

    public async Task DisposeAsync()
    {

        FileHandleIdentityInterop.TryGetPathMetadataNoFollowForTests = null;

        if (_db is not null)
        {

            await _db.DisposeAsync();

        }

        if (File.Exists(_dbPath))
        {

            File.Delete(_dbPath);

        }

        if (Directory.Exists(_filesRoot))
        {

            Directory.Delete(_filesRoot, recursive: true);

        }

    }

    [SkippableFact]
    public async Task CreateAsync_then_GetByIdAsync_round_trips()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid id = Guid.NewGuid();

        DateTimeOffset createdAt = DateTimeOffset.UtcNow;

        UploadedFileRecord record = new(id, "batch-input.jsonl", 1024, "batch", "application/jsonl", createdAt);

        await _repo!.CreateAsync(record, CancellationToken.None);

        UploadedFileRecord? loaded = await _repo.GetByIdAsync(id, CancellationToken.None);

        Assert.NotNull(loaded);

        Assert.Equal(id, loaded!.Id);

        Assert.Equal("batch-input.jsonl", loaded.Filename);

        Assert.Equal(1024, loaded.Bytes);

        Assert.Equal("batch", loaded.Purpose);

        Assert.Equal("application/jsonl", loaded.MimeType);

    }

    [SkippableFact]

    public async Task CreateForOwnedFileAsync_WhenDeleteWriterWins_RejectsWithoutMetadataAndPreservesReplacement()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid id = Guid.NewGuid();

        string path = Path.Combine(_filesRoot, id.ToString("N"));

        await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);

        UploadedFileRecord record = new(
            id,
            "writer-race.bin",
            4,
            "assistants",
            "application/octet-stream",
            DateTimeOffset.UtcNow);

        await using ArcanumDbContext writerDb = _fixture.CreateContext(_dbPath);

        SqliteConnection writerConnection = Assert.IsType<SqliteConnection>(
            writerDb.Database.GetDbConnection());

        await using SqliteTransaction writerTransaction =
            writerConnection.BeginTransaction(deferred: false);

        TaskCompletionSource captured = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        FileHandleIdentityInterop.TryGetPathMetadataNoFollowForTests = candidate =>
        {

            FileHandleMetadata? metadata = ReadActualMetadata(candidate);

            if (string.Equals(candidate, path, StringComparison.Ordinal))
            {

                captured.TrySetResult();

            }

            return metadata;

        };

        Task publish = Task.Run(
            () => _repo!.CreateForOwnedFileAsync(record, CancellationToken.None));

        await captured.Task.WaitAsync(TimeSpan.FromSeconds(30));

        File.Delete(path);

        byte[] replacement = [9, 8, 7];

        await File.WriteAllBytesAsync(path, replacement);

        await writerTransaction.CommitAsync(CancellationToken.None);

        _ = await Assert.ThrowsAnyAsync<IOException>(() => publish);

        Assert.Null(await _repo!.GetByIdAsync(id, CancellationToken.None));

        Assert.Equal(replacement, await File.ReadAllBytesAsync(path));

    }

    [SkippableFact]

    public async Task CreateForOwnedFileAsync_WhenCreateWins_DeleteSeesMetadataAndOwnedBytes()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid id = Guid.NewGuid();

        string path = Path.Combine(_filesRoot, id.ToString("N"));

        byte[] bytes = [5, 6, 7, 8];

        await File.WriteAllBytesAsync(path, bytes);

        UploadedFileRecord record = new(
            id,
            "published.bin",
            bytes.Length,
            "assistants",
            "application/octet-stream",
            DateTimeOffset.UtcNow);

        await _repo!.CreateForOwnedFileAsync(record, CancellationToken.None);

        Assert.NotNull(await _repo!.GetByIdAsync(id, CancellationToken.None));

        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));

        Assert.Equal(
            UploadedFileDeleteStatus.Deleted,
            await _repo.TryDeleteUnreferencedAsync(id, CancellationToken.None));

        Assert.Null(await _repo.GetByIdAsync(id, CancellationToken.None));

        Assert.False(File.Exists(path));

    }

    [SkippableFact]
    public async Task GetByIdAsync_returns_null_for_missing_id()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        UploadedFileRecord? loaded = await _repo!.GetByIdAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Null(loaded);

    }

    [SkippableFact]
    public async Task ListAsync_filters_by_purpose_when_provided()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        await _repo!.CreateAsync(new UploadedFileRecord(Guid.NewGuid(), "a.jsonl", 10, "batch", "application/jsonl", now), CancellationToken.None);

        await _repo.CreateAsync(new UploadedFileRecord(Guid.NewGuid(), "b.png", 20, "vision", "image/png", now), CancellationToken.None);

        IReadOnlyList<UploadedFileRecord> batchOnly = await _repo.ListAsync("batch", CancellationToken.None);

        Assert.Single(batchOnly);

        Assert.Equal("a.jsonl", batchOnly[0].Filename);

        IReadOnlyList<UploadedFileRecord> all = await _repo.ListAsync(null, CancellationToken.None);

        Assert.Equal(2, all.Count);

    }

    [SkippableFact]
    public async Task DeleteAsync_removes_row()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid id = Guid.NewGuid();

        await _repo!.CreateAsync(new UploadedFileRecord(id, "c.txt", 5, "assistants", "text/plain", DateTimeOffset.UtcNow), CancellationToken.None);

        await _repo.DeleteAsync(id, CancellationToken.None);

        UploadedFileRecord? loaded = await _repo.GetByIdAsync(id, CancellationToken.None);

        Assert.Null(loaded);

    }

    [SkippableTheory]
    [InlineData(BatchStatuses.InProgress)]
    [InlineData(BatchStatuses.Completed)]
    public async Task TryDeleteUnreferencedAsync_blocks_all_batch_roles_for_active_and_terminal_batches(
        string status)
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid inputFileId = Guid.NewGuid();

        Guid outputFileId = Guid.NewGuid();

        Guid errorFileId = Guid.NewGuid();

        DateTimeOffset now = DateTimeOffset.UtcNow;

        await _repo!.CreateAsync(
            new UploadedFileRecord(inputFileId, "input.jsonl", 5, "batch", "application/jsonl", now),
            CancellationToken.None);

        await _repo.CreateAsync(
            new UploadedFileRecord(outputFileId, "output.jsonl", 5, "batch_output", "application/jsonl", now),
            CancellationToken.None);

        await _repo.CreateAsync(
            new UploadedFileRecord(errorFileId, "error.jsonl", 5, "error", "application/jsonl", now),
            CancellationToken.None);

        BatchRepository batches = new(_db!);

        await batches.CreateAsync(
            new BatchRecord(
                Guid.NewGuid(),
                inputFileId,
                "/v1/chat/completions",
                status,
                now,
                BatchStatuses.IsTerminal(status) ? now : null,
                outputFileId,
                errorFileId),
            CancellationToken.None);

        Assert.Equal(
            UploadedFileDeleteStatus.ReferencedByBatch,
            await _repo.TryDeleteUnreferencedAsync(inputFileId, CancellationToken.None));

        Assert.Equal(
            UploadedFileDeleteStatus.ReferencedByBatch,
            await _repo.TryDeleteUnreferencedAsync(outputFileId, CancellationToken.None));

        Assert.Equal(
            UploadedFileDeleteStatus.ReferencedByBatch,
            await _repo.TryDeleteUnreferencedAsync(errorFileId, CancellationToken.None));

        Assert.NotNull(await _repo.GetByIdAsync(inputFileId, CancellationToken.None));

        Assert.NotNull(await _repo.GetByIdAsync(outputFileId, CancellationToken.None));

        Assert.NotNull(await _repo.GetByIdAsync(errorFileId, CancellationToken.None));

    }

    [SkippableFact]
    public async Task TryDeleteUnreferencedAsync_is_conditional_and_reports_missing_rows()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid id = Guid.NewGuid();

        await _repo!.CreateAsync(
            new UploadedFileRecord(id, "unreferenced.txt", 5, "assistants", "text/plain", DateTimeOffset.UtcNow),
            CancellationToken.None);

        Assert.Equal(
            UploadedFileDeleteStatus.Deleted,
            await _repo.TryDeleteUnreferencedAsync(id, CancellationToken.None));

        Assert.Equal(
            UploadedFileDeleteStatus.NotFound,
            await _repo.TryDeleteUnreferencedAsync(id, CancellationToken.None));

    }

    [SkippableFact]

    public async Task TryDeleteUnreferencedAsync_WhenMetadataDeleteFails_RestoresOwnedBytesAndMetadata()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid id = Guid.NewGuid();

        string path = Path.Combine(_filesRoot, id.ToString("N"));

        byte[] bytes = [1, 2, 3, 4];

        await File.WriteAllBytesAsync(path, bytes);

        await _repo!.CreateAsync(
            new UploadedFileRecord(
                id,
                "db-failure.bin",
                bytes.Length,
                "assistants",
                "application/octet-stream",
                DateTimeOffset.UtcNow),
            CancellationToken.None);

        await ExecuteSqlAsync(
            $"""
            CREATE TRIGGER reject_uploaded_file_delete
            BEFORE DELETE ON "UploadedFiles"
            WHEN lower(replace(OLD."Id", '-', '')) = '{id:N}'
            BEGIN
                SELECT RAISE(ABORT, 'injected uploaded-file delete failure');
            END;
            """);

        _ = await Assert.ThrowsAsync<SqliteException>(
            () => _repo.TryDeleteUnreferencedAsync(id, CancellationToken.None));

        Assert.NotNull(await _repo.GetByIdAsync(id, CancellationToken.None));

        Assert.True(File.Exists(path));

        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));

        Assert.Empty(
            Directory.GetFileSystemEntries(
                _filesRoot,
                $".arcanum-file-delete-{id:N}-*"));

    }

    [SkippableFact]

    public async Task TryDeleteUnreferencedAsync_WhenByteFinalizationFails_RequiresRecoveryAndKeepsRecognizableQuarantine()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid id = Guid.NewGuid();

        string path = Path.Combine(_filesRoot, id.ToString("N"));

        byte[] bytes = [5, 6, 7, 8];

        await File.WriteAllBytesAsync(path, bytes);

        await _repo!.CreateAsync(
            new UploadedFileRecord(
                id,
                "cleanup-failure.bin",
                bytes.Length,
                "assistants",
                "application/octet-stream",
                DateTimeOffset.UtcNow),
            CancellationToken.None);

        int quarantinedFileReads = 0;

        FileHandleIdentityInterop.TryGetPathMetadataNoFollowForTests = candidate =>
        {

            bool quarantinedFile = File.Exists(candidate)
                && candidate.Contains(
                    $".arcanum-file-delete-{id:N}-",
                    StringComparison.Ordinal);

            if (!quarantinedFile)
            {

                return ReadActualMetadata(candidate);

            }

            quarantinedFileReads++;

            return quarantinedFileReads == 1
                ? ReadActualMetadata(candidate)
                : null;

        };

        UploadedFileDeleteStatus status = await _repo.TryDeleteUnreferencedAsync(
            id,
            CancellationToken.None);

        Assert.Equal(UploadedFileDeleteStatus.RecoveryRequired, status);

        Assert.Null(await _repo.GetByIdAsync(id, CancellationToken.None));

        Assert.False(File.Exists(path));

        string quarantineDirectory = Assert.Single(
            Directory.GetDirectories(
                _filesRoot,
                $".arcanum-file-delete-{id:N}-*"));

        string quarantinedPath = Assert.Single(
            Directory.GetFiles(quarantineDirectory));

        Assert.Equal(id.ToString("N"), Path.GetFileName(quarantinedPath));

        Assert.Equal(bytes, await File.ReadAllBytesAsync(quarantinedPath));

        FileHandleIdentityInterop.TryGetPathMetadataNoFollowForTests = null;

        Assert.Equal(
            UploadedFileDeleteStatus.RecoveryRequired,
            await _repo.TryDeleteUnreferencedAsync(id, CancellationToken.None));

        Assert.True(File.Exists(quarantinedPath));

    }

    [SkippableFact]

    public async Task TryDeleteUnreferencedAsync_WhenOwnedBytesCannotBeQuarantined_PreservesMetadata()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid id = Guid.NewGuid();

        string path = Path.Combine(_filesRoot, id.ToString("N"));

        await File.WriteAllBytesAsync(path, [9, 10]);

        await _repo!.CreateAsync(
            new UploadedFileRecord(
                id,
                "identity-conflict.bin",
                2,
                "assistants",
                "application/octet-stream",
                DateTimeOffset.UtcNow),
            CancellationToken.None);

        FileHandleMetadata original = Assert.IsType<FileHandleMetadata>(
            ReadActualMetadata(path));

        int originalReads = 0;

        FileHandleIdentityInterop.TryGetPathMetadataNoFollowForTests = candidate =>
        {

            if (!string.Equals(candidate, path, StringComparison.Ordinal))
            {

                return ReadActualMetadata(candidate);

            }

            originalReads++;

            return originalReads == 1
                ? original
                : original with
                {
                    Identity = new FileHandleIdentity(
                        original.Identity.VolumeId,
                        original.Identity.FileId + 1),
                };

        };

        UploadedFileDeleteStatus status = await _repo.TryDeleteUnreferencedAsync(
            id,
            CancellationToken.None);

        Assert.Equal(UploadedFileDeleteStatus.StorageConflict, status);

        Assert.NotNull(await _repo.GetByIdAsync(id, CancellationToken.None));

        Assert.True(File.Exists(path));

    }

    [SkippableFact]
    public void ResolvePath_is_deterministic_and_disk_independent()
    {

        Guid id = Guid.NewGuid();

        string path1 = UploadedFileStorage.ResolvePath(id);

        string path2 = UploadedFileStorage.ResolvePath(id);

        Assert.Equal(path1, path2);

        Assert.EndsWith(id.ToString("N"), path1, StringComparison.Ordinal);

    }

    [SkippableFact]
    public async Task Migration_creates_UploadedFiles_table()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        System.Data.Common.DbConnection connection = _db!.Database.GetDbConnection();

        await using System.Data.Common.DbCommand cmd = connection.CreateCommand();

        if (cmd.Connection!.State != System.Data.ConnectionState.Open)
        {

            await cmd.Connection.OpenAsync(CancellationToken.None);

        }

        cmd.CommandText = """
            SELECT 1
            FROM sqlite_master
            WHERE type = 'table' AND name = 'UploadedFiles'
            LIMIT 1;
            """;

        object? result = await cmd.ExecuteScalarAsync(CancellationToken.None);

        Assert.NotNull(result);

    }

    private async Task ExecuteSqlAsync(string sql)
    {

        System.Data.Common.DbConnection connection = _db!.Database.GetDbConnection();

        if (connection.State != System.Data.ConnectionState.Open)
        {

            await connection.OpenAsync(CancellationToken.None);

        }

        await using System.Data.Common.DbCommand command = connection.CreateCommand();

        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);

    }

    /// <summary>
    /// Answers honestly for paths this test is not faking.
    ///
    /// This used to null the process-global seam, call back in, and restore it. That write ran on
    /// whichever thread asked — including concurrently running tests — so this test could observe
    /// its own seam missing mid-flight, and two interleaved calls could clear it permanently. Going
    /// straight to the platform lookup keeps the global untouched.
    /// </summary>
    private static FileHandleMetadata? ReadActualMetadata(string path) =>
        FileHandleIdentityInterop.TryGetPathMetadataNoFollowIgnoringTestSeam(
            path,
            out FileHandleMetadata metadata)
            ? metadata
            : null;

}
