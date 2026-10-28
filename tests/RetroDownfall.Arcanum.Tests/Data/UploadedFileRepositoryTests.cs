using Microsoft.EntityFrameworkCore;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

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

        Assert.True(SecureFilePermissions.TryEnsureOwnerOnlyDirectoryExistsStrict(_filesRoot));

        _db = _fixture.CreateContext(_dbPath);

        _repo = new UploadedFileRepository(_db, _filesRoot);

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        FileHandleIdentityInterop.TryGetPathMetadataNoFollowForTests = null;

        SecureFilePermissions.StrictOwnerOnlyVerificationForTests = null;

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

    [SkippableTheory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Retry_recovers_a_recognizable_quarantine_after_process_memory_is_lost(bool liveRow, bool referenced)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid id = Guid.NewGuid();

        string path = Path.Combine(_filesRoot, id.ToString("N"));

        byte[] bytes = [1, 3, 5, 7];

        await File.WriteAllBytesAsync(path, bytes);

        Assert.True(SecureFilePermissions.TryApplyOwnerOnlyFileStrict(path));

        DateTimeOffset now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

        await _repo!.CreateAsync(new UploadedFileRecord(id, "recover.bin", bytes.Length, "batch", "application/octet-stream", now));

        if (referenced)
        {
            BatchRepository batches = new(_db!);

            await batches.CreateAsync(new BatchRecord(Guid.NewGuid(), id, "/v1/chat/completions", BatchStatuses.Completed, now, now, null, null));
        }

        Assert.True(IdentityOwnedFileSystemCleanup.TryCapturePath(path, FileSystemObjectKind.RegularFile, out IdentityOwnedFileSystemArtifact artifact));

        Assert.True(IdentityOwnedFileSystemCleanup.TryQuarantine(artifact, $".arcanum-file-delete-{id:N}-", out IdentityOwnedFileSystemQuarantine quarantine));

        if (!liveRow) await _repo.DeleteAsync(id);

        // Reopen the native connection/repository; recovery receives no in-memory quarantine ownership object.
        await _db!.DisposeAsync();

        _db = _fixture.CreateContext(_dbPath);

        _repo = new UploadedFileRepository(_db, _filesRoot);

        UploadedFileDeleteStatus expected = referenced
            ? UploadedFileDeleteStatus.ReferencedByBatch
            : liveRow ? UploadedFileDeleteStatus.Deleted : UploadedFileDeleteStatus.NotFound;

        Assert.Equal(expected, await _repo.TryDeleteUnreferencedAsync(id));

        Assert.False(Directory.Exists(quarantine.Directory.Path));

        Assert.Equal(referenced, File.Exists(path));

        Assert.Equal(referenced, await _repo.GetByIdAsync(id) is not null);

        if (referenced) Assert.Equal(bytes, await File.ReadAllBytesAsync(path));

        Assert.Equal(referenced ? UploadedFileDeleteStatus.ReferencedByBatch : UploadedFileDeleteStatus.NotFound,
            await _repo.TryDeleteUnreferencedAsync(id));
    }

    [SkippableTheory]
    [InlineData("destination")]
    [InlineData("extra-child")]
    [InlineData("ambiguous")]
    [InlineData("malformed-name")]
    [InlineData("directory-link")]
    [InlineData("file-link")]
    [InlineData("hardlink")]
    [InlineData("public-root")]
    [InlineData("public-directory")]
    [InlineData("public-file")]
    public async Task Recovery_preserves_bytes_when_quarantine_ownership_is_uncertain(string defect)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid id = Guid.NewGuid();

        string path = Path.Combine(_filesRoot, id.ToString("N"));

        byte[] bytes = [2, 4, 6, 8];

        await File.WriteAllBytesAsync(path, bytes);

        Assert.True(SecureFilePermissions.TryApplyOwnerOnlyFileStrict(path));

        await _repo!.CreateAsync(new UploadedFileRecord(id, "uncertain.bin", bytes.Length, "assistants", "application/octet-stream", DateTimeOffset.UtcNow));

        Assert.True(IdentityOwnedFileSystemCleanup.TryCapturePath(path, FileSystemObjectKind.RegularFile, out IdentityOwnedFileSystemArtifact artifact));

        Assert.True(IdentityOwnedFileSystemCleanup.TryQuarantine(artifact, $".arcanum-file-delete-{id:N}-", out IdentityOwnedFileSystemQuarantine quarantine));

        string bytesPath = quarantine.Quarantined.Path;

        if (defect == "destination") await File.WriteAllBytesAsync(path, [9, 9]);
        else if (defect == "extra-child") await File.WriteAllBytesAsync(Path.Combine(quarantine.Directory.Path, "unrelated"), [9, 9]);
        else if (defect == "ambiguous")
        {
            await File.WriteAllBytesAsync(path, [9, 9]);

            Assert.True(IdentityOwnedFileSystemCleanup.TryCapturePath(path, FileSystemObjectKind.RegularFile, out IdentityOwnedFileSystemArtifact second));

            Assert.True(IdentityOwnedFileSystemCleanup.TryQuarantine(second, $".arcanum-file-delete-{id:N}-", out _));
        }
        else if (defect == "malformed-name")
        {
            string malformed = Path.Combine(_filesRoot, $".arcanum-file-delete-{id:N}-not-an-operation");

            Directory.Move(quarantine.Directory.Path, malformed);

            bytesPath = Path.Combine(malformed, id.ToString("N"));
        }
        else if (defect == "directory-link")
        {
            string moved = Path.Combine(_filesRoot, "other-directory");

            Directory.Move(quarantine.Directory.Path, moved);

            Directory.CreateSymbolicLink(quarantine.Directory.Path, moved);

            bytesPath = Path.Combine(moved, id.ToString("N"));
        }
        else if (defect == "hardlink")
        {
            Assert.True(HardLinkTestSupport.TryCreate(Path.Combine(_filesRoot, "other-link"), bytesPath));
        }
        else if (defect.StartsWith("public-", StringComparison.Ordinal))
        {
            // Exercise the existing platform posture verifier deterministically without modifying machine ACLs.
            string rejected = defect == "public-root" ? _filesRoot : defect == "public-directory" ? quarantine.Directory.Path : bytesPath;

            SecureFilePermissions.StrictOwnerOnlyVerificationForTests = (candidate, _) =>
                NoFollowPathTopology.NormalizeMacOsSystemAlias(candidate) == NoFollowPathTopology.NormalizeMacOsSystemAlias(rejected) ? false : null;
        }
        else
        {
            string moved = Path.Combine(_filesRoot, "other-file");

            File.Move(quarantine.Quarantined.Path, moved);

            File.CreateSymbolicLink(quarantine.Quarantined.Path, moved);

            bytesPath = moved;
        }

        string[] pathsBefore = Directory.GetFileSystemEntries(_filesRoot, "*", SearchOption.TopDirectoryOnly).Order().ToArray();

        Assert.Equal(UploadedFileDeleteStatus.RecoveryRequired, await _repo.TryDeleteUnreferencedAsync(id));

        Assert.NotNull(await _repo.GetByIdAsync(id));

        Assert.Equal(bytes, await File.ReadAllBytesAsync(bytesPath));

        Assert.Equal(pathsBefore, Directory.GetFileSystemEntries(_filesRoot, "*", SearchOption.TopDirectoryOnly).Order().ToArray());

        if (defect == "destination") Assert.Equal(new byte[] { 9, 9 }, await File.ReadAllBytesAsync(path));
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Recovery_cleans_only_an_empty_recognized_directory_and_keeps_other_ids(bool liveRow)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid id = Guid.NewGuid();

        string path = Path.Combine(_filesRoot, id.ToString("N"));

        await File.WriteAllBytesAsync(path, [1, 2]);

        Assert.True(SecureFilePermissions.TryApplyOwnerOnlyFileStrict(path));

        await _repo!.CreateAsync(new UploadedFileRecord(id, "empty.bin", 2, "assistants", "application/octet-stream", DateTimeOffset.UtcNow));

        Assert.True(IdentityOwnedFileSystemCleanup.TryCapturePath(path, FileSystemObjectKind.RegularFile, out IdentityOwnedFileSystemArtifact artifact));

        Assert.True(IdentityOwnedFileSystemCleanup.TryQuarantine(artifact, $".arcanum-file-delete-{id:N}-", out IdentityOwnedFileSystemQuarantine quarantine));

        if (liveRow) File.Move(quarantine.Quarantined.Path, path);
        else
        {
            await _repo.DeleteAsync(id);

            File.Delete(quarantine.Quarantined.Path);
        }

        string other = Path.Combine(_filesRoot, $".arcanum-file-delete-{Guid.NewGuid():N}-{Guid.NewGuid():N}");

        Directory.CreateDirectory(other);

        string otherFile = Path.Combine(other, "unrelated");

        await File.WriteAllBytesAsync(otherFile, [9, 8]);

        await _db!.DisposeAsync();

        _db = _fixture.CreateContext(_dbPath);

        _repo = new UploadedFileRepository(_db, _filesRoot);

        Assert.Equal(liveRow ? UploadedFileDeleteStatus.Deleted : UploadedFileDeleteStatus.NotFound,
            await _repo.TryDeleteUnreferencedAsync(id));

        Assert.False(Directory.Exists(quarantine.Directory.Path));

        Assert.Equal(new byte[] { 9, 8 }, await File.ReadAllBytesAsync(otherFile));
    }

    [SkippableTheory]
    [InlineData("root")]
    [InlineData("directory")]
    [InlineData("file")]
    [InlineData("cancel")]
    public async Task Recovery_refuses_replacement_after_current_capture_and_preserves_cancellation(string eventKind)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using CancellationTokenSource cancellation = new();

        Guid id = Guid.NewGuid();

        string path = Path.Combine(_filesRoot, id.ToString("N"));

        await File.WriteAllBytesAsync(path, [2, 3]);

        Assert.True(SecureFilePermissions.TryApplyOwnerOnlyFileStrict(path));

        await _repo!.CreateAsync(new UploadedFileRecord(id, "replace.bin", 2, "assistants", "application/octet-stream", DateTimeOffset.UtcNow));

        Assert.True(IdentityOwnedFileSystemCleanup.TryCapturePath(path, FileSystemObjectKind.RegularFile, out IdentityOwnedFileSystemArtifact artifact));

        Assert.True(IdentityOwnedFileSystemCleanup.TryQuarantine(artifact, $".arcanum-file-delete-{id:N}-", out IdentityOwnedFileSystemQuarantine quarantine));

        string capturedPath = eventKind == "root" ? _filesRoot : eventKind == "directory" ? quarantine.Directory.Path : quarantine.Quarantined.Path;

        string normalized = NoFollowPathTopology.NormalizeMacOsSystemAlias(capturedPath);

        string moved = capturedPath + "-retained";

        bool triggered = false;

        int rootReads = 0;

        FileHandleIdentityInterop.TryGetPathMetadataNoFollowForTests = candidate =>
        {
            if (!triggered && string.Equals(candidate, normalized, StringComparison.Ordinal)
                && (eventKind != "root" || ++rootReads == 2))
            {
                triggered = true;

                if (eventKind == "cancel") cancellation.Cancel();
                else if (eventKind == "file")
                {
                    File.Move(capturedPath, moved);

                    File.WriteAllBytes(capturedPath, [9, 9]);

                    Assert.True(SecureFilePermissions.TryApplyOwnerOnlyFileStrict(capturedPath));
                }
                else
                {
                    Directory.Move(capturedPath, moved);

                    Assert.True(SecureFilePermissions.TryEnsureOwnerOnlyDirectoryExistsStrict(capturedPath));

                    File.WriteAllBytes(Path.Combine(capturedPath, "replacement"), [9, 9]);
                }
            }

            return ReadActualMetadata(candidate);
        };

        try
        {
            if (eventKind == "cancel")
            {
                OperationCanceledException failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => _repo.TryDeleteUnreferencedAsync(id, cancellation.Token));

                Assert.Equal(cancellation.Token, failure.CancellationToken);
            }
            else
            {
                Assert.Equal(UploadedFileDeleteStatus.RecoveryRequired, await _repo.TryDeleteUnreferencedAsync(id));
            }

            Assert.True(triggered);

            Assert.NotNull(await _repo.GetByIdAsync(id));

            string originalBytes = eventKind == "cancel" ? capturedPath : eventKind == "file" ? moved
                : eventKind == "root" ? Path.Combine(moved, Path.GetFileName(quarantine.Directory.Path), id.ToString("N"))
                : Path.Combine(moved, id.ToString("N"));

            Assert.Equal(new byte[] { 2, 3 }, await File.ReadAllBytesAsync(originalBytes));

            if (eventKind != "cancel") Assert.Equal(new byte[] { 9, 9 }, await File.ReadAllBytesAsync(
                eventKind == "file" ? capturedPath : Path.Combine(capturedPath, "replacement")));
        }
        finally
        {
            FileHandleIdentityInterop.TryGetPathMetadataNoFollowForTests = null;

            if (eventKind == "root" && Directory.Exists(moved)) Directory.Delete(moved, recursive: true);
        }
    }

    [SkippableTheory]
    [InlineData("D", false)]
    [InlineData("D", true)]
    [InlineData("N", false)]
    [InlineData("N", true)]
    public async Task Recovery_preserves_legacy_identity_membership(string format, bool referenced)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid id = Guid.NewGuid();

        string legacy = id.ToString(format);

        string path = Path.Combine(_filesRoot, id.ToString("N"));

        await File.WriteAllBytesAsync(path, [4, 5]);

        Assert.True(SecureFilePermissions.TryApplyOwnerOnlyFileStrict(path));

        await InsertLegacyFileAsync(legacy);

        if (referenced) await ExecuteSqlAsync($"""
            INSERT INTO "Batches" ("Id", "InputFileId", "Endpoint", "Status", "CreatedAt")
            VALUES ('legacy-recovery', '{legacy}', '/v1/chat/completions', 'completed', '{UtcInstantText.Format(DateTimeOffset.UtcNow)}');
            """);

        Assert.True(IdentityOwnedFileSystemCleanup.TryCapturePath(path, FileSystemObjectKind.RegularFile, out IdentityOwnedFileSystemArtifact artifact));

        Assert.True(IdentityOwnedFileSystemCleanup.TryQuarantine(artifact, $".arcanum-file-delete-{id:N}-", out IdentityOwnedFileSystemQuarantine quarantine));

        await _db!.DisposeAsync();

        _db = _fixture.CreateContext(_dbPath);

        _repo = new UploadedFileRepository(_db, _filesRoot);

        Assert.Equal(referenced ? UploadedFileDeleteStatus.ReferencedByBatch : UploadedFileDeleteStatus.Deleted,
            await _repo.TryDeleteUnreferencedAsync(id));

        Assert.Equal(referenced ? 1L : 0L, await CountFileRowsAsync(id));

        Assert.False(Directory.Exists(quarantine.Directory.Path));

        if (referenced) Assert.Equal(new byte[] { 4, 5 }, await File.ReadAllBytesAsync(path));
        else Assert.False(File.Exists(path));
    }

    [SkippableFact]
    public async Task Recovery_observes_a_batch_reference_committed_by_the_preceding_writer()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid id = Guid.NewGuid();

        string path = Path.Combine(_filesRoot, id.ToString("N"));

        await File.WriteAllBytesAsync(path, [5, 6]);

        Assert.True(SecureFilePermissions.TryApplyOwnerOnlyFileStrict(path));

        await _repo!.CreateAsync(new UploadedFileRecord(id, "writer.bin", 2, "batch", "application/octet-stream", DateTimeOffset.UtcNow));

        Assert.True(IdentityOwnedFileSystemCleanup.TryCapturePath(path, FileSystemObjectKind.RegularFile, out IdentityOwnedFileSystemArtifact artifact));

        Assert.True(IdentityOwnedFileSystemCleanup.TryQuarantine(artifact, $".arcanum-file-delete-{id:N}-", out IdentityOwnedFileSystemQuarantine quarantine));

        await using ArcanumDbContext writerDb = _fixture.CreateContext(_dbPath);

        SqliteConnection writer = Assert.IsType<SqliteConnection>(writerDb.Database.GetDbConnection());

        using SqliteStatementTrace trace = new(Assert.IsType<SqliteConnection>(_db!.Database.GetDbConnection()));

        await using SqliteTransaction transaction = writer.BeginTransaction(deferred: false);

        Task<UploadedFileDeleteStatus> recovery = Task.Run(() => _repo.TryDeleteUnreferencedAsync(id));

        try
        {
            System.Diagnostics.Stopwatch waiting = System.Diagnostics.Stopwatch.StartNew();

            while (trace.ImmediateTransactions == 0 && waiting.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(5);

            Assert.True(trace.ImmediateTransactions > 0);

            Assert.False(recovery.IsCompleted);

            Assert.False(File.Exists(path));

            Assert.Equal(new byte[] { 5, 6 }, await File.ReadAllBytesAsync(quarantine.Quarantined.Path));

            await using SqliteCommand command = writer.CreateCommand();

            command.Transaction = transaction;

            command.CommandText = """
                INSERT INTO "Batches" ("Id", "InputFileId", "Endpoint", "Status", "CreatedAt")
                VALUES (@batch, @file, '/v1/chat/completions', 'completed', @created);
                """;

            command.Parameters.AddWithValue("@batch", Guid.NewGuid().ToString("D"));

            command.Parameters.AddWithValue("@file", GrimoireEntitySql.Format(id));

            command.Parameters.AddWithValue("@created", UtcInstantText.Format(DateTimeOffset.UtcNow));

            Assert.Equal(1, await command.ExecuteNonQueryAsync());

            await transaction.CommitAsync();

            Assert.Equal(UploadedFileDeleteStatus.ReferencedByBatch, await recovery.WaitAsync(TimeSpan.FromSeconds(30)));

            Assert.NotNull(await _repo.GetByIdAsync(id));

            Assert.Equal(new byte[] { 5, 6 }, await File.ReadAllBytesAsync(path));

            Assert.False(Directory.Exists(quarantine.Directory.Path));
        }
        finally
        {
            await transaction.DisposeAsync();

            await recovery.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    [SkippableTheory]
    [InlineData("input")]
    [InlineData("output")]
    [InlineData("error")]
    public async Task Recovery_preserves_an_absent_rows_bytes_when_any_batch_role_still_references_it(string role)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid id = Guid.NewGuid();

        Guid input = role == "input" ? id : Guid.NewGuid();

        string path = Path.Combine(_filesRoot, id.ToString("N"));

        await File.WriteAllBytesAsync(path, [7, 8]);

        Assert.True(SecureFilePermissions.TryApplyOwnerOnlyFileStrict(path));

        DateTimeOffset now = DateTimeOffset.UtcNow;

        await _repo!.CreateAsync(new UploadedFileRecord(id, "retained.bin", 2, "batch", "application/octet-stream", now));

        if (input != id) await _repo.CreateAsync(new UploadedFileRecord(input, "input.bin", 0, "batch", "application/octet-stream", now));

        BatchRepository batches = new(_db!);

        await batches.CreateAsync(new BatchRecord(Guid.NewGuid(), input, "/v1/chat/completions", BatchStatuses.Completed, now, now,
            role == "output" ? id : null, role == "error" ? id : null));

        Assert.True(IdentityOwnedFileSystemCleanup.TryCapturePath(path, FileSystemObjectKind.RegularFile, out IdentityOwnedFileSystemArtifact artifact));

        Assert.True(IdentityOwnedFileSystemCleanup.TryQuarantine(artifact, $".arcanum-file-delete-{id:N}-", out IdentityOwnedFileSystemQuarantine quarantine));

        // Model a legacy/inconsistent reference using the real metadata-only seam; no foreign-key disabling or schema change.
        await _repo.DeleteAsync(id);

        await _db!.DisposeAsync();

        _db = _fixture.CreateContext(_dbPath);

        _repo = new UploadedFileRepository(_db, _filesRoot);

        Assert.Null(await _repo.GetByIdAsync(id));

        Assert.Equal(UploadedFileDeleteStatus.RecoveryRequired, await _repo.TryDeleteUnreferencedAsync(id));

        Assert.Equal(new byte[] { 7, 8 }, await File.ReadAllBytesAsync(quarantine.Quarantined.Path));

        Assert.False(File.Exists(path));

        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM \"Batches\""));
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

        Assert.True(SecureFilePermissions.TryApplyOwnerOnlyFileStrict(path));

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
    public async Task CreateAsync_StoresTheCanonicalUppercaseDashedIdentity()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid id = Guid.NewGuid();

        await _repo!.CreateAsync(
            new UploadedFileRecord(id, "canonical.jsonl", 5, "batch", "application/jsonl", DateTimeOffset.UtcNow),
            CancellationToken.None);

        System.Data.Common.DbConnection connection = _db!.Database.GetDbConnection();

        await using System.Data.Common.DbCommand command = connection.CreateCommand();

        command.CommandText = "SELECT \"Id\" FROM \"UploadedFiles\" WHERE \"Id\" = @id";

        System.Data.Common.DbParameter parameter = command.CreateParameter();

        parameter.ParameterName = "@id";

        parameter.Value = id.ToString("D").ToUpperInvariant();

        command.Parameters.Add(parameter);

        Assert.Equal(GrimoireEntitySql.Format(id), await command.ExecuteScalarAsync(CancellationToken.None));
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

    /// <summary>
    /// The delete's classification and its conditional delete are answered by the primary key and by one index per
    /// batch file column, not by a scan of either table.
    /// </summary>
    /// <remarks>
    /// The statements are the repository's own constants, so the plan explained here is the text it runs. A scan of
    /// <c>UploadedFiles</c> or <c>Batches</c> here is the cost of every file delete growing with every file and
    /// every batch the installation ever held.
    /// </remarks>
    [SkippableTheory]
    [InlineData(UploadedFileRepository.ClassifyDeleteSql)]
    [InlineData(UploadedFileRepository.DeleteUnreferencedMetadataSql)]
    public async Task TryDeleteUnreferencedAsync_UsesThePrimaryKey(string statement)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string[] plan = await ExplainAsync(statement);

        string joined = string.Join(" | ", plan);

        // The scalar classifier emits one constant result row; actual table/index scans remain forbidden.
        Assert.DoesNotContain(plan, static line => line.StartsWith("SCAN", StringComparison.Ordinal)
            && !string.Equals(line, "SCAN CONSTANT ROW", StringComparison.Ordinal));

        Assert.Contains(
            plan,
            static line => line.StartsWith("SEARCH UploadedFiles USING", StringComparison.Ordinal)
                && line.EndsWith("INDEX sqlite_autoindex_UploadedFiles_1 (Id=?)", StringComparison.Ordinal));

        foreach (string index in (string[])["IX_Batches_InputFileId", "IX_Batches_OutputFileId", "IX_Batches_ErrorFileId"])
        {
            Assert.True(joined.Contains(index, StringComparison.Ordinal), $"No batch file reference is answered by {index}: {joined}");
        }
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

    /// <summary>
    /// A file still held in a pre-version-15 spelling is found by its Guid, so a stranded batch that names it is
    /// requeued rather than failed for a missing input.
    /// </summary>
    /// <remarks>
    /// An installation upgrading across an earlier step that declares a sweep keeps serving while that sweep
    /// drains, and the version-15 rewrite runs only after it. Until then the files uploaded before the upgrade are
    /// held lowercase dashed (what the earlier writer stored) or dash-free, and startup batch recovery reads a
    /// not-found here as a missing input and fails the batch for good.
    /// </remarks>
    [SkippableTheory]
    [InlineData("D")]
    [InlineData("N")]
    public async Task GetByIdAsync_finds_a_file_still_held_in_a_pre_version_15_spelling(string format)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid id = Guid.NewGuid();

        await InsertLegacyFileAsync(id.ToString(format));

        UploadedFileRecord? loaded = await _repo!.GetByIdAsync(id, CancellationToken.None);

        Assert.NotNull(loaded);

        Assert.Equal(id, loaded!.Id);

        Assert.Equal("legacy.jsonl", loaded.Filename);
    }

    /// <summary>
    /// A legacy-spelling file that a legacy-spelling batch still names is refused as referenced, and neither row
    /// is touched.
    /// </summary>
    [SkippableFact]
    public async Task TryDeleteUnreferencedAsync_blocks_a_legacy_spelling_row_a_legacy_spelling_batch_names()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid id = Guid.NewGuid();

        string legacy = id.ToString("D");

        await InsertLegacyFileAsync(legacy);

        await ExecuteSqlAsync(
            $"""
            INSERT INTO "Batches" ("Id", "InputFileId", "Endpoint", "Status", "CreatedAt")
            VALUES ('legacy-batch', '{legacy}', '/v1/chat/completions', 'completed', '{UtcInstantText.Format(DateTimeOffset.UtcNow)}');
            """);

        Assert.Equal(
            UploadedFileDeleteStatus.ReferencedByBatch,
            await _repo!.TryDeleteUnreferencedAsync(id, CancellationToken.None));

        Assert.Equal(1L, await ScalarAsync($"SELECT COUNT(*) FROM \"UploadedFiles\" WHERE \"Id\" = '{legacy}'"));

        Assert.Equal(1L, await ScalarAsync($"SELECT COUNT(*) FROM \"Batches\" WHERE \"InputFileId\" = '{legacy}'"));
    }

    /// <summary>
    /// An unreferenced legacy-spelling file is deleted by both the conditional delete and the plain delete.
    /// </summary>
    [SkippableFact]
    public async Task Deletes_reach_an_unreferenced_legacy_spelling_row()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid conditional = Guid.NewGuid();

        Guid plain = Guid.NewGuid();

        await InsertLegacyFileAsync(conditional.ToString("D"));

        await InsertLegacyFileAsync(plain.ToString("N"));

        Assert.Equal(
            UploadedFileDeleteStatus.Deleted,
            await _repo!.TryDeleteUnreferencedAsync(conditional, CancellationToken.None));

        await _repo.DeleteAsync(plain, CancellationToken.None);

        Assert.Equal(0L, await CountFileRowsAsync(conditional));

        Assert.Equal(0L, await CountFileRowsAsync(plain));
    }

    /// <summary>
    /// When version 15 left a lowercase row beside the canonical row of the same Guid (its rewrite would have
    /// collided on the primary key), deleting the file removes both, since both describe the one owned file.
    /// </summary>
    [SkippableFact]
    public async Task TryDeleteUnreferencedAsync_removes_both_rows_of_a_case_only_collision()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid id = Guid.NewGuid();

        await _repo!.CreateAsync(
            new UploadedFileRecord(id, "canonical.jsonl", 5, "batch", "application/jsonl", DateTimeOffset.UtcNow),
            CancellationToken.None);

        await InsertLegacyFileAsync(id.ToString("D"));

        Assert.Equal(
            UploadedFileDeleteStatus.Deleted,
            await _repo.TryDeleteUnreferencedAsync(id, CancellationToken.None));

        Assert.Equal(0L, await CountFileRowsAsync(id));
    }

    [SkippableFact]

    public async Task TryDeleteUnreferencedAsync_WhenMetadataDeleteFails_RestoresOwnedBytesAndMetadata()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid id = Guid.NewGuid();

        string path = Path.Combine(_filesRoot, id.ToString("N"));

        byte[] bytes = [1, 2, 3, 4];

        await File.WriteAllBytesAsync(path, bytes);

        Assert.True(SecureFilePermissions.TryApplyOwnerOnlyFileStrict(path));

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

        Assert.True(SecureFilePermissions.TryApplyOwnerOnlyFileStrict(path));

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
            UploadedFileDeleteStatus.NotFound,
            await _repo.TryDeleteUnreferencedAsync(id, CancellationToken.None));

        Assert.False(File.Exists(quarantinedPath));

        Assert.False(Directory.Exists(quarantineDirectory));
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

    private async Task<string[]> ExplainAsync(string statement)
    {
        System.Data.Common.DbConnection connection = _db!.Database.GetDbConnection();

        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(CancellationToken.None);
        }

        await using System.Data.Common.DbCommand command = connection.CreateCommand();

        command.CommandText = "EXPLAIN QUERY PLAN " + statement;

        System.Data.Common.DbParameter id = command.CreateParameter();

        id.ParameterName = "@id";

        id.Value = GrimoireEntitySql.Format(Guid.Empty);

        command.Parameters.Add(id);

        List<string> rows = [];

        await using (System.Data.Common.DbDataReader reader = await command.ExecuteReaderAsync(CancellationToken.None))
        {
            while (await reader.ReadAsync(CancellationToken.None))
            {
                rows.Add(reader.GetString(reader.GetOrdinal("detail")));
            }
        }

        return [.. rows];
    }

    private async Task<long> ScalarAsync(string sql)
    {
        System.Data.Common.DbConnection connection = _db!.Database.GetDbConnection();

        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(CancellationToken.None);
        }

        await using System.Data.Common.DbCommand command = connection.CreateCommand();

        command.CommandText = sql;

        return Convert.ToInt64(
            await command.ExecuteScalarAsync(CancellationToken.None),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private Task InsertLegacyFileAsync(string legacyId) =>
        ExecuteSqlAsync(
            $"""
            INSERT INTO "UploadedFiles" ("Id", "Filename", "Bytes", "Purpose", "MimeType", "CreatedAt")
            VALUES ('{legacyId}', 'legacy.jsonl', 5, 'batch', 'application/jsonl', '{UtcInstantText.Format(DateTimeOffset.UtcNow)}');
            """);

    private Task<long> CountFileRowsAsync(Guid id) =>
        ScalarAsync($"SELECT COUNT(*) FROM \"UploadedFiles\" WHERE lower(replace(\"Id\", '-', '')) = '{id:N}'");

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
