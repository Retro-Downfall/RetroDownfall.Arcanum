using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Hosting;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Storage;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data;

[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class SessionAttachmentStoreTests : IAsyncLifetime
{

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private string _attachmentsRoot = string.Empty;

    private ArcanumDbContext? _db;

    private SessionAttachmentStore? _store;

    private ArcanumSettings _settings = new();

    public SessionAttachmentStoreTests(GrimoireFixture fixture)
    {

        _fixture = fixture;

    }

    public Task InitializeAsync()
    {

        _dbPath = _fixture.CopyDatabase();

        _attachmentsRoot = Path.Combine(Path.GetTempPath(), "arcanum-attachments-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(_attachmentsRoot);

        _db = _fixture.CreateContext(_dbPath);

        _settings = new ArcanumSettings();

        _store = CreateStore(_settings);

        return Task.CompletedTask;

    }

    public async Task DisposeAsync()
    {

        if (_db is not null)
        {

            await _db.DisposeAsync();

        }

        if (File.Exists(_dbPath))
        {

            File.Delete(_dbPath);

        }

        if (Directory.Exists(_attachmentsRoot))
        {

            Directory.Delete(_attachmentsRoot, recursive: true);

        }

    }

    private SessionAttachmentStore CreateStore(ArcanumSettings settings) =>
        new(
            _db!,
            Options.Create(settings),
            _attachmentsRoot,
            CreateEncryptedBlobStore());

    private static EncryptedBlobStore CreateEncryptedBlobStore() =>
        new(
            new FixedFileEncryptionKeyProvider(),
            new EncryptedBlobStoreOptions { ChunkSize = 64 });

    [SkippableFact]
    public async Task PersistNewFromSourceAsync_round_trips_verified_provenance()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string workspace = Path.Combine(Path.GetTempPath(), "arcanum-workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes("workspace source");
            string sourcePath = Path.Combine(workspace, "source.txt");
            await File.WriteAllBytesAsync(sourcePath, bytes);
            AttachmentSourceResolver resolver = new(new TestWorkspaceContext(workspace));
            SessionAttachmentStore store = new(
                _db!,
                Options.Create(_settings),
                _attachmentsRoot,
                CreateEncryptedBlobStore(),
                sourceResolver: resolver);

            SessionAttachmentRecord saved = await store.PersistNewFromSourceAsync(
                Guid.NewGuid(),
                null,
                null,
                "source.txt",
                "source.txt",
                bytes,
                "text/plain",
                SessionAttachmentKind.Text,
                new AttachmentSourceClaim(sourcePath));
            SessionAttachmentRecord? reloaded = await store.GetByIdAsync(saved.Id);

            Assert.NotNull(reloaded);
            Assert.Equal(AttachmentSourceKind.WorkspaceFile, reloaded!.Source!.Kind);
            Assert.Equal(AttachmentSourceStatus.Refreshable, reloaded.Source.Status);
            Assert.Equal("source.txt", reloaded.Source.WorkspaceRelativePath);
            Assert.DoesNotContain(workspace, reloaded.Source.WorkspaceRelativePath, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [SkippableFact]

    public async Task RevalidateBoundSourcesAsync_marks_changed_workspace_file_as_prior_version()

    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string workspace = Path.Combine(

            Path.GetTempPath(),

            "arcanum-workspace-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(workspace);

        try

        {

            byte[] before = Encoding.UTF8.GetBytes("before");

            string sourcePath = Path.Combine(workspace, "source.txt");

            await File.WriteAllBytesAsync(sourcePath, before);

            AttachmentSourceResolver resolver = new(new TestWorkspaceContext(workspace));

            SessionAttachmentStore store = new(

                _db!,

                Options.Create(_settings),

                _attachmentsRoot,

                CreateEncryptedBlobStore(),

                sourceResolver: resolver);

            Guid sessionId = Guid.NewGuid();

            _ = await store.PersistNewFromSourceAsync(

                sessionId,

                null,

                null,

                "source.txt",

                "source.txt",

                before,

                "text/plain",

                SessionAttachmentKind.Text,

                new AttachmentSourceClaim(sourcePath));

            await File.WriteAllBytesAsync(sourcePath, Encoding.UTF8.GetBytes("after"));

            IReadOnlyList<SessionAttachmentRecord> revalidated = await store

                .RevalidateBoundSourcesAsync(sessionId);

            SessionAttachmentRecord row = Assert.Single(revalidated);

            Assert.Equal(AttachmentSourceStatus.PriorVersion, row.Source!.Status);

            Assert.False(row.Source.IsRefreshable);

            Assert.Equal(

                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("after"))),

                row.Source.LastObservedContentSha256);

        }
        finally

        {

            Directory.Delete(workspace, recursive: true);

        }

    }

    [SkippableFact]
    public async Task PersistRefreshedAsync_changed_source_creates_exactly_one_next_version()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string workspace = Path.Combine(Path.GetTempPath(), "arcanum-refresh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            Guid sessionId = Guid.NewGuid();
            string sourcePath = Path.Combine(workspace, "notes.txt");
            byte[] original = Encoding.UTF8.GetBytes("original");
            await File.WriteAllBytesAsync(sourcePath, original);
            AttachmentSourceResolver resolver = new(new TestWorkspaceContext(workspace));
            SessionAttachmentStore store = new(
                _db!, Options.Create(_settings), _attachmentsRoot, CreateEncryptedBlobStore(), sourceResolver: resolver);
            SessionAttachmentRecord first = await store.PersistNewFromSourceAsync(
                sessionId, null, null, "notes.txt", "notes.txt", original, "text/plain",
                SessionAttachmentKind.Text, new AttachmentSourceClaim(sourcePath));
            await File.WriteAllTextAsync(sourcePath, "changed");
            AttachmentSourceResolution current = await resolver.ResolveCurrentAsync(
                first.Source!, first.ContentSha256, 1024, static (_, _) => Task.FromResult(true));

            SessionAttachmentRefreshPersistence refreshed = await store.PersistRefreshedAsync(
                first, Guid.NewGuid(), current);
            SessionAttachmentRefreshPersistence repeated = await store.PersistRefreshedAsync(
                refreshed.Record, Guid.NewGuid(), current);

            Assert.True(refreshed.NewVersionCreated);
            Assert.Equal(2, refreshed.Record.Version);
            Assert.Equal("changed", Encoding.UTF8.GetString((await store.ReadBytesAsync(refreshed.Record)).Span));
            Assert.Equal(AttachmentSourceStatus.Refreshable, refreshed.Record.Source!.Status);
            Assert.False(repeated.NewVersionCreated);
            Assert.Equal(refreshed.Record.Id, repeated.Record.Id);
            Assert.Equal(2, (await store.ListBoundAsync(sessionId)).Count);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [SkippableFact]
    public async Task PersistRefreshedAsync_unchanged_source_reuses_latest_without_duplicate()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string workspace = Path.Combine(Path.GetTempPath(), "arcanum-refresh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            Guid sessionId = Guid.NewGuid();
            string sourcePath = Path.Combine(workspace, "notes.txt");
            byte[] original = Encoding.UTF8.GetBytes("unchanged");
            await File.WriteAllBytesAsync(sourcePath, original);
            AttachmentSourceResolver resolver = new(new TestWorkspaceContext(workspace));
            SessionAttachmentStore store = new(
                _db!, Options.Create(_settings), _attachmentsRoot, CreateEncryptedBlobStore(), sourceResolver: resolver);
            SessionAttachmentRecord first = await store.PersistNewFromSourceAsync(
                sessionId, null, null, "notes.txt", "notes.txt", original, "text/plain",
                SessionAttachmentKind.Text, new AttachmentSourceClaim(sourcePath));
            AttachmentSourceResolution current = await resolver.ResolveCurrentAsync(
                first.Source!, first.ContentSha256, 1024, static (_, _) => Task.FromResult(true));

            SessionAttachmentRefreshPersistence refreshed = await store.PersistRefreshedAsync(first, null, current);

            Assert.False(refreshed.NewVersionCreated);
            Assert.Equal(first.Id, refreshed.Record.Id);
            Assert.Single(await store.ListBoundAsync(sessionId));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [SkippableFact]
    public async Task PersistRefreshedAsync_changed_content_reclassifies_kind_from_detected_mime()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        byte[] originalBytes = "%PDF-old"u8.ToArray();

        AttachmentSourceResolution originalSource = CreateResolvedSource(
            originalBytes,
            "application/pdf");

        SessionAttachmentRecord original = await _store!.PersistNewResolvedSourceAsync(
            sessionId,
            pendingTurnId: null,
            entryId: null,
            logicalNameHint: "artifact.bin",
            originalFileName: "artifact.bin",
            SessionAttachmentKind.Binary,
            originalSource);

        byte[] refreshedBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

        AttachmentSourceResolution detectedImage = CreateResolvedSource(
            refreshedBytes,
            "image/png");

        AttachmentSourceResolution refreshedSource = detectedImage with
        {
            Metadata = detectedImage.Metadata with
            {
                Status = AttachmentSourceStatus.PriorVersion,
            },
        };

        SessionAttachmentRefreshPersistence refreshed = await _store.PersistRefreshedAsync(
            original,
            Guid.NewGuid(),
            refreshedSource);

        Assert.True(refreshed.NewVersionCreated);

        Assert.Equal(2, refreshed.Record.Version);

        Assert.Equal(SessionAttachmentKind.Image, refreshed.Record.Kind);

        Assert.Equal("image/png", refreshed.Record.MimeType);

        Assert.Equal(refreshedBytes, (await _store.ReadBytesAsync(refreshed.Record)).ToArray());
    }

    [SkippableFact]
    public async Task PersistNewAsync_bound_v1_writes_row_and_bytes_readable()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        byte[] bytes = Encoding.UTF8.GetBytes("hello attachment");

        SessionAttachmentRecord record = await _store!.PersistNewAsync(
            sessionId,
            pendingTurnId: null,
            entryId: null,
            logicalNameHint: "notes.txt",
            originalFileName: "notes.txt",
            bytes,
            mimeType: "text/plain",
            SessionAttachmentKind.Text);

        Assert.Equal(SessionAttachmentState.Bound, record.State);

        Assert.Equal(sessionId, record.SessionId);

        Assert.Null(record.PendingTurnId);

        Assert.Equal(1, record.Version);

        Assert.Equal("notes.txt", record.LogicalKey);

        Assert.StartsWith(sessionId.ToString("N") + Path.DirectorySeparatorChar, record.RelativePath.Replace('/', Path.DirectorySeparatorChar), StringComparison.Ordinal);

        Assert.Contains($"{Path.DirectorySeparatorChar}v1{Path.DirectorySeparatorChar}", record.RelativePath.Replace('/', Path.DirectorySeparatorChar), StringComparison.Ordinal);

        ReadOnlyMemory<byte> loaded = await _store.ReadBytesAsync(record);

        Assert.Equal(bytes, loaded.ToArray());
        Assert.Equal(EncryptedBlobFormat.CurrentVersion, record.EncryptionVersion);
        Assert.False(string.IsNullOrWhiteSpace(record.EncryptionKeyId));
        byte[] stored = await File.ReadAllBytesAsync(
            Path.Combine(_attachmentsRoot, record.RelativePath));
        Assert.True(stored.AsSpan().StartsWith("ARCABLOB"u8));
        Assert.DoesNotContain("hello attachment"u8.ToArray(), stored);

        SessionAttachmentRecord? byId = await _store.GetByIdAsync(record.Id);

        Assert.NotNull(byId);

        Assert.Equal(record.Id, byId!.Id);

    }

    [SkippableFact]
    public async Task PersistNewResolvedSourceAsync_retains_provenance_and_reuses_version_logic()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        ISessionAttachmentStore store = _store!;

        byte[] firstBytes = Encoding.UTF8.GetBytes("first reference");

        AttachmentSourceResolution firstSource = CreateResolvedSource(firstBytes);

        SessionAttachmentRecord first = await store.PersistNewResolvedSourceAsync(
            sessionId,
            pendingTurnId: null,
            entryId: null,
            logicalNameHint: "reference.txt",
            originalFileName: "reference.txt",
            SessionAttachmentKind.Text,
            firstSource);

        byte[] changedBytes = Encoding.UTF8.GetBytes("changed reference");

        AttachmentSourceResolution changedSource = CreateResolvedSource(changedBytes);

        SessionAttachmentRecord changed = await store.PersistNewResolvedSourceAsync(
            sessionId,
            pendingTurnId: null,
            entryId: null,
            logicalNameHint: "reference.txt",
            originalFileName: "reference.txt",
            SessionAttachmentKind.Text,
            changedSource);

        SessionAttachmentRecord repeated = await store.PersistNewResolvedSourceAsync(
            sessionId,
            pendingTurnId: null,
            entryId: null,
            logicalNameHint: "reference.txt",
            originalFileName: "reference.txt",
            SessionAttachmentKind.Text,
            changedSource);

        Assert.Equal(AttachmentSourceKind.WorkspaceFile, first.Source!.Kind);

        Assert.Equal(AttachmentSourceStatus.Refreshable, first.Source.Status);

        Assert.Equal(firstSource.Metadata.WorkspaceIdentity, first.Source.WorkspaceIdentity);

        Assert.Equal(firstSource.Metadata.LastObservedContentSha256, first.Source.LastObservedContentSha256);

        Assert.Equal(1, first.Version);

        Assert.Equal(2, changed.Version);

        Assert.Equal(changed.Id, repeated.Id);

        Assert.Equal(changedBytes, (await store.ReadBytesAsync(changed)).ToArray());

        Assert.Equal(2, (await store.ListBoundAsync(sessionId)).Count);
    }

    [SkippableFact]

    public async Task Identical_snapshot_after_live_reference_creates_snapshot_version_without_provenance_blur()

    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        byte[] bytes = Encoding.UTF8.GetBytes("same bytes, different attachment mode");

        SessionAttachmentRecord live = await _store!.PersistNewResolvedSourceAsync(

            sessionId,

            pendingTurnId: null,

            entryId: null,

            logicalNameHint: "mode.txt",

            originalFileName: "mode.txt",

            SessionAttachmentKind.Text,

            CreateResolvedSource(bytes));

        SessionAttachmentRecord snapshot = await _store.PersistNewAsync(

            sessionId,

            pendingTurnId: null,

            entryId: null,

            logicalNameHint: "mode.txt",

            originalFileName: "mode.txt",

            bytes,

            mimeType: "text/plain",

            SessionAttachmentKind.Text);

        Assert.NotEqual(live.Id, snapshot.Id);

        Assert.Equal(1, live.Version);

        Assert.Equal(2, snapshot.Version);

        Assert.Equal(AttachmentSourceKind.WorkspaceFile, live.Source!.Kind);

        Assert.Equal(

            AttachmentSourceKind.SnapshotOnly,

            (snapshot.Source ?? AttachmentSourceMetadata.SnapshotOnly).Kind);

        Assert.Equal(2, (await _store.ListBoundAsync(sessionId)).Count);

    }

    [SkippableFact]

    public async Task Identical_live_reference_after_snapshot_creates_live_version_without_provenance_blur()

    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        byte[] bytes = Encoding.UTF8.GetBytes("same bytes, different attachment mode");

        SessionAttachmentRecord snapshot = await _store!.PersistNewAsync(

            sessionId,

            pendingTurnId: null,

            entryId: null,

            logicalNameHint: "mode.txt",

            originalFileName: "mode.txt",

            bytes,

            mimeType: "text/plain",

            SessionAttachmentKind.Text);

        SessionAttachmentRecord live = await _store.PersistNewResolvedSourceAsync(

            sessionId,

            pendingTurnId: null,

            entryId: null,

            logicalNameHint: "mode.txt",

            originalFileName: "mode.txt",

            SessionAttachmentKind.Text,

            CreateResolvedSource(bytes));

        Assert.NotEqual(snapshot.Id, live.Id);

        Assert.Equal(1, snapshot.Version);

        Assert.Equal(2, live.Version);

        Assert.Equal(

            AttachmentSourceKind.SnapshotOnly,

            (snapshot.Source ?? AttachmentSourceMetadata.SnapshotOnly).Kind);

        Assert.Equal(AttachmentSourceKind.WorkspaceFile, live.Source!.Kind);

        Assert.Equal(2, (await _store.ListBoundAsync(sessionId)).Count);

    }

    [SkippableTheory]

    [InlineData("registered-workspace-id", "other.txt")]

    [InlineData("other-workspace-id", "reference.txt")]

    public async Task Identical_live_reference_from_different_source_identity_creates_new_version(

        string workspaceIdentity,

        string relativePath)

    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        byte[] bytes = Encoding.UTF8.GetBytes("same live bytes from a distinct source");

        AttachmentSourceResolution firstSource = CreateResolvedSource(bytes);

        AttachmentSourceResolution distinctSource = firstSource with

        {

            Metadata = firstSource.Metadata with

            {

                WorkspaceIdentity = workspaceIdentity,

                WorkspaceRelativePath = relativePath,

                LastKnownCanonicalPath = Path.GetFullPath(

                    Path.Combine(Path.GetTempPath(), workspaceIdentity, relativePath)),

            },

        };

        SessionAttachmentRecord first = await _store!.PersistNewResolvedSourceAsync(

            sessionId,

            pendingTurnId: null,

            entryId: null,

            logicalNameHint: "mode.txt",

            originalFileName: "mode.txt",

            SessionAttachmentKind.Text,

            firstSource);

        SessionAttachmentRecord distinct = await _store.PersistNewResolvedSourceAsync(

            sessionId,

            pendingTurnId: null,

            entryId: null,

            logicalNameHint: "mode.txt",

            originalFileName: "mode.txt",

            SessionAttachmentKind.Text,

            distinctSource);

        Assert.NotEqual(first.Id, distinct.Id);

        Assert.Equal(1, first.Version);

        Assert.Equal(2, distinct.Version);

        Assert.Equal(workspaceIdentity, distinct.Source!.WorkspaceIdentity);

        Assert.Equal(relativePath, distinct.Source.WorkspaceRelativePath);

    }

    [SkippableFact]
    public async Task Resolved_source_persistence_rejects_failed_or_incomplete_provenance()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        ISessionAttachmentStore store = _store!;

        AttachmentSourceResolution denied = new(
            AttachmentSourceMetadata.SnapshotOnly with
            {
                Status = AttachmentSourceStatus.Inaccessible,
                DiagnosticReason = "denied",
            },
            ReadOnlyMemory<byte>.Empty);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.PersistNewResolvedSourceAsync(
                sessionId,
                pendingTurnId: null,
                entryId: null,
                logicalNameHint: "denied.txt",
                originalFileName: "denied.txt",
                SessionAttachmentKind.Text,
                denied));

        byte[] bytes = Encoding.UTF8.GetBytes("incomplete");

        AttachmentSourceResolution complete = CreateResolvedSource(bytes);

        AttachmentSourceResolution incomplete = complete with
        {
            Metadata = complete.Metadata with
            {
                LastObservedByteLength = null,
            },
        };

        SessionAttachmentRecord latest = await store.PersistNewAsync(
            sessionId,
            pendingTurnId: null,
            entryId: null,
            logicalNameHint: "refresh.txt",
            originalFileName: "refresh.txt",
            bytes,
            mimeType: "text/plain",
            SessionAttachmentKind.Text);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.PersistRefreshedAsync(
                latest,
                entryId: null,
                incomplete with
                {
                    Metadata = incomplete.Metadata with
                    {
                        Status = AttachmentSourceStatus.PriorVersion,
                    },
                }));

        Assert.Null(await store.GetByLogicalAsync(sessionId, "denied.txt", version: null));
    }

    [SkippableFact]
    public async Task OpenReadAsync_returns_live_decrypted_stream_and_enforces_plaintext_length()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        byte[] expected = Encoding.UTF8.GetBytes("stream without buffering the whole attachment");

        SessionAttachmentRecord record = await _store!.PersistNewAsync(
            Guid.NewGuid(),
            pendingTurnId: null,
            entryId: null,
            logicalNameHint: "stream.txt",
            originalFileName: "stream.txt",
            expected,
            mimeType: "text/plain",
            SessionAttachmentKind.Text);

        ISessionAttachmentStore store = _store;

        await using (Stream stream = await store.OpenReadAsync(record))
        {
            Assert.IsNotType<MemoryStream>(stream);

            using MemoryStream copied = new();

            await stream.CopyToAsync(copied);

            Assert.Equal(expected, copied.ToArray());
        }

        SessionAttachmentRecord wrongLength = record with
        {
            ByteLength = record.ByteLength + 1,
        };

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await using Stream _ = await store.OpenReadAsync(wrongLength);
        });
    }

    private static AttachmentSourceResolution CreateResolvedSource(
        byte[] bytes,
        string mimeType = "text/plain")
    {
        string hash = Convert.ToHexString(SHA256.HashData(bytes));

        string canonical = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "registered", "reference.txt"));

        AttachmentSourceMetadata metadata = new(
            AttachmentSourceKind.WorkspaceFile,
            "registered-workspace-id",
            "reference.txt",
            canonical,
            hash,
            "0000000000000001:0000000000000002",
            DateTimeOffset.UtcNow,
            bytes.LongLength,
            AttachmentSourceStatus.Refreshable,
            null);

        return new AttachmentSourceResolution(metadata, bytes, mimeType);
    }

    private sealed class FixedFileEncryptionKeyProvider : IFileEncryptionKeyProvider
    {
        private readonly FileEncryptionKeyMaterial _material = FileEncryptionKeyMaterial.Create(
            Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray());

        public ValueTask<FileEncryptionKeyMaterial> GetForWriteAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_material);

        public ValueTask<FileEncryptionKeyMaterial> GetForReadAsync(
            string keyId,
            CancellationToken cancellationToken = default)
        {
            if (!string.Equals(keyId, _material.KeyId, StringComparison.Ordinal))
            {
                throw new EncryptedBlobKeyException("test key unavailable");
            }

            return ValueTask.FromResult(_material);
        }
    }

    [SkippableFact]
    public async Task PersistNewAsync_identical_bytes_same_logical_key_returns_same_id_no_v2()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        byte[] bytes = Encoding.UTF8.GetBytes("same-bytes");

        SessionAttachmentRecord first = await _store!.PersistNewAsync(
            sessionId,
            null,
            null,
            "notes.txt",
            "notes.txt",
            bytes,
            "text/plain",
            SessionAttachmentKind.Text);

        SessionAttachmentRecord second = await _store.PersistNewAsync(
            sessionId,
            null,
            null,
            "notes.txt",
            "notes.txt",
            bytes,
            "text/plain",
            SessionAttachmentKind.Text);

        Assert.Equal(first.Id, second.Id);

        Assert.Equal(1, second.Version);

        IReadOnlyList<SessionAttachmentRecord> listed = await _store.ListBoundAsync(sessionId);

        Assert.Single(listed);

    }

    [SkippableFact]
    public async Task PersistNewAsync_changed_bytes_creates_v2()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        SessionAttachmentRecord v1 = await _store!.PersistNewAsync(
            sessionId,
            null,
            null,
            "notes.txt",
            "notes.txt",
            Encoding.UTF8.GetBytes("v1-body"),
            "text/plain",
            SessionAttachmentKind.Text);

        SessionAttachmentRecord v2 = await _store.PersistNewAsync(
            sessionId,
            null,
            null,
            "notes.txt",
            "notes.txt",
            Encoding.UTF8.GetBytes("v2-body"),
            "text/plain",
            SessionAttachmentKind.Text);

        Assert.NotEqual(v1.Id, v2.Id);

        Assert.Equal(2, v2.Version);

        SessionAttachmentRecord? latest = await _store.GetByLogicalAsync(sessionId, "notes.txt", version: null);

        Assert.NotNull(latest);

        Assert.Equal(v2.Id, latest!.Id);

        SessionAttachmentRecord? byVersion = await _store.GetByLogicalAsync(sessionId, "notes.txt", version: 1);

        Assert.NotNull(byVersion);

        Assert.Equal(v1.Id, byVersion!.Id);

    }

    [SkippableFact]
    public async Task PersistNewAsync_rejects_unsafe_names()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        await Assert.ThrowsAsync<ArgumentException>(() => _store!.PersistNewAsync(
            sessionId,
            null,
            null,
            logicalNameHint: "CON",
            originalFileName: "notes.txt",
            Encoding.UTF8.GetBytes("x"),
            "text/plain",
            SessionAttachmentKind.Text));

        await Assert.ThrowsAsync<ArgumentException>(() => _store!.PersistNewAsync(
            sessionId,
            null,
            null,
            logicalNameHint: "notes.txt",
            originalFileName: "..",
            Encoding.UTF8.GetBytes("x"),
            "text/plain",
            SessionAttachmentKind.Text));

    }

    [SkippableFact]
    public async Task PersistPending_then_PromotePendingAsync_moves_to_bound_session_path()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string pendingTurnId = "turn-" + Guid.NewGuid().ToString("N");

        Guid sessionId = Guid.NewGuid();

        Guid entryId = Guid.NewGuid();

        byte[] bytes = Encoding.UTF8.GetBytes("pending-bytes");

        SessionAttachmentRecord pending = await _store!.PersistNewAsync(
            sessionId: null,
            pendingTurnId,
            entryId: null,
            "shot.png",
            "shot.png",
            bytes,
            "image/png",
            SessionAttachmentKind.Image);

        Assert.Equal(SessionAttachmentState.Pending, pending.State);

        Assert.Null(pending.SessionId);

        Assert.Equal(pendingTurnId, pending.PendingTurnId);

        Assert.StartsWith("_pending" + Path.DirectorySeparatorChar, pending.RelativePath.Replace('/', Path.DirectorySeparatorChar), StringComparison.Ordinal);

        string pendingAbsolute = Path.Combine(_attachmentsRoot, pending.RelativePath);

        Assert.True(File.Exists(pendingAbsolute));

        await _store.PromotePendingAsync(pendingTurnId, sessionId, entryId);

        SessionAttachmentRecord? promoted = await _store.GetByIdAsync(pending.Id);

        Assert.NotNull(promoted);

        Assert.Equal(SessionAttachmentState.Bound, promoted!.State);

        Assert.Equal(sessionId, promoted.SessionId);

        Assert.Equal(entryId, promoted.EntryId);

        Assert.Null(promoted.PendingTurnId);

        Assert.StartsWith(sessionId.ToString("N") + Path.DirectorySeparatorChar, promoted.RelativePath.Replace('/', Path.DirectorySeparatorChar), StringComparison.Ordinal);

        Assert.False(File.Exists(pendingAbsolute));

        Assert.True(File.Exists(Path.Combine(_attachmentsRoot, promoted.RelativePath)));

        IReadOnlyList<SessionAttachmentRecord> listed = await _store.ListBoundAsync(sessionId);

        Assert.Single(listed);

        Assert.Equal(pending.Id, listed[0].Id);

        ReadOnlyMemory<byte> loaded = await _store.ReadBytesAsync(promoted);

        Assert.Equal(bytes, loaded.ToArray());

    }

    [SkippableFact]
    public async Task DeleteStalePendingAsync_removes_old_pending_row_and_directory()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string pendingTurnId = "stale-" + Guid.NewGuid().ToString("N");

        SessionAttachmentRecord pending = await _store!.PersistNewAsync(
            null,
            pendingTurnId,
            null,
            "old.txt",
            "old.txt",
            Encoding.UTF8.GetBytes("stale"),
            "text/plain",
            SessionAttachmentKind.Text);

        string pendingDir = Path.Combine(_attachmentsRoot, "_pending", pendingTurnId);

        Assert.True(Directory.Exists(pendingDir));

        // Backdate CreatedAt so the row is older than the retention threshold.
        await BackdateCreatedAtAsync(pending.Id, DateTimeOffset.UtcNow.AddHours(-48));

        await _store.DeleteStalePendingAsync(TimeSpan.FromHours(24));

        SessionAttachmentRecord? gone = await _store.GetByIdAsync(pending.Id);

        Assert.Null(gone);

        Assert.False(Directory.Exists(pendingDir));

    }

    [SkippableFact]
    public async Task ValidateReferencesAsync_rejects_wrong_session_and_over_max()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionA = Guid.NewGuid();

        Guid sessionB = Guid.NewGuid();

        SessionAttachmentRecord a = await _store!.PersistNewAsync(
            sessionA,
            null,
            null,
            "a.txt",
            "a.txt",
            Encoding.UTF8.GetBytes("a"),
            "text/plain",
            SessionAttachmentKind.Text);

        SessionAttachmentRecord b = await _store.PersistNewAsync(
            sessionB,
            null,
            null,
            "b.txt",
            "b.txt",
            Encoding.UTF8.GetBytes("b"),
            "text/plain",
            SessionAttachmentKind.Text);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _store.ValidateReferencesAsync(sessionA, [b.Id], maxReferences: 8));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _store.ValidateReferencesAsync(sessionA, [a.Id, a.Id], maxReferences: 1));

        await _store.ValidateReferencesAsync(sessionA, [a.Id], maxReferences: 8);

    }

    [SkippableFact]
    public async Task PersistNewAsync_rejects_when_version_cap_exceeded()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SessionAttachmentStore store = CreateStore(new ArcanumSettings());

        Guid sessionId = Guid.NewGuid();

        int maxVersions = ArcanumSettingClamps.AttachmentsMaxVersionsPerLogicalKey(
            ArcanumRuntimeDefaults.Attachments.MaxVersionsPerLogicalKey);
        for (int version = 0; version < maxVersions; version++)
        {
            _ = await store.PersistNewAsync(
                sessionId,
                null,
                null,
                "cap.txt",
                "cap.txt",
                Encoding.UTF8.GetBytes($"version-{version}"),
                "text/plain",
                SessionAttachmentKind.Text);
        }

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.PersistNewAsync(
                sessionId,
                null,
                null,
                "cap.txt",
                "cap.txt",
                Encoding.UTF8.GetBytes("two"),
                "text/plain",
                SessionAttachmentKind.Text));

        Assert.Contains("version", ex.Message, StringComparison.OrdinalIgnoreCase);

    }

    [SkippableFact]
    public async Task PromotePendingAsync_db_failure_retains_pending_and_removes_destination()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string pendingTurnId = "turn-" + Guid.NewGuid().ToString("N");

        Guid sessionId = Guid.NewGuid();

        SessionAttachmentRecord pending = await _store!.PersistNewAsync(
            sessionId: null,
            pendingTurnId,
            entryId: null,
            "notes.txt",
            "notes.txt",
            Encoding.UTF8.GetBytes("pending-keep"),
            "text/plain",
            SessionAttachmentKind.Text);

        string pendingAbsolute = Path.Combine(_attachmentsRoot, pending.RelativePath);

        string expectedBoundAbsolute = Path.Combine(
            _attachmentsRoot,
            sessionId.ToString("N"),
            "notes.txt",
            "v1",
            "notes.txt");

        _store.AfterBytesCommittedBeforeDbForTesting = _ =>
            throw new InvalidOperationException("simulated exhausted DB failure");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _store.PromotePendingAsync(pendingTurnId, sessionId, entryId: null));

        SessionAttachmentRecord? stillPending = await _store.GetByIdAsync(pending.Id);

        Assert.NotNull(stillPending);

        Assert.Equal(SessionAttachmentState.Pending, stillPending!.State);

        Assert.True(File.Exists(pendingAbsolute));

        Assert.False(File.Exists(expectedBoundAbsolute));

    }

    [SkippableFact]
    public async Task PersistNewAsync_db_failure_deletes_orphan_bytes()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        string? capturedPath = null;

        _store!.AfterBytesCommittedBeforeDbForTesting = _ =>
        {

            capturedPath = Path.Combine(
                _attachmentsRoot,
                sessionId.ToString("N"),
                "orphan.txt",
                "v1",
                "orphan.txt");

            Assert.True(File.Exists(capturedPath));

            throw new InvalidOperationException("simulated insert failure");

        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _store.PersistNewAsync(
                sessionId,
                null,
                null,
                "orphan.txt",
                "orphan.txt",
                Encoding.UTF8.GetBytes("orphan-bytes"),
                "text/plain",
                SessionAttachmentKind.Text));

        Assert.NotNull(capturedPath);

        Assert.False(File.Exists(capturedPath));

    }

    [SkippableFact]
    public async Task Partial_unique_indexes_reject_duplicate_bound_and_pending_versions()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        string pendingTurnId = "turn-" + Guid.NewGuid().ToString("N");

        _ = await _store!.PersistNewAsync(
            sessionId,
            null,
            null,
            "dup.txt",
            "dup.txt",
            Encoding.UTF8.GetBytes("bound-v1"),
            "text/plain",
            SessionAttachmentKind.Text);

        _ = await _store.PersistNewAsync(
            null,
            pendingTurnId,
            null,
            "dup.txt",
            "dup.txt",
            Encoding.UTF8.GetBytes("pending-v1"),
            "text/plain",
            SessionAttachmentKind.Text);

        System.Data.Common.DbConnection connection = _db!.Database.GetDbConnection();

        if (connection.State != System.Data.ConnectionState.Open)
        {

            await connection.OpenAsync();

        }

        await using (System.Data.Common.DbCommand boundDup = connection.CreateCommand())
        {

            boundDup.CommandText =
                """
                INSERT INTO "SessionAttachments"
                    ("Id", "SessionId", "EntryId", "PendingTurnId", "State", "LogicalKey", "OriginalFileName",
                     "Version", "RelativePath", "ContentSha256", "MimeType", "ByteLength", "Kind", "CreatedAt")
                VALUES
                    (@id, @sessionId, NULL, NULL, 'Bound', 'dup.txt', 'dup.txt',
                     1, 'x', 'abc', 'text/plain', 1, 'Text', @createdAt)
                """;

            AddParam(boundDup, "@id", Guid.NewGuid().ToString());

            AddParam(boundDup, "@sessionId", sessionId.ToString());

            AddParam(boundDup, "@createdAt", DateTimeOffset.UtcNow.ToString("o"));

            Exception ex = await Assert.ThrowsAnyAsync<Exception>(() => boundDup.ExecuteNonQueryAsync());

            Assert.Contains("UNIQUE", ex.Message, StringComparison.OrdinalIgnoreCase);

        }

        await using (System.Data.Common.DbCommand pendingDup = connection.CreateCommand())
        {

            pendingDup.CommandText =
                """
                INSERT INTO "SessionAttachments"
                    ("Id", "SessionId", "EntryId", "PendingTurnId", "State", "LogicalKey", "OriginalFileName",
                     "Version", "RelativePath", "ContentSha256", "MimeType", "ByteLength", "Kind", "CreatedAt")
                VALUES
                    (@id, NULL, NULL, @pendingTurnId, 'Pending', 'dup.txt', 'dup.txt',
                     1, 'y', 'def', 'text/plain', 1, 'Text', @createdAt)
                """;

            AddParam(pendingDup, "@id", Guid.NewGuid().ToString());

            AddParam(pendingDup, "@pendingTurnId", pendingTurnId);

            AddParam(pendingDup, "@createdAt", DateTimeOffset.UtcNow.ToString("o"));

            Exception ex = await Assert.ThrowsAnyAsync<Exception>(() => pendingDup.ExecuteNonQueryAsync());

            Assert.Contains("UNIQUE", ex.Message, StringComparison.OrdinalIgnoreCase);

        }

    }

    [SkippableFact]
    public async Task PromotePending_and_DeleteStalePending_serialize_on_same_turn_gate()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string pendingTurnId = "turn-" + Guid.NewGuid().ToString("N");

        Guid sessionId = Guid.NewGuid();

        SessionAttachmentRecord pending = await _store!.PersistNewAsync(
            null,
            pendingTurnId,
            null,
            "race.txt",
            "race.txt",
            Encoding.UTF8.GetBytes("race-bytes"),
            "text/plain",
            SessionAttachmentKind.Text);

        await BackdateCreatedAtAsync(pending.Id, DateTimeOffset.UtcNow.AddHours(-48));

        // Separate DbContexts so promote and GC do not share a non-thread-safe context;
        // they still serialize on the process-wide named pending-turn gate.
        await using ArcanumDbContext promoteDb = _fixture.CreateContext(_dbPath);

        await using ArcanumDbContext gcDb = _fixture.CreateContext(_dbPath);

        SessionAttachmentStore promoteStore = new(
            promoteDb,
            Options.Create(_settings),
            _attachmentsRoot,
            CreateEncryptedBlobStore());

        SessionAttachmentStore gcStore = new(
            gcDb,
            Options.Create(_settings),
            _attachmentsRoot,
            CreateEncryptedBlobStore());

        Task promote;
        Task gc;

        using (IDisposable holdGate = await SessionAttachmentStore.AttachmentGates
                   .AcquireAsync(SessionAttachmentStore.PendingTurnGateKey(pendingTurnId)))
        {

            promote = promoteStore.PromotePendingAsync(pendingTurnId, sessionId, entryId: null);

            gc = gcStore.DeleteStalePendingAsync(TimeSpan.FromHours(24));

            await Task.Delay(150);

            Assert.False(promote.IsCompleted);

            Assert.False(gc.IsCompleted);

            Assert.True(SessionAttachmentStore.AttachmentGates.IsHeld(
                SessionAttachmentStore.PendingTurnGateKey(pendingTurnId)));

        }

        await Task.WhenAll(promote, gc);

        SessionAttachmentRecord? after = await _store.GetByIdAsync(pending.Id);

        // Exactly one winner: promoted (Bound) or GC'd (missing). Never a Bound row with missing bytes.
        if (after is null)
        {

            Assert.False(Directory.Exists(Path.Combine(_attachmentsRoot, "_pending", pendingTurnId)));

            return;

        }

        Assert.Equal(SessionAttachmentState.Bound, after.State);

        Assert.Equal(sessionId, after.SessionId);

        Assert.True(File.Exists(Path.Combine(_attachmentsRoot, after.RelativePath)));

        ReadOnlyMemory<byte> bytes = await _store.ReadBytesAsync(after);

        Assert.Equal(Encoding.UTF8.GetBytes("race-bytes"), bytes.ToArray());

    }

    [SkippableFact]
    public async Task DeleteStalePendingAsync_removes_aged_orphan_pending_directory()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string orphanTurnId = "orphan-" + Guid.NewGuid().ToString("N");

        string orphanDir = Path.Combine(_attachmentsRoot, "_pending", orphanTurnId);

        Directory.CreateDirectory(orphanDir);

        File.WriteAllText(Path.Combine(orphanDir, "leftover.bin"), "orphan");

        DateTime aged = DateTime.UtcNow.AddHours(-48);

        Directory.SetCreationTimeUtc(orphanDir, aged);

        Directory.SetLastWriteTimeUtc(orphanDir, aged);

        await _store!.DeleteStalePendingAsync(TimeSpan.FromHours(24));

        Assert.False(Directory.Exists(orphanDir));

    }

    [SkippableFact]
    public async Task DeleteStalePendingAsync_sweeps_orphan_files_without_rows()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        string orphanFile = Path.Combine(
            _attachmentsRoot,
            sessionId.ToString("N"),
            "ghost.txt",
            "v1",
            "ghost.txt");

        Directory.CreateDirectory(Path.GetDirectoryName(orphanFile)!);

        await File.WriteAllTextAsync(orphanFile, "no-row");

        await _store!.DeleteStalePendingAsync(TimeSpan.FromHours(24));

        Assert.False(File.Exists(orphanFile));

    }

    [SkippableFact]
    public async Task ListBoundForForkAsync_full_includes_null_entry_cutoff_excludes()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        Guid mappedEntryId = Guid.NewGuid();

        Guid otherEntryId = Guid.NewGuid();

        SessionAttachmentRecord unbound = await _store!.PersistNewAsync(
            sessionId,
            null,
            entryId: null,
            "unbound.txt",
            "unbound.txt",
            Encoding.UTF8.GetBytes("unbound"),
            "text/plain",
            SessionAttachmentKind.Text);

        SessionAttachmentRecord mapped = await _store.PersistNewAsync(
            sessionId,
            null,
            mappedEntryId,
            "mapped.txt",
            "mapped.txt",
            Encoding.UTF8.GetBytes("mapped"),
            "text/plain",
            SessionAttachmentKind.Text);

        SessionAttachmentRecord other = await _store.PersistNewAsync(
            sessionId,
            null,
            otherEntryId,
            "other.txt",
            "other.txt",
            Encoding.UTF8.GetBytes("other"),
            "text/plain",
            SessionAttachmentKind.Text);

        IReadOnlyList<SessionAttachmentRecord> fullFork = await _store.ListBoundForForkAsync(
            sessionId,
            copiedSourceEntryIds: null);

        Assert.Equal(3, fullFork.Count);

        Assert.Contains(fullFork, r => r.Id == unbound.Id && r.EntryId is null);

        Assert.Contains(fullFork, r => r.Id == mapped.Id);

        Assert.Contains(fullFork, r => r.Id == other.Id);

        HashSet<Guid> cutoff = [mappedEntryId];

        IReadOnlyList<SessionAttachmentRecord> cutoffFork = await _store.ListBoundForForkAsync(
            sessionId,
            cutoff);

        Assert.Single(cutoffFork);

        Assert.Equal(mapped.Id, cutoffFork[0].Id);

        Assert.DoesNotContain(cutoffFork, r => r.EntryId is null);

        Assert.DoesNotContain(cutoffFork, r => r.Id == other.Id);

    }

    [SkippableFact]
    public async Task CopyBytesForForkAsync_then_InsertForkRows_remaps_and_hash_matches()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sourceSessionId = Guid.NewGuid();

        Guid forkSessionId = Guid.NewGuid();

        Guid sourceEntryId = Guid.NewGuid();

        Guid forkEntryId = Guid.NewGuid();

        await EnsureSessionAsync(sourceSessionId, "fork-source");

        await EnsureSessionAsync(forkSessionId, "fork-dest");

        byte[] bytes = Encoding.UTF8.GetBytes("fork-bytes");

        SessionAttachmentRecord source = await _store!.PersistNewAsync(
            sourceSessionId,
            null,
            sourceEntryId,
            "notes.txt",
            "notes.txt",
            bytes,
            "text/plain",
            SessionAttachmentKind.Text);

        Guid newAttachmentId = Guid.NewGuid();

        SessionAttachmentForkCopyPlan plan = new(source, newAttachmentId, forkEntryId);

        try
        {

            await _store.CopyBytesForForkAsync(forkSessionId, [plan]);

            using IDisposable gate = await _store.AcquireSessionGateAsync(forkSessionId);

            await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx =
                await _db!.Database.BeginTransactionAsync();

            await _store.InsertForkRowsInAmbientTransactionAsync(forkSessionId, [plan]);

            await tx.CommitAsync();

            IReadOnlyList<SessionAttachmentRecord> forked = await _store.ListBoundAsync(forkSessionId);

            Assert.Single(forked);

            SessionAttachmentRecord forkRow = forked[0];

            Assert.Equal(newAttachmentId, forkRow.Id);

            Assert.Equal(forkSessionId, forkRow.SessionId);

            Assert.Equal(forkEntryId, forkRow.EntryId);

            Assert.Equal(source.LogicalKey, forkRow.LogicalKey);

            Assert.Equal(source.ContentSha256, forkRow.ContentSha256);

            Assert.NotEqual(source.Id, forkRow.Id);

            ReadOnlyMemory<byte> loaded = await _store.ReadBytesAsync(forkRow);

            Assert.Equal(bytes, loaded.ToArray());

            string hash = Convert.ToHexString(SHA256.HashData(loaded.Span));

            Assert.Equal(source.ContentSha256, hash, ignoreCase: true);

        }
        finally
        {

            Assert.True(_store.TryDeleteSessionDirectory(forkSessionId));

            Assert.False(Directory.Exists(Path.Combine(_attachmentsRoot, forkSessionId.ToString("N"))));

        }

    }

    [SkippableFact]

    public async Task InsertForkRowsInAmbientTransactionAsync_AcquiresWriterBeforeBlobValidation()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sourceSessionId = Guid.NewGuid();

        Guid forkSessionId = Guid.NewGuid();

        await EnsureSessionAsync(sourceSessionId, "fork-writer-source");

        await EnsureSessionAsync(forkSessionId, "fork-writer-destination");

        SessionAttachmentRecord source = await _store!.PersistNewAsync(
            sourceSessionId,
            pendingTurnId: null,
            entryId: null,
            "writer-lock.txt",
            "writer-lock.txt",
            Encoding.UTF8.GetBytes("writer lock bytes"),
            "text/plain",
            SessionAttachmentKind.Text);

        SessionAttachmentForkCopyPlan plan = new(
            source,
            Guid.NewGuid(),
            NewEntryId: null);

        await _store.CopyBytesForForkAsync(forkSessionId, [plan]);

        TaskCompletionSource validationReached = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource releaseValidation = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _store.AfterWriterLockBeforeBlobValidationForTesting = async cancellationToken =>
        {

            validationReached.TrySetResult();

            await releaseValidation.Task.WaitAsync(cancellationToken);

        };

        SqliteConnection connection =
            (SqliteConnection)_db!.Database.GetDbConnection();

        if (connection.State != System.Data.ConnectionState.Open)
        {

            await connection.OpenAsync();

        }

        await using SqliteTransaction sqliteTransaction =
            connection.BeginTransaction(deferred: true);

        await using IDbContextTransaction ambient =
            await _db.Database.UseTransactionAsync(sqliteTransaction)
            ?? throw new InvalidOperationException("The test could not attach its deferred transaction.");

        Task insert = _store.InsertForkRowsInAmbientTransactionAsync(
            forkSessionId,
            [plan]);

        try
        {

            await validationReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Exception? competingFailure;

            await using (ArcanumDbContext competingDb = _fixture.CreateContext(_dbPath))
            {

                SqliteConnection competingConnection =
                    (SqliteConnection)competingDb.Database.GetDbConnection();

                competingConnection.DefaultTimeout = 1;

                await competingConnection.OpenAsync();

                await using (SqliteCommand timeout = competingConnection.CreateCommand())
                {

                    timeout.CommandText = "PRAGMA busy_timeout = 1";

                    _ = await timeout.ExecuteNonQueryAsync();

                }

                try
                {

                    await using SqliteTransaction competingTransaction =
                        competingConnection.BeginTransaction(deferred: false);

                    competingFailure = null;

                }
                catch (SqliteException ex)
                {

                    competingFailure = ex;

                }

            }

            SqliteException busy = Assert.IsType<SqliteException>(competingFailure);

            Assert.Equal(5, busy.SqliteErrorCode);

        }
        finally
        {

            releaseValidation.TrySetResult();

            try
            {

                await insert;

            }
            finally
            {

                _store.AfterWriterLockBeforeBlobValidationForTesting = null;

            }

        }

        await ambient.CommitAsync();

    }

    [SkippableFact]
    public async Task DeleteRowsForSession_under_gate_then_TryDeleteSessionDirectory_clears_all()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        await EnsureSessionAsync(sessionId, "purge-me");

        SessionAttachmentRecord record = await _store!.PersistNewAsync(
            sessionId,
            null,
            null,
            "purge.txt",
            "purge.txt",
            Encoding.UTF8.GetBytes("purge-bytes"),
            "text/plain",
            SessionAttachmentKind.Text);

        string sessionDir = Path.Combine(_attachmentsRoot, sessionId.ToString("N"));

        Assert.True(Directory.Exists(sessionDir));

        Assert.True(File.Exists(Path.Combine(_attachmentsRoot, record.RelativePath)));

        using (IDisposable gate = await _store.AcquireSessionGateAsync(sessionId))
        {

            await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx =
                await _db!.Database.BeginTransactionAsync();

            await _store.DeleteRowsForSessionInAmbientTransactionAsync(sessionId);

            await tx.CommitAsync();

        }

        Assert.True(_store.TryDeleteSessionDirectory(sessionId));

        Assert.Null(await _store.GetByIdAsync(record.Id));

        Assert.Empty(await _store.ListBoundAsync(sessionId));

        Assert.False(Directory.Exists(sessionDir));

    }

    [SkippableFact]
    public async Task ClearEntryIdsInAmbientTransactionAsync_nulls_EntryId()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        Guid entryId = Guid.NewGuid();

        await EnsureSessionAsync(sessionId, "clear-entry");

        SessionAttachmentRecord record = await _store!.PersistNewAsync(
            sessionId,
            null,
            entryId,
            "linked.txt",
            "linked.txt",
            Encoding.UTF8.GetBytes("linked"),
            "text/plain",
            SessionAttachmentKind.Text);

        Assert.Equal(entryId, record.EntryId);

        using IDisposable gate = await _store.AcquireSessionGateAsync(sessionId);

        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx =
            await _db!.Database.BeginTransactionAsync();

        await _store.ClearEntryIdsInAmbientTransactionAsync(sessionId, [entryId]);

        await tx.CommitAsync();

        SessionAttachmentRecord? cleared = await _store.GetByIdAsync(record.Id);

        Assert.NotNull(cleared);

        Assert.Null(cleared!.EntryId);

        Assert.Equal(sessionId, cleared.SessionId);

    }

    [SkippableFact]
    public async Task ReconcileAsync_deletes_row_whose_file_is_missing()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        await EnsureSessionAsync(sessionId, "reconcile-missing");

        SessionAttachmentRecord record = await _store!.PersistNewAsync(
            sessionId,
            null,
            null,
            "missing.txt",
            "missing.txt",
            Encoding.UTF8.GetBytes("will-delete-file"),
            "text/plain",
            SessionAttachmentKind.Text);

        string absolute = Path.Combine(_attachmentsRoot, record.RelativePath);

        Assert.True(File.Exists(absolute));

        File.Delete(absolute);

        Assert.False(File.Exists(absolute));

        await _store.ReconcileAsync(TimeSpan.FromHours(24));

        Assert.Null(await _store.GetByIdAsync(record.Id));

    }

    private async Task EnsureSessionAsync(Guid sessionId, string title)
    {

        DateTimeOffset now = DateTimeOffset.UtcNow;

        _db!.Sessions.Add(new Session
        {
            Id = sessionId,
            Title = title,
            Status = "active",
            CreatedAt = now,
            UpdatedAt = now,
        });

        await _db.SaveChangesAsync();

    }

    private sealed class TestWorkspaceContext(string workspacePath) : IHostWorkspaceContext
    {
        public string? WorkspacePath { get; } = workspacePath;
    }

    private static void AddParam(System.Data.Common.DbCommand cmd, string name, object value)
    {

        System.Data.Common.DbParameter parameter = cmd.CreateParameter();

        parameter.ParameterName = name;

        parameter.Value = value;

        cmd.Parameters.Add(parameter);

    }

    private async Task BackdateCreatedAtAsync(Guid id, DateTimeOffset createdAt)
    {

        System.Data.Common.DbConnection connection = _db!.Database.GetDbConnection();

        if (connection.State != System.Data.ConnectionState.Open)
        {

            await connection.OpenAsync();

        }

        await using System.Data.Common.DbCommand cmd = connection.CreateCommand();

        cmd.CommandText =
            """
            UPDATE "SessionAttachments"
            SET "CreatedAt" = @createdAt
            WHERE "Id" = @id
            """;

        System.Data.Common.DbParameter idParam = cmd.CreateParameter();

        idParam.ParameterName = "@id";

        idParam.Value = id.ToString();

        cmd.Parameters.Add(idParam);

        System.Data.Common.DbParameter createdParam = cmd.CreateParameter();

        createdParam.ParameterName = "@createdAt";

        createdParam.Value = createdAt.ToString("o", System.Globalization.CultureInfo.InvariantCulture);

        cmd.Parameters.Add(createdParam);

        _ = await cmd.ExecuteNonQueryAsync();

    }

}
