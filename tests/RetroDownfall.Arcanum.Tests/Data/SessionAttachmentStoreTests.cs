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
using RetroDownfall.Arcanum.Tests.Support;

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

    [SkippableTheory]
    [InlineData(" notes", 2)]
    [InlineData(".notes", null)]
    [InlineData("notes", null)]
    public async Task Rv266_exact_and_alias_selection_use_one_database_snapshot(string selector, int? version)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid session = Guid.NewGuid();

        await EnsureSessionAsync(session, "single-key-snapshot");

        SessionAttachmentRecord old = await _store!.PersistNewAsync(session, null, null, "old", "old.txt",
            "legacy"u8.ToArray(), "text/plain", SessionAttachmentKind.Text);

        await _db!.Database.ExecuteSqlRawAsync("UPDATE SessionAttachments SET LogicalKey = {0} WHERE Id = {1}", " notes", GrimoireEntitySql.Format(old.Id));

        SessionAttachmentRecord canonical = await _store.PersistNewAsync(session, null, null, "notes", "new.txt",
            "canonical"u8.ToArray(), "text/plain", SessionAttachmentKind.Text);

        using SqliteStatementTrace trace = new((SqliteConnection)_db.Database.GetDbConnection());

        SessionAttachmentRecord? selected = await _store.GetByLogicalAsync(session, selector, version);

        Assert.Equal(selector == " notes" ? (Guid?)null : canonical.Id, selected?.Id);

        Assert.Single(trace.Statements, statement => statement.Contains("FROM \"SessionAttachments\"", StringComparison.OrdinalIgnoreCase));
    }

    [SkippableTheory]
    [InlineData(". notes", "notes")]
    [InlineData("x\u00a0.", "x")]
    [InlineData(".\u00a0. notes .", "notes")]
    [InlineData("notes.txt", "notes.txt")]
    public async Task Rv266_new_attachment_key_round_trips_index_latest_and_version(string input, string expected)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid session = Guid.NewGuid();

        await EnsureSessionAsync(session, "key-roundtrip");

        SessionAttachmentRecord row = await _store!.PersistNewAsync(session, null, null, input, input,
            "stable key"u8.ToArray(), "text/plain", SessionAttachmentKind.Text);

        Assert.Equal(expected, row.LogicalKey);

        Assert.Equal(row.LogicalKey, Assert.Single(await _store.BuildIndexAsync(session, 10)).LogicalKey);

        Assert.Equal(row.Id, (await _store.GetByLogicalAsync(session, row.LogicalKey, null))?.Id);

        Assert.Equal(row.Id, (await _store.GetByLogicalAsync(session, row.LogicalKey, 1))?.Id);

        Assert.Equal("stable key"u8.ToArray(), (await _store.ReadBytesAsync(row)).ToArray());
    }

    [SkippableTheory]
    [InlineData(" notes", "notes")]
    [InlineData("x\u00a0", "x")]
    [InlineData("\u00a0", "notes")]
    [InlineData("\u2003", "notes")]
    public async Task Rv266_legacy_exact_key_wins_without_renaming_or_cross_version_fallback(string legacy, string canonical)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid session = Guid.NewGuid();

        await EnsureSessionAsync(session, "legacy-key");

        SessionAttachmentRecord old = await _store!.PersistNewAsync(session, null, null, "old", "old.txt",
            "legacy bytes"u8.ToArray(), "text/plain", SessionAttachmentKind.Text);

        await _db!.Database.ExecuteSqlRawAsync("UPDATE SessionAttachments SET LogicalKey = {0} WHERE Id = {1}", legacy, GrimoireEntitySql.Format(old.Id));

        await _store.PersistNewAsync(session, null, null, canonical, "new.txt", "new v1"u8.ToArray(), "text/plain", SessionAttachmentKind.Text);

        SessionAttachmentRecord newer = await _store.PersistNewAsync(session, null, null, canonical, "new.txt", "new v2"u8.ToArray(), "text/plain", SessionAttachmentKind.Text);

        Assert.Equal(2, newer.Version);

        Assert.Equal(old.Id, (await _store.GetByLogicalAsync(session, legacy, null))?.Id);

        Assert.Equal(old.Id, (await _store.GetByLogicalAsync(session, legacy, 1))?.Id);

        Assert.Null(await _store.GetByLogicalAsync(session, legacy, 2));

        Assert.Equal(newer.Id, (await _store.GetByLogicalAsync(session, canonical, null))?.Id);

        Assert.Equal(newer.Id, (await _store.GetByLogicalAsync(session, "." + canonical, null))?.Id);

        Assert.Null(await _store.GetByLogicalAsync(Guid.NewGuid(), legacy, null));

        Assert.Equal(legacy, (await _store.GetByIdAsync(old.Id))?.LogicalKey);

        Assert.Equal("legacy bytes"u8.ToArray(), (await _store.ReadBytesAsync(old)).ToArray());
    }

    [SkippableFact]

    public async Task PersistNewAsync_uses_streaming_encrypted_writer()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InstrumentedEncryptedBlobStore blobs = new(CreateEncryptedBlobStore())
        {
            RejectWholeStreamWrite = true,
        };

        SessionAttachmentStore store = new(
            _db!,
            Options.Create(_settings),
            _attachmentsRoot,
            blobs);

        byte[] bytes = Enumerable.Range(0, 513)
            .Select(static value => (byte)(value % 251))
            .ToArray();

        SessionAttachmentRecord record = await store.PersistNewAsync(
            Guid.NewGuid(),
            pendingTurnId: null,
            entryId: null,
            logicalNameHint: "streamed.bin",
            originalFileName: "streamed.bin",
            bytes,
            mimeType: "application/octet-stream",
            SessionAttachmentKind.Binary);

        Assert.Equal(0, blobs.WholeStreamWriteCallCount);

        Assert.Equal(1, blobs.CreateWriterCallCount);

        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), record.ContentSha256);

        Assert.Equal(bytes, (await store.ReadBytesAsync(record)).ToArray());
    }

    [SkippableFact]

    public async Task PersistNewAsync_streaming_cancellation_leaves_no_row_or_blob()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using CancellationTokenSource cancellation = new();

        InstrumentedEncryptedBlobStore blobs = new(CreateEncryptedBlobStore())
        {
            CancelWriterAfterWriteCall = 1,

            WriterCancellation = cancellation,
        };

        SessionAttachmentStore store = new(
            _db!,
            Options.Create(_settings),
            _attachmentsRoot,
            blobs);

        Guid sessionId = Guid.NewGuid();

        byte[] bytes = new byte[(64 * 1024) + 1];

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.PersistNewAsync(
                sessionId,
                pendingTurnId: null,
                entryId: null,
                logicalNameHint: "cancelled.bin",
                originalFileName: "cancelled.bin",
                bytes,
                mimeType: "application/octet-stream",
                SessionAttachmentKind.Binary,
                cancellation.Token));

        Assert.Null(await store.GetByLogicalAsync(sessionId, "cancelled.bin", version: null));

        Assert.Empty(Directory.EnumerateFiles(
            _attachmentsRoot,
            "*",
            SearchOption.AllDirectories));
    }

    [SkippableFact]

    public async Task ReadBytesAsync_uses_incremental_reads_without_CopyTo_buffer_duplication()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InstrumentedEncryptedBlobStore blobs = new(CreateEncryptedBlobStore());

        SessionAttachmentStore store = new(
            _db!,
            Options.Create(_settings),
            _attachmentsRoot,
            blobs);

        byte[] bytes = Encoding.UTF8.GetBytes(
            "read directly into the one returned attachment buffer");

        SessionAttachmentRecord record = await store.PersistNewAsync(
            Guid.NewGuid(),
            pendingTurnId: null,
            entryId: null,
            logicalNameHint: "direct-read.txt",
            originalFileName: "direct-read.txt",
            bytes,
            mimeType: "text/plain",
            SessionAttachmentKind.Text);

        blobs.RejectCopyToRead = true;

        Assert.Equal(bytes, (await store.ReadBytesAsync(record)).ToArray());
    }

    [SkippableFact]

    public async Task ReadBoundPagesAsync_keyset_pages_every_version_in_stable_order()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        foreach (string logicalKey in new[] { "charlie.txt", "alpha.txt", "bravo.txt", "echo.txt", "delta.txt" })
        {
            _ = await _store!.PersistNewAsync(
                sessionId,
                pendingTurnId: null,
                entryId: null,
                logicalKey,
                logicalKey,
                Encoding.UTF8.GetBytes(logicalKey),
                mimeType: "text/plain",
                SessionAttachmentKind.Text);
        }

        List<IReadOnlyList<SessionAttachmentRecord>> pages = [];

        await foreach (IReadOnlyList<SessionAttachmentRecord> page in _store!.ReadBoundPagesAsync(
            sessionId,
            pageSize: 2))
        {
            pages.Add(page);
        }

        Assert.Equal([2, 2, 1], pages.Select(static page => page.Count));

        Assert.Equal(
            ["alpha.txt", "bravo.txt", "charlie.txt", "delta.txt", "echo.txt"],
            pages.SelectMany(static page => page).Select(static row => row.LogicalKey));

        Assert.Equal(5, (await _store!.ListBoundAsync(sessionId)).Count);
    }

    [SkippableFact]

    public async Task ListLatestBoundAsync_returns_only_latest_version_per_logical_key()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        _ = await _store!.PersistNewAsync(
            sessionId,
            null,
            null,
            "alpha.txt",
            "alpha.txt",
            "alpha-v1"u8.ToArray(),
            "text/plain",
            SessionAttachmentKind.Text);

        SessionAttachmentRecord alphaLatest = await _store.PersistNewAsync(
            sessionId,
            null,
            null,
            "alpha.txt",
            "alpha.txt",
            "alpha-v2"u8.ToArray(),
            "text/plain",
            SessionAttachmentKind.Text);

        SessionAttachmentRecord bravo = await _store.PersistNewAsync(
            sessionId,
            null,
            null,
            "bravo.txt",
            "bravo.txt",
            "bravo-v1"u8.ToArray(),
            "text/plain",
            SessionAttachmentKind.Text);

        IReadOnlyList<SessionAttachmentRecord> latest = await _store
            .ListLatestBoundAsync(sessionId);

        Assert.Equal([alphaLatest.Id, bravo.Id], latest.Select(static row => row.Id));

        Assert.Equal([2, 1], latest.Select(static row => row.Version));

        IReadOnlyList<SessionAttachmentRecord> selected = await _store
            .ListLatestBoundByLogicalKeysAsync(
                sessionId,
                ["bravo.txt", "alpha.txt", "bravo.txt", "missing.txt"]);

        Assert.Equal(
            [bravo.Id, alphaLatest.Id],
            selected.Select(static row => row.Id));
    }

    [SkippableFact]
    public async Task ListLatestBoundByLogicalKeysAsync_pages_queries_and_preserves_selected_order()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SessionAttachmentStore store = _store!;

        Guid sessionId = Guid.NewGuid();
        List<string> logicalKeys = [];

        for (int index = 0; index < 130; index++)
        {
            string logicalKey = $"attachment-{index:D3}.txt";
            logicalKeys.Add(logicalKey);

            _ = await store.PersistNewAsync(
                sessionId,
                null,
                null,
                logicalKey,
                logicalKey,
                Encoding.UTF8.GetBytes(logicalKey),
                "text/plain",
                SessionAttachmentKind.Text);
        }

        string[] requested = logicalKeys
            .AsEnumerable()
            .Reverse()
            .Append(logicalKeys[^1])
            .ToArray();
        IReadOnlyList<SessionAttachmentRecord> selected = await store
            .ListLatestBoundByLogicalKeysAsync(
                sessionId,
                requested);

        Assert.Equal(logicalKeys.Count, selected.Count);
        Assert.Equal(
            logicalKeys.AsEnumerable().Reverse(),
            selected.Select(static row => row.LogicalKey));
        Assert.Equal(
            2,
            store.LatestLogicalKeyQueryPageCountForTesting);
    }

    [SkippableFact]

    public async Task BuildIndexAsync_pushes_logical_limit_before_full_record_paging()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SessionAttachmentStore store = _store!;

        Guid sessionId = Guid.NewGuid();

        foreach (string content in new[] { "alpha-v1", "alpha-v2" })
        {
            _ = await store.PersistNewAsync(
                sessionId,
                null,
                null,
                "alpha.txt",
                "alpha.txt",
                Encoding.UTF8.GetBytes(content),
                "text/plain",
                SessionAttachmentKind.Text);
        }

        foreach (string logicalKey in new[] { "bravo.txt", "charlie.txt" })
        {
            _ = await store.PersistNewAsync(
                sessionId,
                null,
                null,
                logicalKey,
                logicalKey,
                Encoding.UTF8.GetBytes(logicalKey),
                "text/plain",
                SessionAttachmentKind.Text);
        }

        IReadOnlyList<SessionAttachmentIndexItem> index = await store
            .BuildIndexAsync(sessionId, maxItems: 1);

        SessionAttachmentIndexItem alpha = Assert.Single(index);

        Assert.Equal("alpha.txt", alpha.LogicalKey);

        Assert.Equal([1, 2], alpha.Versions);

        Assert.Equal(0, store.BoundRecordPageReadCountForTesting);
    }

    [SkippableFact]
    public async Task BuildIndexAsync_bounds_one_version_page_without_losing_history()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SessionAttachmentStore store = _store!;

        Guid sessionId = Guid.NewGuid();

        for (int version = 1; version <= 260; version++)
        {
            _ = await store.PersistNewAsync(
                sessionId,
                null,
                null,
                "history.txt",
                "history.txt",
                Encoding.UTF8.GetBytes($"version-{version}"),
                "text/plain",
                SessionAttachmentKind.Text);
        }

        IReadOnlyList<SessionAttachmentIndexItem> index = await store
            .BuildIndexAsync(sessionId, maxItems: 1);

        SessionAttachmentIndexItem history = Assert.Single(index);

        Assert.True(
            history.Versions.Count <= 256,
            $"One prompt-index item accumulated {history.Versions.Count} versions.");
        Assert.Equal(
            Enumerable.Range(1, 256),
            history.Versions);
        Assert.True(history.HasMoreVersions);
        Assert.Equal(257, history.NextVersion);

        SessionAttachmentRecord last =
            Assert.IsType<SessionAttachmentRecord>(
                await store.GetByLogicalAsync(
                    sessionId,
                    "history.txt",
                    version: 260));

        Assert.Equal(260, last.Version);
    }

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
            AttachmentSourceResolver resolver = new(new TestWorkspaceContext(workspace), new EmptyWorkspaceRegistry());
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

            AttachmentSourceResolver resolver = new(new TestWorkspaceContext(workspace), new EmptyWorkspaceRegistry());

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
            AttachmentSourceResolver resolver = new(new TestWorkspaceContext(workspace), new EmptyWorkspaceRegistry());
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
            AttachmentSourceResolver resolver = new(new TestWorkspaceContext(workspace), new EmptyWorkspaceRegistry());
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

        Assert.Equal(sessionId.ToString("N") + "/" + record.Id.ToString("N"), record.RelativePath);

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
    public async Task Attachment_integrity_upload_collision_preserves_existing_destination()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InstrumentedEncryptedBlobStore blobs = new(CreateEncryptedBlobStore()) { OccupyDestination = true };

        SessionAttachmentStore store = new(_db!, Options.Create(_settings), _attachmentsRoot, blobs);

        await Assert.ThrowsAsync<IOException>(() => store.PersistNewAsync(
            Guid.NewGuid(), null, null, "collision", "notes.txt", "new snapshot"u8.ToArray(), "text/plain", SessionAttachmentKind.Text));

        string occupied = Assert.Single(Directory.EnumerateFiles(_attachmentsRoot, "*", SearchOption.AllDirectories));

        Assert.Equal("existing destination", await File.ReadAllTextAsync(occupied));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Attachment_integrity_reads_legacy_plaintext_and_metadata_free_envelopes(bool encrypted)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        byte[] bytes = "legacy snapshot"u8.ToArray();

        SessionAttachmentRecord record = await _store!.PersistNewAsync(
            Guid.NewGuid(), null, null, "legacy", "legacy.txt", bytes, "text/plain", SessionAttachmentKind.Text);

        string absolute = Path.Combine(_attachmentsRoot, record.RelativePath);

        if (encrypted)
        {
            using MemoryStream input = new(bytes);

            _ = await CreateEncryptedBlobStore().WriteAsync(absolute, input, EncryptedBlobPurpose.SessionAttachment);
        }
        else
        {
            await File.WriteAllBytesAsync(absolute, bytes);

            record = record with { EncryptionVersion = 0, EncryptionKeyId = null };
        }

        Assert.Equal(bytes, (await _store.ReadBytesAsync(record)).ToArray());

        await using Stream stream = await _store.OpenReadAsync(record);

        using MemoryStream copied = new();

        await stream.CopyToAsync(copied);

        Assert.Equal(bytes, copied.ToArray());
    }

    [SkippableTheory]
    [InlineData(0)]
    [InlineData(193)]
    public async Task Attachment_integrity_zero_length_read_does_not_finalize_digest(int length)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        byte[] bytes = Enumerable.Repeat((byte)9, length).ToArray();

        SessionAttachmentRecord record = await _store!.PersistNewAsync(
            Guid.NewGuid(), null, null, "zero-read", "read.txt", bytes, "text/plain", SessionAttachmentKind.Text);

        await using Stream stream = await _store.OpenReadAsync(record);

        Assert.Equal(0, stream.Read(Span<byte>.Empty));

        Assert.Equal(0, await stream.ReadAsync(Memory<byte>.Empty));

        using MemoryStream copied = new();

        await stream.CopyToAsync(copied);

        Assert.Equal(bytes, copied.ToArray());

        Assert.Equal(length, stream.Position);
    }

    [SkippableFact]
    public async Task Attachment_integrity_failed_fork_verification_removes_only_its_owned_copy()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SessionAttachmentRecord source = await _store!.PersistNewAsync(
            Guid.NewGuid(), null, null, "source", "source.txt", "source bytes"u8.ToArray(), "text/plain", SessionAttachmentKind.Text);

        string sourcePath = Path.Combine(_attachmentsRoot, source.RelativePath);

        byte[] corrupt = await File.ReadAllBytesAsync(sourcePath);

        corrupt[^1] ^= 1;

        await File.WriteAllBytesAsync(sourcePath, corrupt);

        Guid forkSession = Guid.NewGuid();

        SessionAttachmentForkCopyPlan plan = new(source, Guid.NewGuid(), null);

        string destination = Path.Combine(_attachmentsRoot, forkSession.ToString("N"), plan.NewAttachmentId.ToString("N"));

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        string unrelated = Path.Combine(Path.GetDirectoryName(destination)!, "unrelated");

        await File.WriteAllTextAsync(unrelated, "retain this file");

        await Assert.ThrowsAnyAsync<CryptographicException>(() => _store.CopyBytesForForkAsync(forkSession, [plan]));

        Assert.False(File.Exists(destination), "A failed fork left its rejected ciphertext behind.");

        Assert.Equal("retain this file", await File.ReadAllTextAsync(unrelated));

        Assert.Equal(corrupt, await File.ReadAllBytesAsync(sourcePath));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Attachment_integrity_copy_collision_preserves_existing_destination(bool fork)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid destinationSession = Guid.NewGuid();

        string turn = "collision-turn";

        SessionAttachmentRecord source = await _store!.PersistNewAsync(
            fork ? Guid.NewGuid() : null, fork ? null : turn, null,
            "notes", "notes.txt", "source bytes"u8.ToArray(), "text/plain", SessionAttachmentKind.Text);

        SessionAttachmentForkCopyPlan plan = new(source, Guid.NewGuid(), null);

        string destination = Path.Combine(_attachmentsRoot, destinationSession.ToString("N"), (fork ? plan.NewAttachmentId : source.Id).ToString("N"));

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        await File.WriteAllTextAsync(destination, "existing destination");

        await Assert.ThrowsAsync<IOException>(() => fork
            ? _store.CopyBytesForForkAsync(destinationSession, [plan])
            : _store.PromotePendingAsync(turn, destinationSession, null));

        Assert.Equal("existing destination", await File.ReadAllTextAsync(destination));

        Assert.Equal("source bytes", Encoding.UTF8.GetString((await _store.ReadBytesAsync(source)).Span));
    }

    [SkippableFact]
    public async Task Attachment_integrity_sweep_preserves_upload_waiting_to_insert_its_row()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        await EnsureSessionAsync(sessionId, "upload-in-flight");

        await using ArcanumDbContext sweepDb = _fixture.CreateContext(_dbPath);

        SessionAttachmentStore sweepStore = new(sweepDb, Options.Create(_settings), _attachmentsRoot, CreateEncryptedBlobStore());

        _store!.AfterBytesCommittedBeforeDbForTesting = async token =>
        {
            string published = Assert.Single(Directory.EnumerateFiles(_attachmentsRoot, "*", SearchOption.AllDirectories));

            File.SetLastWriteTimeUtc(published, DateTime.UtcNow.AddHours(-2));

            await sweepStore.DeleteStalePendingAsync(TimeSpan.FromHours(24), token);

            Assert.True(File.Exists(published), "The sweep removed a published upload before its row could commit.");
        };

        SessionAttachmentRecord record = await _store.PersistNewAsync(
            sessionId, null, null, "upload", "upload.txt", "in flight"u8.ToArray(), "text/plain", SessionAttachmentKind.Text);

        Assert.Equal("in flight", Encoding.UTF8.GetString((await _store.ReadBytesAsync(record)).Span));
    }

    [SkippableFact]
    public async Task Attachment_integrity_sweep_holds_session_gate_until_destructive_check_finishes()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        await EnsureSessionAsync(sessionId, "sweep-before-fork");

        string orphan = Path.Combine(_attachmentsRoot, sessionId.ToString("N"), Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(Path.GetDirectoryName(orphan)!);

        await File.WriteAllTextAsync(orphan, "abandoned bytes");

        File.SetLastWriteTimeUtc(orphan, DateTime.UtcNow.AddHours(-2));

        Task<IDisposable>? forkGate = null;

        _store!.BeforeOrphanFileDeleteForTesting = (path, _) =>
        {
            Assert.Equal(orphan, path);

            forkGate = _store.AcquireSessionGateAsync(sessionId);

            Assert.False(forkGate.IsCompleted, "A new fork acquired ownership while the sweep was about to unlink its candidate.");

            return Task.CompletedTask;
        };

        try
        {
            await _store.DeleteStalePendingAsync(TimeSpan.FromHours(24));

            Assert.False(File.Exists(orphan));

            Assert.NotNull(forkGate);

            using IDisposable acquired = await forkGate.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            _store.BeforeOrphanFileDeleteForTesting = null;

            if (forkGate is { IsCompletedSuccessfully: true })
            {
                (await forkGate).Dispose();
            }
        }
    }

    [SkippableFact]
    public async Task Attachment_integrity_sweep_preserves_fork_until_ambient_transaction_commits()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sourceSession = Guid.NewGuid();

        Guid forkSession = Guid.NewGuid();

        await EnsureSessionAsync(sourceSession, "fork-source");

        await EnsureSessionAsync(forkSession, "fork-destination");

        SessionAttachmentRecord source = await _store!.PersistNewAsync(
            sourceSession, null, null, "source", "source.txt", "fork bytes"u8.ToArray(), "text/plain", SessionAttachmentKind.Text);

        SessionAttachmentForkCopyPlan plan = new(source, Guid.NewGuid(), null);

        await using ArcanumDbContext sweepDb = _fixture.CreateContext(_dbPath);

        SessionAttachmentStore sweepStore = new(sweepDb, Options.Create(_settings), _attachmentsRoot, CreateEncryptedBlobStore());

        using (await _store.AcquireSessionGateAsync(forkSession))
        {
            await _store.CopyBytesForForkAsync(forkSession, [plan]);

            string copy = Path.Combine(_attachmentsRoot, forkSession.ToString("N"), plan.NewAttachmentId.ToString("N"));

            File.SetLastWriteTimeUtc(copy, DateTime.UtcNow.AddHours(-2));

            await sweepStore.DeleteStalePendingAsync(TimeSpan.FromHours(24));

            Assert.True(File.Exists(copy), "The fork session gate must protect its unpublished copies.");

            await using IDbContextTransaction transaction = await _db!.Database.BeginTransactionAsync();

            await _store.InsertForkRowsInAmbientTransactionAsync(forkSession, [plan]);

            await sweepStore.DeleteStalePendingAsync(TimeSpan.FromHours(24));

            Assert.True(File.Exists(copy), "The fork remains protected until the ambient transaction commits.");

            await transaction.CommitAsync();
        }

        await sweepStore.DeleteStalePendingAsync(TimeSpan.FromHours(24));

        SessionAttachmentRecord forked = Assert.IsType<SessionAttachmentRecord>(await _store.GetByIdAsync(plan.NewAttachmentId));

        Assert.Equal("fork bytes", Encoding.UTF8.GetString((await _store.ReadBytesAsync(forked)).Span));
    }

    [SkippableFact]
    public async Task Attachment_integrity_pending_alias_preserves_in_flight_upload()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string existingDirectory = Path.Combine(_attachmentsRoot, "_pending", "TURN");

        Directory.CreateDirectory(existingDirectory);

        Skip.IfNot(Directory.Exists(Path.Combine(_attachmentsRoot, "_pending", "turn")), "This volume distinguishes directory case.");

        await using ArcanumDbContext sweepDb = _fixture.CreateContext(_dbPath);

        SessionAttachmentStore sweepStore = new(sweepDb, Options.Create(_settings), _attachmentsRoot, CreateEncryptedBlobStore());

        _store!.AfterBytesCommittedBeforeDbForTesting = async token =>
        {
            string published = Assert.Single(Directory.EnumerateFiles(_attachmentsRoot, "*", SearchOption.AllDirectories));

            File.SetLastWriteTimeUtc(published, DateTime.UtcNow.AddHours(-2));

            await sweepStore.DeleteStalePendingAsync(TimeSpan.FromHours(24), token);

            Assert.True(File.Exists(published), "A differently spelled pending directory hid the active publication.");
        };

        SessionAttachmentRecord record = await _store.PersistNewAsync(
            null, "turn", null, "upload", "upload.txt", "in flight"u8.ToArray(), "text/plain", SessionAttachmentKind.Text);

        Assert.Equal("in flight", Encoding.UTF8.GetString((await _store.ReadBytesAsync(record)).Span));
    }

    [SkippableTheory]
    [InlineData("README.md", "README.md", "readme.md", "readme.md")]
    [InlineData("Report", "a.txt", "report", "b.txt")]
    [InlineData("café", "a.txt", "cafe\u0301", "b.txt")]
    public async Task Attachment_integrity_distinct_logical_names_keep_distinct_snapshots(
        string firstKey, string firstName, string secondKey, string secondName)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        await EnsureSessionAsync(sessionId, "distinct-snapshots");

        SessionAttachmentRecord first = await _store!.PersistNewAsync(
            sessionId, null, null, firstKey, firstName, "FIRST-note"u8.ToArray(), "text/plain", SessionAttachmentKind.Text);

        SessionAttachmentRecord second = await _store.PersistNewAsync(
            sessionId, null, null, secondKey, secondName, "OTHER-note"u8.ToArray(), "text/plain", SessionAttachmentKind.Text);

        Assert.Equal("FIRST-note", Encoding.UTF8.GetString((await _store.ReadBytesAsync(first)).Span));

        foreach (string file in Directory.EnumerateFiles(_attachmentsRoot, "*", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddHours(-2));
        }

        await _store.ReconcileAsync(TimeSpan.FromHours(24));

        Assert.NotNull(await _store.GetByIdAsync(first.Id));

        Assert.NotNull(await _store.GetByIdAsync(second.Id));

        Assert.Equal("FIRST-note", Encoding.UTF8.GetString((await _store.ReadBytesAsync(first)).Span));

        Assert.Equal("OTHER-note", Encoding.UTF8.GetString((await _store.ReadBytesAsync(second)).Span));
    }

    [SkippableTheory]
    [InlineData("materialized")]
    [InlineData("sync")]
    [InlineData("async")]
    [InlineData("sync-array")]
    [InlineData("async-array")]
    public async Task Attachment_integrity_rejects_equal_length_valid_envelope_substitution(string readMode)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        SessionAttachmentRecord first = await _store!.PersistNewAsync(
            sessionId, null, null, "first", "first.txt", new byte[193], "text/plain", SessionAttachmentKind.Text);

        SessionAttachmentRecord other = await _store.PersistNewAsync(
            sessionId, null, null, "other", "other.txt", Enumerable.Repeat((byte)42, 193).ToArray(), "text/plain", SessionAttachmentKind.Text);

        File.Copy(Path.Combine(_attachmentsRoot, other.RelativePath), Path.Combine(_attachmentsRoot, first.RelativePath), overwrite: true);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            if (readMode == "materialized")
            {
                _ = await _store.ReadBytesAsync(first);

                return;
            }

            await using Stream stream = await _store.OpenReadAsync(first);

            byte[] page = new byte[31];

            Assert.Equal(0, stream.Read(Span<byte>.Empty));

            Assert.Equal(0, await stream.ReadAsync(Memory<byte>.Empty));

            int read;

            do
            {
                read = readMode switch
                {
                    "sync" => stream.Read(page.AsSpan()),
                    "sync-array" => stream.Read(page, 0, page.Length),
                    "async-array" => await stream.ReadAsync(page, 0, page.Length),
                    _ => await stream.ReadAsync(page.AsMemory()),
                };
            }

            while (read != 0);
        });
    }

    [SkippableTheory]
    [InlineData("Report", "report", false)]
    [InlineData("Report", "report", true)]
    [InlineData("café", "cafe\u0301", false)]
    [InlineData("café", "cafe\u0301", true)]
    public async Task Attachment_integrity_reconcile_preserves_legacy_filesystem_aliases(string diskKey, string rowKey, bool afterSnapshot)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        await EnsureSessionAsync(sessionId, "legacy-alias");

        SessionAttachmentRecord? record = null;

        string? orphan = null;

        async Task CreateLegacyAsync()
        {
            record = await _store!.PersistNewAsync(
                sessionId, null, null, "legacy", "notes.txt", "legacy bytes"u8.ToArray(), "text/plain", SessionAttachmentKind.Text);

            string diskRelative = sessionId.ToString("N") + "/" + diskKey + "/v1/notes.txt";

            string rowRelative = sessionId.ToString("N") + "/" + rowKey + "/v1/notes.txt";

            string diskPath = Path.Combine(_attachmentsRoot, diskRelative);

            Directory.CreateDirectory(Path.GetDirectoryName(diskPath)!);

            File.Move(Path.Combine(_attachmentsRoot, record.RelativePath), diskPath);

            Skip.IfNot(File.Exists(Path.Combine(_attachmentsRoot, rowRelative)), "This volume distinguishes the supplied path spellings.");

            await _db!.Database.OpenConnectionAsync();

            using (System.Data.Common.DbCommand command = _db.Database.GetDbConnection().CreateCommand())
            {
                command.CommandText = "UPDATE SessionAttachments SET RelativePath = @path WHERE Id = @id";

                command.Parameters.Add(new SqliteParameter("@path", rowRelative));

                command.Parameters.Add(new SqliteParameter("@id", record.Id.ToString().ToUpperInvariant()));

                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }

            File.SetLastWriteTimeUtc(diskPath, DateTime.UtcNow.AddHours(-2));

            orphan = Path.Combine(Path.GetDirectoryName(diskPath)!, "orphan.txt");

            await File.WriteAllTextAsync(orphan, "orphan");

            File.SetLastWriteTimeUtc(orphan, DateTime.UtcNow.AddHours(-2));
        }

        if (afterSnapshot)
        {
            _store!.AfterOrphanPathSnapshotForTesting = async _ =>
            {
                _store.AfterOrphanPathSnapshotForTesting = null;

                await CreateLegacyAsync();
            };
        }
        else
        {
            await CreateLegacyAsync();
        }

        await _store!.ReconcileAsync(TimeSpan.FromHours(24));

        SessionAttachmentRecord reloaded = Assert.IsType<SessionAttachmentRecord>(await _store.GetByIdAsync(record!.Id));

        Assert.Equal(sessionId.ToString("N") + "/" + rowKey + "/v1/notes.txt", reloaded.RelativePath);

        Assert.Equal("legacy bytes", Encoding.UTF8.GetString((await _store.ReadBytesAsync(reloaded)).Span));

        Assert.False(File.Exists(orphan));
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

    private sealed class InstrumentedEncryptedBlobStore(IEncryptedBlobStore inner)
        : IEncryptedBlobStore
    {
        public bool OccupyDestination { get; init; }

        public bool RejectWholeStreamWrite { get; init; }

        public bool RejectCopyToRead { get; set; }

        public int? CancelWriterAfterWriteCall { get; init; }

        public CancellationTokenSource? WriterCancellation { get; init; }

        public int WholeStreamWriteCallCount { get; private set; }

        public int CreateWriterCallCount { get; private set; }

        public Task<EncryptedBlobDescriptor> WriteAsync(
            string destinationPath,
            Stream plaintext,
            EncryptedBlobPurpose purpose,
            ReadOnlyMemory<byte> authenticatedMetadata = default,
            long? plaintextLength = null,
            CancellationToken cancellationToken = default)
        {
            WholeStreamWriteCallCount++;

            if (RejectWholeStreamWrite)
            {
                throw new InvalidOperationException(
                    "The whole-stream encrypted write path must not be used.");
            }

            return inner.WriteAsync(
                destinationPath,
                plaintext,
                purpose,
                authenticatedMetadata,
                plaintextLength,
                cancellationToken);
        }

        public async Task<Stream> OpenReadAsync(
            string path,
            EncryptedBlobPurpose purpose,
            CancellationToken cancellationToken = default)
        {
            Stream stream = await inner
                .OpenReadAsync(path, purpose, cancellationToken)
                .ConfigureAwait(false);

            return RejectCopyToRead
                ? new CopyRejectingReadStream(stream)
                : stream;
        }

        public Task<EncryptedBlobWriter> CreateWriterAsync(
            string destinationPath,
            EncryptedBlobPurpose purpose,
            ReadOnlyMemory<byte> authenticatedMetadata = default,
            CancellationToken cancellationToken = default) =>
            CreateWriterCoreAsync(destinationPath, purpose, authenticatedMetadata, false, cancellationToken);

        public Task<EncryptedBlobWriter> CreateNewWriterAsync(
            string destinationPath,
            EncryptedBlobPurpose purpose,
            ReadOnlyMemory<byte> authenticatedMetadata = default,
            CancellationToken cancellationToken = default) =>
            CreateWriterCoreAsync(destinationPath, purpose, authenticatedMetadata, true, cancellationToken);

        private async Task<EncryptedBlobWriter> CreateWriterCoreAsync(
            string destinationPath,
            EncryptedBlobPurpose purpose,
            ReadOnlyMemory<byte> authenticatedMetadata,
            bool createNew,
            CancellationToken cancellationToken)
        {
            CreateWriterCallCount++;

            EncryptedBlobWriter writer = createNew
                ? await inner.CreateNewWriterAsync(destinationPath, purpose, authenticatedMetadata, cancellationToken)
                    .ConfigureAwait(false)
                : await inner.CreateWriterAsync(destinationPath, purpose, authenticatedMetadata, cancellationToken)
                    .ConfigureAwait(false);

            if (OccupyDestination)
            {
                await File.WriteAllTextAsync(destinationPath, "existing destination", cancellationToken);
            }

            return CancelWriterAfterWriteCall is { } cancelAfter
                && WriterCancellation is { } writerCancellation
                    ? new CancelingEncryptedBlobWriter(
                        writer,
                        cancelAfter,
                        writerCancellation)
                    : writer;
        }

        public Task<EncryptedBlobPlaintextInspection> InspectAuthenticatedAsync(
            string path,
            EncryptedBlobPurpose purpose,
            CancellationToken cancellationToken = default) =>
            inner.InspectAuthenticatedAsync(path, purpose, cancellationToken);

        public Task<EncryptedBlobDescriptor> InspectAsync(
            string path,
            EncryptedBlobPurpose purpose,
            bool verifyAllChunks,
            CancellationToken cancellationToken = default) =>
            inner.InspectAsync(
                path,
                purpose,
                verifyAllChunks,
                cancellationToken);

        public bool HasEnvelope(string path) => inner.HasEnvelope(path);
    }

    private sealed class CancelingEncryptedBlobWriter(
        EncryptedBlobWriter inner,
        int cancelAfterWriteCall,
        CancellationTokenSource cancellation) : EncryptedBlobWriter
    {
        private int _writeCallCount;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => inner.CanWrite;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;

            set => throw new NotSupportedException();
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);

            _writeCallCount++;

            if (_writeCallCount == cancelAfterWriteCall)
            {
                cancellation.Cancel();
            }
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override Task<EncryptedBlobDescriptor> CompleteAsync(
            CancellationToken cancellationToken = default) =>
            inner.CompleteAsync(cancellationToken);

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);

            GC.SuppressFinalize(this);
        }
    }

    private sealed class CopyRejectingReadStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;

            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override Task CopyToAsync(
            Stream destination,
            int bufferSize,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "ReadBytesAsync must not create a second whole-payload CopyTo buffer.");

        public override long Seek(long offset, SeekOrigin origin) =>
            inner.Seek(offset, origin);

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);

            GC.SuppressFinalize(this);
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

        await Assert.ThrowsAsync<SessionAttachmentReferenceException>(() =>
            _store.ValidateReferencesAsync(sessionA, [b.Id]));

        await _store.ValidateReferencesAsync(sessionA, [a.Id, a.Id]);

        await _store.ValidateReferencesAsync(sessionA, [a.Id]);
    }

    [SkippableFact]
    public async Task PersistNewAsync_over_the_session_byte_limit_throws_AttachmentLimitExceededException()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SessionAttachmentStore store = CreateStore(new ArcanumSettings());

        store.SessionByteLimitForTesting = 10;

        Guid sessionId = Guid.NewGuid();

        _ = await store.PersistNewAsync(
            sessionId,
            null,
            null,
            "first.txt",
            "first.txt",
            Encoding.UTF8.GetBytes("123456"),
            "text/plain",
            SessionAttachmentKind.Text);

        // The typed refusal is what the HTTP routes report as Attachment.LimitExceeded; it still is an
        // InvalidOperationException for every caller that predates the type.
        AttachmentLimitExceededException refused = await Assert.ThrowsAsync<AttachmentLimitExceededException>(() =>
            store.PersistNewAsync(
                sessionId,
                null,
                null,
                "second.txt",
                "second.txt",
                Encoding.UTF8.GetBytes("123456"),
                "text/plain",
                SessionAttachmentKind.Text));

        Assert.IsAssignableFrom<InvalidOperationException>(refused);

        Assert.Null(await store.GetByLogicalAsync(sessionId, "second.txt", version: null));
    }

    [SkippableFact]
    public async Task PersistNewAsync_accepts_versions_beyond_the_former_count_ceiling()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SessionAttachmentStore store = CreateStore(new ArcanumSettings());

        Guid sessionId = Guid.NewGuid();

        const int formerDefaultMaxVersions = 20;

        for (int version = 0; version < formerDefaultMaxVersions; version++)
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

        SessionAttachmentRecord next = await store.PersistNewAsync(
                sessionId,
                null,
                null,
                "cap.txt",
                "cap.txt",
                Encoding.UTF8.GetBytes("version-beyond-former-ceiling"),
                "text/plain",
                SessionAttachmentKind.Text);

        Assert.Equal(formerDefaultMaxVersions + 1, next.Version);
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
            pending.Id.ToString("N"));

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
            capturedPath = Assert.Single(Directory.EnumerateFiles(
                Path.Combine(_attachmentsRoot, sessionId.ToString("N")), "*", SearchOption.AllDirectories));

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

            // Both identities in the spelling the store writes: the partial unique index this case is
            // about compares SessionId exactly, so a minority-spelled duplicate would collide with
            // nothing and the case would pass by not reaching the index at all.
            AddParam(boundDup, "@id", Guid.NewGuid().ToString().ToUpperInvariant());

            AddParam(boundDup, "@sessionId", sessionId.ToString().ToUpperInvariant());

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

            AddParam(pendingDup, "@id", Guid.NewGuid().ToString().ToUpperInvariant());

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

        // Backdated so the sweep's verdict does not ride on clock granularity. The threshold is read
        // from the filesystem's own clock via a probe written after this file, and a probe landing in
        // the same tick makes this orphan look no older than the threshold -- which the sweep then
        // spares, correctly, because that is indistinguishable from a write still in flight. An hour
        // is unambiguous on any filesystem granularity.
        File.SetLastWriteTimeUtc(orphanFile, DateTime.UtcNow.AddHours(-1));

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

        await EnsureSessionAsync(sessionId, "fork-list-source");

        DateTimeOffset now = DateTimeOffset.UtcNow;

        _db!.Entries.AddRange(
            new Entry
            {
                Id = mappedEntryId,
                SessionId = sessionId,
                Role = MessageRole.User,
                Content = "mapped",
                ModelUsed = "test",
                CreatedAt = now,
                Sequence = 1,
            },
            new Entry
            {
                Id = otherEntryId,
                SessionId = sessionId,
                Role = MessageRole.User,
                Content = "other",
                ModelUsed = "test",
                CreatedAt = now.AddSeconds(1),
                Sequence = 2,
            });

        await _db.SaveChangesAsync();

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

        List<SessionAttachmentRecord> pagedFullFork = [];

        await foreach (IReadOnlyList<SessionAttachmentRecord> page in _store
                           .ReadBoundForForkPagesAsync(
                               sessionId,
                               maximumSourceEntrySequence: 2,
                               includeEntrylessAttachments: true))
        {
            pagedFullFork.AddRange(page);
        }

        Assert.Equal(3, pagedFullFork.Count);

        List<SessionAttachmentRecord> pagedCutoffFork = [];

        await foreach (IReadOnlyList<SessionAttachmentRecord> page in _store
                           .ReadBoundForForkPagesAsync(
                               sessionId,
                               maximumSourceEntrySequence: 1,
                               includeEntrylessAttachments: false))
        {
            pagedCutoffFork.AddRange(page);
        }

        SessionAttachmentRecord cutoffRecord = Assert.Single(pagedCutoffFork);

        Assert.Equal(mapped.Id, cutoffRecord.Id);
    }

    [SkippableTheory]
    [InlineData("entry")]
    [InlineData("metadata")]
    [InlineData("foreign")]
    [InlineData("missing")]
    [InlineData("disposed")]
    [InlineData("complete")]
    [InlineData("unscoped-clone")]
    [InlineData("scoped-equivalent")]
    [InlineData("unscoped-original")]
    public async Task Fork_copy_authority_is_bound_to_verified_plan_fork_and_live_scope(string scenario)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sourceId = Guid.NewGuid();

        Guid forkId = Guid.NewGuid();

        await EnsureSessionAsync(sourceId, "authority-source");

        await EnsureSessionAsync(forkId, "authority-fork");

        SessionAttachmentRecord source = await _store!.PersistNewAsync(
            sourceId, null, null, "proof", "proof.txt", "verified copy"u8.ToArray(),
            "text/plain", SessionAttachmentKind.Text);

        SessionAttachmentForkCopyPlan plan = new(source, Guid.NewGuid(), null);

        bool unscoped = scenario.StartsWith("unscoped-", StringComparison.Ordinal);

        using SessionAttachmentForkScope? scope = unscoped ? null : _store.BeginForkCopyScope(forkId);

        if (scenario != "missing")
        {
            await _store.CopyBytesForForkAsync(forkId, [plan]);
        }

        string copy = Path.Combine(_attachmentsRoot, forkId.ToString("N"), plan.NewAttachmentId.ToString("N"));

        if (scenario == "disposed")
        {
            byte[] ciphertext = await File.ReadAllBytesAsync(copy);

            scope!.Dispose();

            Assert.False(File.Exists(copy));

            await File.WriteAllBytesAsync(copy, ciphertext);
        }
        else if (scenario == "complete")
        {
            scope!.Complete();
        }
        else if (scenario is "missing" or "foreign")
        {
            if (scenario == "foreign")
            {
                forkId = Guid.NewGuid();

                await EnsureSessionAsync(forkId, "other-fork");
            }

            copy = Path.Combine(_attachmentsRoot, forkId.ToString("N"), plan.NewAttachmentId.ToString("N"));

            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);

            File.Copy(Path.Combine(_attachmentsRoot, source.RelativePath), copy);
        }

        SessionAttachmentForkCopyPlan insertion = scenario switch
        {
            "entry" => plan with { NewEntryId = Guid.NewGuid() },
            "metadata" => plan with { Source = source with { OriginalFileName = "altered.txt" } },
            "scoped-equivalent" or "unscoped-clone" => plan with { Source = source with { } },
            _ => plan,
        };

        await using IDbContextTransaction transaction = await _db!.Database.BeginTransactionAsync();

        if (scenario is "scoped-equivalent" or "unscoped-original")
        {
            await _store.InsertForkRowsInAmbientTransactionAsync(forkId, [insertion]);

            await transaction.CommitAsync();

            scope?.Complete();

            Assert.Single(await _store.ListBoundAsync(forkId));
        }
        else
        {
            InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
                () => _store.InsertForkRowsInAmbientTransactionAsync(forkId, [insertion]));

            Assert.Contains("copy-time authority", refused.Message, StringComparison.Ordinal);

            Assert.Empty(await _store.ListBoundAsync(forkId));

            await transaction.RollbackAsync();
        }

        Assert.Equal("verified copy"u8.ToArray(), (await _store.ReadBytesAsync(source)).ToArray());
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

    /// <summary>
    /// A fork's copy that is unlinked after it was made is refused when its row is inserted, and the source is left
    /// exactly as it was.
    /// </summary>
    /// <remarks>
    /// The Session gate protects a production fork from the orphan sweep, while commit-time identity validation
    /// independently refuses externally removed bytes. This pins that second boundary: the fork fails, no row
    /// is written for it, and the source attachment still reads back.
    /// </remarks>
    [SkippableFact]
    public async Task InsertForkRowsInAmbientTransactionAsync_refuses_a_copy_unlinked_after_it_was_made_and_leaves_the_source_intact()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sourceSessionId = Guid.NewGuid();

        Guid forkSessionId = Guid.NewGuid();

        await EnsureSessionAsync(sourceSessionId, "fork-lost-source");

        await EnsureSessionAsync(forkSessionId, "fork-lost-destination");

        byte[] bytes = Encoding.UTF8.GetBytes("fork lost copy bytes");

        SessionAttachmentRecord source = await _store!.PersistNewAsync(
            sourceSessionId,
            pendingTurnId: null,
            entryId: null,
            "lost-copy.txt",
            "lost-copy.txt",
            bytes,
            "text/plain",
            SessionAttachmentKind.Text);

        SessionAttachmentForkCopyPlan plan = new(source, Guid.NewGuid(), NewEntryId: null);

        await _store.CopyBytesForForkAsync(forkSessionId, [plan]);

        string copy = Assert.Single(
            Directory.EnumerateFiles(
                Path.Combine(_attachmentsRoot, forkSessionId.ToString("N")),
                "*",
                SearchOption.AllDirectories));

        // What the orphan sweep does to a copy it takes for unreferenced.
        File.Delete(copy);

        try
        {
            using IDisposable gate = await _store.AcquireSessionGateAsync(forkSessionId);

            await using IDbContextTransaction transaction = await _db!.Database.BeginTransactionAsync();

            IOException refused = await Assert.ThrowsAsync<IOException>(
                () => _store.InsertForkRowsInAmbientTransactionAsync(forkSessionId, [plan]));

            Assert.Contains("missing or replaced bytes", refused.Message, StringComparison.Ordinal);

            await transaction.RollbackAsync();

            Assert.Empty(await _store.ListBoundAsync(forkSessionId));

            SessionAttachmentRecord intact = Assert.Single(await _store.ListBoundAsync(sourceSessionId));

            Assert.Equal(bytes, (await _store.ReadBytesAsync(intact)).ToArray());
        }
        finally
        {
            _ = _store.TryDeleteSessionDirectory(forkSessionId);
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
    public async Task TryDeleteSessionDirectory_clears_the_directory_and_the_bytes_a_persisted_attachment_wrote()
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

        Assert.True(_store.TryDeleteSessionDirectory(sessionId));

        Assert.False(Directory.Exists(sessionDir));

        Assert.False(File.Exists(Path.Combine(_attachmentsRoot, record.RelativePath)));
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

    [SkippableTheory]
    [InlineData("notes.tmp")]
    [InlineData("data.tmp.csv")]
    [InlineData("Report.TMP.md")]
    [InlineData("notes.txt")]
    public async Task ReconcileAsync_preserves_bound_temporary_looking_names(string name)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        await EnsureSessionAsync(sessionId, "temporary-looking-name");

        byte[] bytes = Encoding.UTF8.GetBytes("bound snapshot content");

        SessionAttachmentRecord record = await _store!.PersistNewAsync(
            sessionId, null, null, name, name, bytes, "text/plain", SessionAttachmentKind.Text);

        string originalPath = Path.Combine(_attachmentsRoot, record.RelativePath);

        string legacyRelative = sessionId.ToString("N") + "/legacy/v1/" + name;

        string absolute = Path.Combine(_attachmentsRoot, legacyRelative);

        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);

        File.Move(originalPath, absolute);

        await _db!.Database.OpenConnectionAsync();

        using (System.Data.Common.DbCommand command = _db.Database.GetDbConnection().CreateCommand())
        {
            command.CommandText = "UPDATE SessionAttachments SET RelativePath = @path WHERE Id = @id";

            command.Parameters.Add(new SqliteParameter("@path", legacyRelative));

            command.Parameters.Add(new SqliteParameter("@id", record.Id.ToString().ToUpperInvariant()));

            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        record = record with { RelativePath = legacyRelative };

        File.SetLastWriteTimeUtc(absolute, DateTime.UtcNow.AddHours(-2));

        string orphan = Path.Combine(Path.GetDirectoryName(absolute)!, ".abandoned.tmp." + Guid.NewGuid().ToString("N"));

        await File.WriteAllTextAsync(orphan, "abandoned staging");

        File.SetLastWriteTimeUtc(orphan, DateTime.UtcNow.AddHours(-2));

        await _store.ReconcileAsync(TimeSpan.FromHours(24));

        Assert.NotNull(await _store.GetByIdAsync(record.Id));

        Assert.Equal(bytes, (await _store.ReadBytesAsync(record)).ToArray());

        Assert.False(File.Exists(orphan));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rv260_reconcile_retains_rows_when_the_parent_directory_cannot_be_admitted(bool linkedParent)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid session = Guid.NewGuid();

        await EnsureSessionAsync(session, "unavailable-parent");

        byte[] plaintext = "parent recovery"u8.ToArray();

        SessionAttachmentRecord record = await _store!.PersistNewAsync(
            session, null, null, "parent.txt", "parent.txt", plaintext, "text/plain", SessionAttachmentKind.Text);

        string parent = Path.GetDirectoryName(Path.Combine(_attachmentsRoot, record.RelativePath))!;

        string parked = _attachmentsRoot + ".parked-parent-" + session.ToString("N");

        Directory.Move(parent, parked);

        try
        {
            if (linkedParent)
            {
                try
                {
                    Directory.CreateSymbolicLink(parent, parked);
                }
                catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException)
                {
                    Skip.If(true, "Symbolic links are unavailable on this runner.");
                }
            }

            await _store.ReconcileAsync(TimeSpan.FromHours(24));

            Assert.NotNull(await _store.GetByIdAsync(record.Id));
        }
        finally
        {
            if (linkedParent && new DirectoryInfo(parent).LinkTarget is not null)
            {
                Directory.Delete(parent);
            }

            Directory.Move(parked, parent);
        }

        Assert.Equal(plaintext, (await _store.ReadBytesAsync(record)).ToArray());
    }

    [SkippableFact]
    public async Task Rv260_reconcile_retains_row_when_native_parent_metadata_is_unavailable()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid session = Guid.NewGuid();

        await EnsureSessionAsync(session, "unreadable-parent");

        byte[] plaintext = "metadata recovery"u8.ToArray();

        SessionAttachmentRecord record = await _store!.PersistNewAsync(
            session, null, null, "metadata.txt", "metadata.txt", plaintext, "text/plain", SessionAttachmentKind.Text);

        string absolute = Path.Combine(_attachmentsRoot, record.RelativePath);

        string parked = _attachmentsRoot + ".parked-metadata-" + record.Id.ToString("N");

        File.Move(absolute, parked);

        FileHandleIdentityInterop.TryGetHandleMetadataForTests = _ => null;

        try
        {
            await _store.ReconcileAsync(TimeSpan.FromHours(24));

            Assert.NotNull(await _store.GetByIdAsync(record.Id));
        }
        finally
        {
            FileHandleIdentityInterop.TryGetHandleMetadataForTests = null;

            File.Move(parked, absolute);
        }

        Assert.Equal(plaintext, (await _store.ReadBytesAsync(record)).ToArray());
    }

    [SkippableTheory]
    [InlineData("leaf-directory")]
    [InlineData("dangling-link")]
    [InlineData("outside-link")]
    [InlineData("escaping-row")]
    public async Task Rv260_reconcile_retains_recovery_metadata_when_file_absence_is_unproved(string scenario)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid session = Guid.NewGuid();

        await EnsureSessionAsync(session, "uncertain-attachment");

        byte[] plaintext = "recoverable encrypted attachment"u8.ToArray();

        SessionAttachmentRecord record = await _store!.PersistNewAsync(
            session, null, null, "recoverable.txt", "recoverable.txt", plaintext, "text/plain", SessionAttachmentKind.Text);

        string absolute = Path.Combine(_attachmentsRoot, record.RelativePath);

        string parked = _attachmentsRoot + ".parked-" + record.Id.ToString("N");

        if (scenario == "escaping-row")
        {
            await _db!.Database.ExecuteSqlRawAsync(
                "UPDATE SessionAttachments SET RelativePath = {0} WHERE Id = {1}", "../outside-unknown", GrimoireEntitySql.Format(record.Id));
        }
        else
        {
            File.Move(absolute, parked);
        }

        try
        {
            if (scenario == "leaf-directory")
            {
                Directory.CreateDirectory(absolute);
            }
            else if (scenario is "dangling-link" or "outside-link")
            {
                try
                {
                    File.CreateSymbolicLink(absolute, scenario == "outside-link" ? parked : parked + ".absent");
                }
                catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException)
                {
                    Skip.If(true, "Symbolic links are unavailable on this runner.");
                }
            }

            for (int attempt = 0; attempt < 2; attempt++)
            {
                await _store.ReconcileAsync(TimeSpan.FromHours(24));

                Assert.NotNull(await _store.GetByIdAsync(record.Id));
            }
        }
        finally
        {
            if (scenario == "escaping-row")
            {
                await _db!.Database.ExecuteSqlRawAsync(
                    "UPDATE SessionAttachments SET RelativePath = {0} WHERE Id = {1}", record.RelativePath, GrimoireEntitySql.Format(record.Id));
            }
            else
            {
                if (scenario == "leaf-directory")
                {
                    Directory.Delete(absolute);
                }
                else
                {
                    File.Delete(absolute);
                }

                File.Move(parked, absolute);
            }
        }

        Assert.NotNull(await _store.GetByIdAsync(record.Id));

        Assert.Equal(plaintext, (await _store.ReadBytesAsync(record)).ToArray());
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rv260_reconcile_rechecks_after_the_session_gate_and_honors_cancellation(bool cancel)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid session = Guid.NewGuid();

        await EnsureSessionAsync(session, "gate-restoration");

        byte[] plaintext = "gated recovery"u8.ToArray();

        SessionAttachmentRecord record = await _store!.PersistNewAsync(
            session, null, null, "gated.txt", "gated.txt", plaintext, "text/plain", SessionAttachmentKind.Text);

        string absolute = Path.Combine(_attachmentsRoot, record.RelativePath);

        string parked = _attachmentsRoot + ".gate-" + record.Id.ToString("N");

        File.Move(absolute, parked);

        using CancellationTokenSource cancellation = new();

        IDisposable? gate = await _store.AcquireSessionGateAsync(session);

        bool snapshotReached = false;

        _store.AfterMissingFileSnapshotForTesting = _ =>
        {
            snapshotReached = true;

            return Task.CompletedTask;
        };

        Task reconcile = _store.ReconcileAsync(TimeSpan.FromHours(24), cancellation.Token);

        try
        {
            Assert.True(snapshotReached);

            Assert.False(reconcile.IsCompleted);

            if (cancel)
            {
                cancellation.Cancel();

                OperationCanceledException failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reconcile);

                Assert.Equal(cancellation.Token, failure.CancellationToken);

                File.Move(parked, absolute);
            }
            else
            {
                File.Move(parked, absolute);

                gate.Dispose();

                gate = null;

                await reconcile;
            }

            Assert.NotNull(await _store.GetByIdAsync(record.Id));
        }
        finally
        {
            _store.AfterMissingFileSnapshotForTesting = null;

            cancellation.Cancel();

            gate?.Dispose();

            if (File.Exists(parked))
            {
                File.Move(parked, absolute);
            }
        }

        Assert.Equal(plaintext, (await _store.ReadBytesAsync(record)).ToArray());
    }

    [SkippableTheory]
    [InlineData("parent-within-root")]
    [InlineData("parent-outside-root")]
    [InlineData("parent-replaced")]
    [InlineData("root-replaced")]
    [InlineData("root-replaced-parent-returned")]
    public async Task Rv260_reconcile_requires_the_current_exact_root_and_parent_position(string scenario)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid session = Guid.NewGuid();

        await EnsureSessionAsync(session, "native-parent-position");

        byte[] plaintext = "position recovery"u8.ToArray();

        SessionAttachmentRecord record = await _store!.PersistNewAsync(
            session, null, null, "position.txt", "position.txt", plaintext, "text/plain", SessionAttachmentKind.Text);

        string absolute = Path.Combine(_attachmentsRoot, record.RelativePath);

        string parent = Path.GetDirectoryName(absolute)!;

        string parked = _attachmentsRoot + ".position-bytes-" + record.Id.ToString("N");

        string moved = scenario == "parent-within-root" ? parent + "-moved" : _attachmentsRoot + ".position-moved-" + record.Id.ToString("N");

        File.Move(absolute, parked);

        bool enabled = false;

        bool changed = false;

        _store.AfterMissingFileSnapshotForTesting = _ =>
        {
            enabled = true;

            return Task.CompletedTask;
        };

        Func<Microsoft.Win32.SafeHandles.SafeFileHandle, FileHandleMetadata?>? probe = null;

        probe = handle =>
        {
            FileHandleIdentityInterop.TryGetHandleMetadataForTests = null;

            try
            {
                FileHandleMetadata? metadata = FileHandleIdentityInterop.TryGetHandleMetadata(handle, out FileHandleMetadata actual) ? actual : null;

                if (enabled && !changed
                    && FileHandleIdentityInterop.TryGetHandleKernelPath(handle, out string? kernel)
                    && Path.GetFileName(kernel) == session.ToString("N"))
                {
                    changed = true;

                    if (scenario.StartsWith("root-replaced", StringComparison.Ordinal))
                    {
                        Directory.Move(_attachmentsRoot, moved);

                        Directory.CreateDirectory(_attachmentsRoot);

                        if (scenario == "root-replaced-parent-returned")
                        {
                            Directory.Move(Path.Combine(moved, session.ToString("N")), parent);
                        }
                        else
                        {
                            Directory.CreateDirectory(parent);
                        }
                    }
                    else
                    {
                        Directory.Move(parent, moved);

                        if (scenario == "parent-replaced")
                        {
                            Directory.CreateDirectory(parent);
                        }
                    }
                }

                return metadata;
            }
            finally
            {
                FileHandleIdentityInterop.TryGetHandleMetadataForTests = probe;
            }
        };

        FileHandleIdentityInterop.TryGetHandleMetadataForTests = probe;

        try
        {
            await _store.ReconcileAsync(TimeSpan.FromHours(24));

            Assert.True(changed);

            Assert.NotNull(await _store.GetByIdAsync(record.Id));
        }
        finally
        {
            FileHandleIdentityInterop.TryGetHandleMetadataForTests = null;

            _store.AfterMissingFileSnapshotForTesting = null;

            if (changed)
            {
                string original = scenario.StartsWith("root-replaced", StringComparison.Ordinal) ? _attachmentsRoot : parent;

                if (scenario == "root-replaced-parent-returned")
                {
                    Directory.Move(parent, Path.Combine(moved, session.ToString("N")));
                }

                if (Directory.Exists(original))
                {
                    Directory.Delete(original, recursive: true);
                }

                Directory.Move(moved, original);
            }

            File.Move(parked, absolute);
        }

        Assert.Equal(plaintext, (await _store.ReadBytesAsync(record)).ToArray());
    }

    [SkippableFact]
    public async Task Rv260_reconcile_reproves_absence_after_a_busy_sql_attempt()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid session = Guid.NewGuid();

        await EnsureSessionAsync(session, "retry-restoration");

        byte[] plaintext = "retry recovery"u8.ToArray();

        SessionAttachmentRecord record = await _store!.PersistNewAsync(
            session, null, null, "retry.txt", "retry.txt", plaintext, "text/plain", SessionAttachmentKind.Text);

        string absolute = Path.Combine(_attachmentsRoot, record.RelativePath);

        string parked = _attachmentsRoot + ".retry-" + record.Id.ToString("N");

        File.Move(absolute, parked);

        await _db!.Database.OpenConnectionAsync();

        SqliteConnection connection = (SqliteConnection)_db.Database.GetDbConnection();

        connection.DefaultTimeout = 1;

        await using ArcanumDbContext writerDb = _fixture.CreateContext(_dbPath);

        await writerDb.Database.OpenConnectionAsync();

        await using SqliteTransaction writer = ((SqliteConnection)writerDb.Database.GetDbConnection()).BeginTransaction(deferred: false);

        bool writerReleased = false;

        int deletes = 0;

        int parentProbes = 0;

        bool enabled = false;

        _store.AfterMissingFileSnapshotForTesting = _ =>
        {
            enabled = true;

            return Task.CompletedTask;
        };

        Func<Microsoft.Win32.SafeHandles.SafeFileHandle, FileHandleMetadata?>? probe = null;

        probe = handle =>
        {
            FileHandleIdentityInterop.TryGetHandleMetadataForTests = null;

            try
            {
                if (enabled
                    && FileHandleIdentityInterop.TryGetHandleKernelPath(handle, out string? kernel)
                    && Path.GetFileName(kernel) == session.ToString("N"))
                {
                    parentProbes++;
                }

                return FileHandleIdentityInterop.TryGetHandleMetadata(handle, out FileHandleMetadata metadata) ? metadata : null;
            }
            finally
            {
                FileHandleIdentityInterop.TryGetHandleMetadataForTests = probe;
            }
        };

        FileHandleIdentityInterop.TryGetHandleMetadataForTests = probe;

        SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.strdelegate_trace)((_, statement) =>
        {
            if (statement.StartsWith("DELETE FROM \"SessionAttachments\"", StringComparison.Ordinal))
            {
                deletes++;

                if (deletes == 1)
                {
                    File.Move(parked, absolute);
                }
                else if (!writerReleased)
                {
                    writer.Rollback();

                    writerReleased = true;
                }
            }
        }), null);

        try
        {
            await _store.ReconcileAsync(TimeSpan.FromHours(24)).WaitAsync(TimeSpan.FromSeconds(8));

            Assert.Equal(1, deletes);

            Assert.True(parentProbes >= 2);

            Assert.NotNull(await _store.GetByIdAsync(record.Id));
        }
        finally
        {
            FileHandleIdentityInterop.TryGetHandleMetadataForTests = null;

            _store.AfterMissingFileSnapshotForTesting = null;

            SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.strdelegate_trace)null!, null);

            if (!writerReleased)
            {
                await writer.RollbackAsync();
            }

            if (File.Exists(parked))
            {
                File.Move(parked, absolute);
            }
        }

        Assert.Equal(plaintext, (await _store.ReadBytesAsync(record)).ToArray());
    }

    [SkippableTheory]
    [InlineData("../private-selector-canary")]
    [InlineData("session/\0private-selector-canary")]
    [InlineData("session//missing")]
    [InlineData("session/./missing")]
    [InlineData("session/leaf:private-selector-canary")]
    [InlineData("session/leaf\\private-selector-canary")]
    public async Task Rv260_reconcile_retains_malformed_selectors_with_content_free_warning(string selector)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid session = Guid.NewGuid();

        await EnsureSessionAsync(session, "selector-recovery");

        selector = selector.Replace("session/", session.ToString("N") + "/", StringComparison.Ordinal);

        byte[] plaintext = "selector recovery"u8.ToArray();

        SessionAttachmentRecord record = await _store!.PersistNewAsync(
            session, null, null, "selector.txt", "selector.txt", plaintext, "text/plain", SessionAttachmentKind.Text);

        SessionAttachmentRecord healthy = await _store.PersistNewAsync(
            session, null, null, "healthy.txt", "healthy.txt", "healthy"u8.ToArray(), "text/plain", SessionAttachmentKind.Text);

        File.Delete(Path.Combine(_attachmentsRoot, healthy.RelativePath));

        await _db!.Database.ExecuteSqlRawAsync(
            "UPDATE SessionAttachments SET RelativePath = {0} WHERE Id = {1}", selector, GrimoireEntitySql.Format(record.Id));

        string absolute = Path.Combine(_attachmentsRoot, record.RelativePath);

        string parked = _attachmentsRoot + ".selector-" + record.Id.ToString("N");

        File.Move(absolute, parked);

        TestCapturingLogger<SessionAttachmentStore> logger = new();

        SessionAttachmentStore store = new(_db, Options.Create(_settings), _attachmentsRoot, CreateEncryptedBlobStore(), logger);

        try
        {
            if (selector.Contains('\0'))
            {
                InvalidOperationException refusal = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => store.ReadBytesAsync(record with { RelativePath = selector }));

                Assert.Null(refusal.InnerException);

                Assert.DoesNotContain("private-selector-canary", refusal.ToString(), StringComparison.Ordinal);
            }

            await store.ReconcileAsync(TimeSpan.FromHours(24));

            Assert.NotNull(await store.GetByIdAsync(record.Id));

            Assert.Null(await store.GetByIdAsync(healthy.Id));

            TestLogEntry warning = Assert.Single(logger.Entries, entry => entry.Message.Contains("file absence could not be proved", StringComparison.Ordinal));

            Assert.Null(warning.Exception);

            Assert.DoesNotContain(selector, warning.Message, StringComparison.Ordinal);

            Assert.DoesNotContain("private-selector-canary", string.Join("|", warning.Properties!.Select(property => property.Value)), StringComparison.Ordinal);

            Assert.Equal(record.Id, Assert.Single(warning.Properties!, property => property.Key == "AttachmentId").Value);
        }
        finally
        {
            await _db.Database.ExecuteSqlRawAsync(
                "UPDATE SessionAttachments SET RelativePath = {0} WHERE Id = {1}", record.RelativePath, GrimoireEntitySql.Format(record.Id));

            File.Move(parked, absolute);
        }

        Assert.Equal(plaintext, (await _store.ReadBytesAsync(record)).ToArray());
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rv260_reconcile_deletes_missing_legacy_leaves_under_the_exact_owner_gate(bool pending)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid session = Guid.NewGuid();

        string turn = "rv260..legacy";

        if (!pending)
        {
            await EnsureSessionAsync(session, "legacy-absence");
        }

        SessionAttachmentRecord record = await _store!.PersistNewAsync(
            pending ? null : session, pending ? turn : null, null, "legacy.txt", "legacy.txt", "legacy"u8.ToArray(), "text/plain", SessionAttachmentKind.Text);

        string original = Path.Combine(_attachmentsRoot, record.RelativePath);

        string parked = _attachmentsRoot + ".legacy-" + record.Id.ToString("N");

        File.Move(original, parked);

        string prefix = pending ? "_pending/" + turn : session.ToString("N");

        string relative = prefix + "/legacy/v1/legacy.txt";

        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(_attachmentsRoot, relative))!);

        await _db!.Database.ExecuteSqlRawAsync(
            "UPDATE SessionAttachments SET RelativePath = {0} WHERE Id = {1}", relative, GrimoireEntitySql.Format(record.Id));

        bool gated = false;

        Microsoft.Win32.SafeHandles.SafeFileHandle? parent = null;

        Func<Microsoft.Win32.SafeHandles.SafeFileHandle, FileHandleMetadata?>? probe = null;

        probe = handle =>
        {
            FileHandleIdentityInterop.TryGetHandleMetadataForTests = null;

            try
            {
                if (FileHandleIdentityInterop.TryGetHandleKernelPath(handle, out string? path) && Path.GetFileName(path) == "v1")
                {
                    parent = handle;
                }

                return FileHandleIdentityInterop.TryGetHandleMetadata(handle, out FileHandleMetadata metadata) ? metadata : null;
            }
            finally
            {
                FileHandleIdentityInterop.TryGetHandleMetadataForTests = probe;
            }
        };

        FileHandleIdentityInterop.TryGetHandleMetadataForTests = probe;

        SqliteConnection connection = (SqliteConnection)_db.Database.GetDbConnection();

        SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.strdelegate_trace)((_, statement) =>
        {
            if (statement.StartsWith("DELETE FROM \"SessionAttachments\"", StringComparison.Ordinal))
            {
                gated = SessionAttachmentStore.AttachmentGates.IsHeld(pending ? "pending|" + turn : "session|" + session.ToString("N"))
                    && parent is { IsClosed: false, IsInvalid: false };
            }
        }), null);

        try
        {
            await _store.ReconcileAsync(TimeSpan.FromHours(24));

            Assert.True(gated);

            Assert.True(parent!.IsClosed);

            Assert.Null(await _store.GetByIdAsync(record.Id));
        }
        finally
        {
            FileHandleIdentityInterop.TryGetHandleMetadataForTests = null;

            SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.strdelegate_trace)null!, null);

            File.Delete(parked);
        }
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rv260_reconcile_retains_a_pending_row_without_an_exact_valid_gate_key(bool undefinedState)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string turn = "rv260-gate";

        byte[] plaintext = "pending recovery"u8.ToArray();

        SessionAttachmentRecord record = await _store!.PersistNewAsync(
            null, turn, null, "pending.txt", "pending.txt", plaintext, "text/plain", SessionAttachmentKind.Text);

        string absolute = Path.Combine(_attachmentsRoot, record.RelativePath);

        string parked = _attachmentsRoot + ".pending-" + record.Id.ToString("N");

        File.Move(absolute, parked);

        if (undefinedState)
        {
            await _db!.Database.ExecuteSqlRawAsync(
                "UPDATE SessionAttachments SET State = {0} WHERE Id = {1}", "99", GrimoireEntitySql.Format(record.Id));
        }
        else
        {
            await _db!.Database.ExecuteSqlRawAsync(
                "UPDATE SessionAttachments SET PendingTurnId = {0} WHERE Id = {1}", " " + turn + " ", GrimoireEntitySql.Format(record.Id));
        }

        try
        {
            await _store.ReconcileAsync(TimeSpan.FromHours(24));

            Assert.NotNull(await _store.GetByIdAsync(record.Id));
        }
        finally
        {
            await _db.Database.ExecuteSqlRawAsync(
                "UPDATE SessionAttachments SET PendingTurnId = {0}, State = 'Pending' WHERE Id = {1}", turn, GrimoireEntitySql.Format(record.Id));

            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);

            File.Move(parked, absolute);
        }

        Assert.Equal(plaintext, (await _store.ReadBytesAsync(record)).ToArray());
    }

    [SkippableFact]
    public async Task Rv260_reconcile_cancellation_after_native_admission_disposes_the_parent_without_deleting()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid session = Guid.NewGuid();

        await EnsureSessionAsync(session, "cancel-native-proof");

        byte[] plaintext = "cancel recovery"u8.ToArray();

        SessionAttachmentRecord record = await _store!.PersistNewAsync(
            session, null, null, "cancel.txt", "cancel.txt", plaintext, "text/plain", SessionAttachmentKind.Text);

        string absolute = Path.Combine(_attachmentsRoot, record.RelativePath);

        string parked = _attachmentsRoot + ".cancel-" + record.Id.ToString("N");

        File.Move(absolute, parked);

        using CancellationTokenSource cancellation = new();

        bool enabled = false;

        Microsoft.Win32.SafeHandles.SafeFileHandle? parent = null;

        _store.AfterMissingFileSnapshotForTesting = _ =>
        {
            enabled = true;

            return Task.CompletedTask;
        };

        Func<Microsoft.Win32.SafeHandles.SafeFileHandle, FileHandleMetadata?>? probe = null;

        probe = handle =>
        {
            FileHandleIdentityInterop.TryGetHandleMetadataForTests = null;

            try
            {
                if (enabled && FileHandleIdentityInterop.TryGetHandleKernelPath(handle, out string? path) && Path.GetFileName(path) == session.ToString("N"))
                {
                    parent = handle;

                    cancellation.Cancel();
                }

                return FileHandleIdentityInterop.TryGetHandleMetadata(handle, out FileHandleMetadata metadata) ? metadata : null;
            }
            finally
            {
                FileHandleIdentityInterop.TryGetHandleMetadataForTests = probe;
            }
        };

        FileHandleIdentityInterop.TryGetHandleMetadataForTests = probe;

        try
        {
            OperationCanceledException failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => _store.ReconcileAsync(TimeSpan.FromHours(24), cancellation.Token));

            Assert.Equal(cancellation.Token, failure.CancellationToken);

            Assert.NotNull(parent);

            Assert.True(parent.IsClosed);

            Assert.NotNull(await _store.GetByIdAsync(record.Id));
        }
        finally
        {
            FileHandleIdentityInterop.TryGetHandleMetadataForTests = null;

            _store.AfterMissingFileSnapshotForTesting = null;

            File.Move(parked, absolute);
        }

        Assert.Equal(plaintext, (await _store.ReadBytesAsync(record)).ToArray());
    }

    [SkippableTheory]
    [InlineData("bound", false)]
    [InlineData("bound", true)]
    [InlineData("pending", false)]
    [InlineData("pending", true)]
    [InlineData("bound-owner-only", false)]
    [InlineData("pending-owner-only", false)]
    [InlineData("bound-pending-namespace", false)]
    [InlineData("pending-bound-namespace", false)]
    [InlineData("pending-case-alias", false)]
    public async Task Rv260_owner_prefix_must_match_the_row_gate_before_missing_file_deletion(string scenario, bool legacy)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid session = Guid.NewGuid();

        Guid otherSession = Guid.NewGuid();

        string turn = "rv260-owner-a";

        string otherTurn = "rv260-owner-b";

        await EnsureSessionAsync(session, "source-owner");

        await EnsureSessionAsync(otherSession, "other-owner");

        bool pending = scenario.StartsWith("pending", StringComparison.Ordinal);

        byte[] plaintext = "owner-bound recovery"u8.ToArray();

        SessionAttachmentRecord record = await _store!.PersistNewAsync(
            pending ? null : session, pending ? turn : null, null, "owner.txt", "owner.txt", plaintext, "text/plain", SessionAttachmentKind.Text);

        string original = Path.Combine(_attachmentsRoot, record.RelativePath);

        string parked = _attachmentsRoot + ".owner-proof-" + record.Id.ToString("N");

        File.Move(original, parked);

        string tail = legacy ? "legacy/v1/owner.txt" : record.Id.ToString("N");

        string relative = scenario switch
        {
            "bound" => otherSession.ToString("N") + "/" + tail,
            "pending" => "_pending/" + otherTurn + "/" + tail,
            "bound-owner-only" => session.ToString("N"),
            "pending-owner-only" => "_pending/" + turn,
            "bound-pending-namespace" => "_pending/" + otherTurn + "/" + tail,
            "pending-bound-namespace" => otherSession.ToString("N") + "/" + turn + "/v1/" + tail,
            "pending-case-alias" => "_pending/" + turn.ToUpperInvariant() + "/" + tail,
            _ => throw new InvalidOperationException("Unknown owner-proof fixture."),
        };

        string aliasTemporary = _attachmentsRoot + ".pending-case-parent-" + record.Id.ToString("N");

        string? aliasParent = scenario == "pending-case-alias" ? Path.GetDirectoryName(Path.Combine(_attachmentsRoot, relative)) : null;

        if (aliasParent is not null)
        {
            Directory.Move(Path.GetDirectoryName(original)!, aliasTemporary);

            Directory.Move(aliasTemporary, aliasParent);
        }

        bool ownerOnly = scenario.EndsWith("owner-only", StringComparison.Ordinal);

        if (ownerOnly)
        {
            Directory.Delete(Path.GetDirectoryName(original)!);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(_attachmentsRoot, relative))!);
        }

        await _db!.Database.ExecuteSqlRawAsync(
            "UPDATE SessionAttachments SET RelativePath = {0} WHERE Id = {1}", relative, GrimoireEntitySql.Format(record.Id));

        string? targetGateKey = ownerOnly ? null
            : relative.StartsWith("_pending/", StringComparison.Ordinal)
                ? SessionAttachmentStore.PendingTurnGateKey(relative.Split('/')[1])
                : SessionAttachmentStore.SessionGateKey(otherSession);

        using IDisposable? targetGate = targetGateKey is null
            ? null
            : await SessionAttachmentStore.AttachmentGates.AcquireAsync(targetGateKey);

        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));

        try
        {
            if (targetGateKey is not null)
            {
                Assert.True(SessionAttachmentStore.AttachmentGates.IsHeld(targetGateKey));
            }

            await _store.ReconcileAsync(TimeSpan.FromDays(365), cancellation.Token);

            Assert.NotNull(await _store.GetByIdAsync(record.Id));
        }
        finally
        {
            await _db.Database.ExecuteSqlRawAsync(
                "UPDATE SessionAttachments SET RelativePath = {0} WHERE Id = {1}", record.RelativePath, GrimoireEntitySql.Format(record.Id));

            if (aliasParent is not null)
            {
                Directory.Move(aliasParent, aliasTemporary);

                Directory.Move(aliasTemporary, Path.GetDirectoryName(original)!);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(original)!);

            File.Move(parked, original);
        }

        Assert.Equal(plaintext, (await _store.ReadBytesAsync(record)).ToArray());
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rv260_owner_prefix_accepts_case_equivalent_session_guids_without_rewriting_legacy_tails(bool legacy)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid session = Guid.ParseExact("a2600000000000000000000000000001", "N");

        await EnsureSessionAsync(session, "case-equivalent-owner");

        SessionAttachmentRecord record = await _store!.PersistNewAsync(
            session, null, null, "case.txt", "case.txt", "case"u8.ToArray(), "text/plain", SessionAttachmentKind.Text);

        string original = Path.Combine(_attachmentsRoot, record.RelativePath);

        string parked = _attachmentsRoot + ".case-proof-" + record.Id.ToString("N");

        File.Move(original, parked);

        string owner = session.ToString("N").ToUpperInvariant();

        string originalParent = Path.GetDirectoryName(original)!;

        string temporaryParent = _attachmentsRoot + ".case-parent-" + record.Id.ToString("N");

        string upperParent = Path.Combine(_attachmentsRoot, owner);

        Directory.Move(originalParent, temporaryParent);

        Directory.Move(temporaryParent, upperParent);

        string relative = owner + "/" + (legacy ? "legacy/v1/case.txt" : record.Id.ToString("N"));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(_attachmentsRoot, relative))!);

        await _db!.Database.ExecuteSqlRawAsync(
            "UPDATE SessionAttachments SET RelativePath = {0} WHERE Id = {1}", relative, GrimoireEntitySql.Format(record.Id));

        try
        {
            await _store.ReconcileAsync(TimeSpan.FromDays(365));

            Assert.Null(await _store.GetByIdAsync(record.Id));
        }
        finally
        {
            File.Delete(parked);
        }
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

    [SkippableFact]
    public async Task ReconcileAsync_deletes_a_missing_sessions_attachment_and_keeps_a_live_sessions_attachment()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid liveSessionId = Guid.NewGuid();
        Guid missingSessionId = Guid.NewGuid();

        await EnsureSessionAsync(liveSessionId, "live-session");

        SessionAttachmentRecord live = await _store!.PersistNewAsync(
            liveSessionId,
            pendingTurnId: null,
            entryId: null,
            "live.txt",
            "live.txt",
            Encoding.UTF8.GetBytes("live"),
            "text/plain",
            SessionAttachmentKind.Text);
        SessionAttachmentRecord orphan = await _store.PersistNewAsync(
            missingSessionId,
            pendingTurnId: null,
            entryId: null,
            "orphan.txt",
            "orphan.txt",
            Encoding.UTF8.GetBytes("orphan"),
            "text/plain",
            SessionAttachmentKind.Text);

        string livePath = Path.Combine(_attachmentsRoot, live.RelativePath);
        string orphanPath = Path.Combine(_attachmentsRoot, orphan.RelativePath);

        await _store.ReconcileAsync(TimeSpan.FromDays(365));

        Assert.NotNull(await _store.GetByIdAsync(live.Id));
        Assert.True(File.Exists(livePath));
        Assert.Null(await _store.GetByIdAsync(orphan.Id));
        Assert.False(File.Exists(orphanPath));
        Assert.False(Directory.Exists(Path.Combine(_attachmentsRoot, missingSessionId.ToString("N"))));
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

    [SkippableFact]
    public async Task ReconcileAsync_keeps_a_row_promoted_after_the_missing_file_snapshot()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string pendingTurnId = "race-" + Guid.NewGuid().ToString("N");

        Guid sessionId = Guid.NewGuid();

        Guid entryId = Guid.NewGuid();

        byte[] bytes = Encoding.UTF8.GetBytes("promoted-during-reconcile");

        SessionAttachmentRecord pending = await _store!.PersistNewAsync(
            sessionId: null,
            pendingTurnId,
            entryId: null,
            "notes.txt",
            "notes.txt",
            bytes,
            "text/plain",
            SessionAttachmentKind.Text);

        // The sweep snapshots every row with no gate held; the promotion then rewrites the row and
        // unlinks the old pending file. The snapshot the sweep is about to evaluate therefore names a
        // path that no longer exists, even though the attachment is alive and bound.
        _store.AfterMissingFileSnapshotForTesting = async _ =>
        {
            await _store.PromotePendingAsync(pendingTurnId, sessionId, entryId);

            // Retain an ordinary stale parent so positive leaf absence actually reaches the SQL CAS.
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(_attachmentsRoot, pending.RelativePath))!);

            _store.AfterMissingFileSnapshotForTesting = null;
        };

        using SqliteStatementTrace trace = new((SqliteConnection)_db!.Database.GetDbConnection());

        await _store.ReconcileAsync(TimeSpan.FromDays(365));

        Assert.Contains(trace.Statements, statement => statement.StartsWith("DELETE FROM \"SessionAttachments\"", StringComparison.Ordinal));

        SessionAttachmentRecord? survivor = await _store.GetByIdAsync(pending.Id);

        Assert.NotNull(survivor);

        Assert.Equal(SessionAttachmentState.Bound, survivor!.State);

        Assert.Equal(sessionId, survivor.SessionId);

        Assert.True(File.Exists(Path.Combine(_attachmentsRoot, survivor.RelativePath)));

        ReadOnlyMemory<byte> loaded = await _store.ReadBytesAsync(survivor);

        Assert.Equal(bytes, loaded.ToArray());
    }

    [SkippableFact]
    public async Task ReconcileAsync_spares_an_unreferenced_file_written_after_the_sweep_started()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string liveDirectory = Path.Combine(_attachmentsRoot, Guid.NewGuid().ToString("N"), "in-flight.txt", "v1");

        string inFlight = Path.Combine(liveDirectory, "in-flight.txt");

        string abandonedDirectory = Path.Combine(_attachmentsRoot, Guid.NewGuid().ToString("N"), "abandoned.txt", "v1");

        Directory.CreateDirectory(abandonedDirectory);

        string abandoned = Path.Combine(abandonedDirectory, "abandoned.txt");

        await File.WriteAllTextAsync(abandoned, "ciphertext");

        // Stamped well before the sweep starts, because a host with a coarse file clock can stamp this
        // file and the sweep's own threshold within one tick of each other, and the sweep would then
        // spare it as in-flight. Ageing it keeps this assertion about the sweep and not about the tick.
        File.SetLastWriteTimeUtc(abandoned, DateTime.UtcNow - TimeSpan.FromHours(1));

        // An attachment write lands its ciphertext before it inserts its row, so a write that starts
        // after the sweep snapshotted the referenced paths owns a file the snapshot cannot name. The
        // seam stays installed because a reconcile runs the orphan sweep more than once, and the point
        // is that each pass spares what appeared after its own snapshot.
        _store!.AfterOrphanPathSnapshotForTesting = async _ =>
        {
            Directory.CreateDirectory(liveDirectory);

            await File.WriteAllTextAsync(inFlight, "ciphertext");
        };

        await _store.ReconcileAsync(TimeSpan.FromDays(365));

        _store.AfterOrphanPathSnapshotForTesting = null;

        Assert.True(File.Exists(inFlight), "the sweep unlinked ciphertext for an attachment write still in flight.");

        Assert.False(File.Exists(abandoned), "the sweep left a genuinely unreferenced file behind.");
    }

    [SkippableFact]
    public async Task ReconcileAsync_spares_an_in_flight_file_when_the_file_clock_lags_the_process_clock()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        // The same commit spared this file on one Windows runner and unlinked it on another, so the real
        // skew between the clock a process reads and the clock a host stamps files with is whatever a
        // timer tick happened to be. The seam replaces that coin flip with a fixed lag applied to every
        // stamp the sweep reads, its own threshold included. A minute is far more lag than any host has,
        // and that is the point: the assertions then turn on which clock the threshold is drawn from and
        // not on how long the row snapshot took to run.
        TimeSpan fileClockLag = TimeSpan.FromMinutes(1);

        _store!.FileLastWriteTimeUtcForTesting = path => File.GetLastWriteTimeUtc(path) - fileClockLag;

        string liveDirectory = Path.Combine(_attachmentsRoot, Guid.NewGuid().ToString("N"), "in-flight.txt", "v1");

        string inFlight = Path.Combine(liveDirectory, "in-flight.txt");

        string abandonedDirectory = Path.Combine(_attachmentsRoot, Guid.NewGuid().ToString("N"), "abandoned.txt", "v1");

        Directory.CreateDirectory(abandonedDirectory);

        string abandoned = Path.Combine(abandonedDirectory, "abandoned.txt");

        await File.WriteAllTextAsync(abandoned, "ciphertext");

        File.SetLastWriteTimeUtc(abandoned, DateTime.UtcNow - TimeSpan.FromHours(1));

        _store.AfterOrphanPathSnapshotForTesting = async _ =>
        {
            Directory.CreateDirectory(liveDirectory);

            await File.WriteAllTextAsync(inFlight, "ciphertext");
        };

        try
        {
            await _store.ReconcileAsync(TimeSpan.FromDays(365));
        }
        finally
        {
            _store.AfterOrphanPathSnapshotForTesting = null;

            _store.FileLastWriteTimeUtcForTesting = null;
        }

        Assert.True(
            File.Exists(inFlight),
            "the sweep read this file's stamp as older than a threshold taken from a different clock, and unlinked "
            + "ciphertext for an attachment write still in flight.");

        Assert.False(File.Exists(abandoned), "the sweep left a genuinely unreferenced file behind.");
    }

    [SkippableFact]
    public async Task ReconcileAsync_spares_a_promoted_copy_whose_row_has_not_committed_yet()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        await EnsureSessionAsync(sessionId, "promote-versus-sweep");

        string pendingTurnId = "promote-sweep-" + Guid.NewGuid().ToString("N");

        byte[] bytes = Encoding.UTF8.GetBytes("promoted-while-the-sweep-ran");

        SessionAttachmentRecord pending = await _store!.PersistNewAsync(
            sessionId: null,
            pendingTurnId,
            entryId: null,
            "notes.txt",
            "notes.txt",
            bytes,
            "text/plain",
            SessionAttachmentKind.Text);

        await using ArcanumDbContext promoterDb = _fixture.CreateContext(_dbPath);

        SessionAttachmentStore promoter = new(
            promoterDb,
            Options.Create(_settings),
            _attachmentsRoot,
            CreateEncryptedBlobStore());

        TaskCompletionSource copyLanded = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource releasePromotion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // The copy is on disk and the row that will claim it is not yet committed. The copy is aged so the sweep's
        // "written after my snapshot" guard cannot be what spares it: a promotion that stalled between its copy and
        // its commit leaves exactly this file.
        promoter.AfterBytesCommittedBeforeDbForTesting = async cancellationToken =>
        {
            foreach (string copy in Directory.EnumerateFiles(
                         Path.Combine(_attachmentsRoot, sessionId.ToString("N")),
                         "*",
                         SearchOption.AllDirectories))
            {
                File.SetLastWriteTimeUtc(copy, DateTime.UtcNow - TimeSpan.FromHours(1));
            }

            copyLanded.TrySetResult();

            await releasePromotion.Task.WaitAsync(cancellationToken);
        };

        Task promotion = Task.Run(() => promoter.PromotePendingAsync(pendingTurnId, sessionId, entryId: null));

        try
        {
            await copyLanded.Task.WaitAsync(TimeSpan.FromSeconds(30));

            await _store.ReconcileAsync(TimeSpan.FromDays(365));
        }
        finally
        {
            releasePromotion.TrySetResult();
        }

        await promotion.WaitAsync(TimeSpan.FromSeconds(30));

        SessionAttachmentRecord? bound = await _store.GetByIdAsync(pending.Id);

        Assert.NotNull(bound);

        Assert.Equal(SessionAttachmentState.Bound, bound!.State);

        Assert.True(
            File.Exists(Path.Combine(_attachmentsRoot, bound.RelativePath)),
            "the sweep unlinked the promoted copy before its row committed, and the promotion then deleted the original.");

        Assert.Equal(bytes, (await _store.ReadBytesAsync(bound)).ToArray());
    }

    [SkippableFact]
    public async Task PromotePendingAsync_refuses_to_commit_when_a_promoted_copy_is_gone_and_keeps_the_original()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        await EnsureSessionAsync(sessionId, "promote-lost-copy");

        string pendingTurnId = "promote-lost-" + Guid.NewGuid().ToString("N");

        byte[] bytes = Encoding.UTF8.GetBytes("copy-removed-before-commit");

        SessionAttachmentRecord pending = await _store!.PersistNewAsync(
            sessionId: null,
            pendingTurnId,
            entryId: null,
            "notes.txt",
            "notes.txt",
            bytes,
            "text/plain",
            SessionAttachmentKind.Text);

        _store.AfterBytesCommittedBeforeDbForTesting = _ =>
        {
            foreach (string copy in Directory.EnumerateFiles(
                         Path.Combine(_attachmentsRoot, sessionId.ToString("N")),
                         "*",
                         SearchOption.AllDirectories))
            {
                File.Delete(copy);
            }

            return Task.CompletedTask;
        };

        _ = await Assert.ThrowsAsync<IOException>(
            () => _store.PromotePendingAsync(pendingTurnId, sessionId, entryId: null));

        _store.AfterBytesCommittedBeforeDbForTesting = null;

        SessionAttachmentRecord? still = await _store.GetByIdAsync(pending.Id);

        Assert.NotNull(still);

        Assert.Equal(SessionAttachmentState.Pending, still!.State);

        Assert.True(File.Exists(Path.Combine(_attachmentsRoot, still.RelativePath)));

        Assert.Equal(bytes, (await _store.ReadBytesAsync(still)).ToArray());
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

        // Rendered the way the store renders it. A bare ToString() here matched nothing once the store
        // was canonicalised, and the backdate then updated zero rows in silence - which made the stale
        // sweep this helper exists to trigger look as though it had failed.
        idParam.Value = id.ToString().ToUpperInvariant();

        cmd.Parameters.Add(idParam);

        System.Data.Common.DbParameter createdParam = cmd.CreateParameter();

        createdParam.ParameterName = "@createdAt";

        createdParam.Value = createdAt.ToString("o", System.Globalization.CultureInfo.InvariantCulture);

        cmd.Parameters.Add(createdParam);

        _ = await cmd.ExecuteNonQueryAsync();
    }
}
