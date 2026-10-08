using System.Data.Common;
using System.Globalization;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.Weave.Tapestry;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Fixtures;

using SQLitePCL;

namespace RetroDownfall.Arcanum.Tests.Weave.Tapestry;

/// <summary>
/// A scope's corpus fingerprint comes from content hashes the store keeps beside each leaf, so the sweep tick
/// that only has to learn nothing changed reads no chunk text (DESIGN §21.11). These cases pin the three
/// things that has to be true for that to be safe: the fingerprint is exactly the one the leaves' text yields,
/// a stored hash can never outlive the text it describes whichever way the row changes, and a scope past the
/// ceiling is refused by counting.
/// </summary>
[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class TapestryLeafHashTests : IAsyncLifetime
{
    private const int TestDimensions = 8;

    private const string WorkspaceId = "/repo";

    private const string SessionId = "AAAAAAAA-1111-4111-8111-AAAAAAAAAAAA";

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    private TapestryStore? _store;

    public TapestryLeafHashTests(GrimoireFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _dbPath = _fixture.CopyDatabase();

        _db = _fixture.CreateContext(_dbPath);

        _store = new TapestryStore(_db, new WeaveIndexAvailability());

        if (GrimoireFixture.SqlCipherAvailable)
        {
            await ExecuteAsync(
                """
                INSERT INTO "Sessions" ("Id", "Status", "CreatedAt", "UpdatedAt")
                VALUES (@session, 'active', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z')
                """,
                ("@session", SessionId));
        }
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
    }

    /// <summary>The three corpora, each with the scope it is woven under and the row operations that change it.</summary>
    public static TheoryData<TapestryScopeKind> Kinds =>
        new()
        {
            TapestryScopeKind.Workspace,
            TapestryScopeKind.SessionAttachment,
            TapestryScopeKind.Session,
        };

    private static TapestryScope ScopeOf(TapestryScopeKind kind) =>
        kind switch
        {
            TapestryScopeKind.Workspace => new(kind, WorkspaceId),
            TapestryScopeKind.SessionAttachment => TapestryScope.ForSessionAttachment(Guid.Parse(SessionId)),
            _ => TapestryScope.ForSession(Guid.Parse(SessionId)),
        };

    private static string SourceKindOf(TapestryScopeKind kind) =>
        kind switch
        {
            TapestryScopeKind.Workspace => nameof(TapestryLeafSourceKind.WorkspaceFileChunk),
            TapestryScopeKind.SessionAttachment => nameof(TapestryLeafSourceKind.SessionAttachmentChunk),
            _ => nameof(TapestryLeafSourceKind.Entry),
        };

    private static string TableOf(TapestryScopeKind kind) =>
        kind switch
        {
            TapestryScopeKind.Workspace => "workspace_file_chunks",
            TapestryScopeKind.SessionAttachment => "session_attachment_chunks",
            _ => "Entries",
        };

    private static string IdColumnOf(TapestryScopeKind kind) => kind == TapestryScopeKind.Session ? "Id" : "ChunkId";

    /// <summary>The id a seeded row carries: an Entry id is a Guid, the chunk tables take any text.</summary>
    private static string IdOf(TapestryScopeKind kind, int index) =>
        kind == TapestryScopeKind.Session
            ? new Guid(index, 0, 0, [0, 0, 0, 0, 0, 0, 0, 1]).ToString("D").ToUpperInvariant()
            : $"chunk-{index:D5}";

    private async Task InsertAsync(TapestryScopeKind kind, int index, string content, bool replace = false)
    {
        string verb = replace ? "INSERT OR REPLACE" : "INSERT";

        string id = IdOf(kind, index);

        switch (kind)
        {
            case TapestryScopeKind.Workspace:
                await ExecuteAsync(
                    $"""
                    {verb} INTO workspace_file_chunks
                        (ChunkId, WorkspacePath, RelativePath, ChunkIndex, Content, CharOffset, CharLength,
                         StartLine, EndLine, FileLastWriteTime, IndexedAt)
                    VALUES (@id, @scope, 'a.cs', 0, @content, 0, 10, 1, 3, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z')
                    """,
                    ("@id", id),
                    ("@scope", WorkspaceId),
                    ("@content", content));

                break;

            case TapestryScopeKind.SessionAttachment:
                string attachmentId = new Guid(index + 1000, 0, 0, [0, 0, 0, 0, 0, 0, 0, 2]).ToString("D").ToUpperInvariant();

                await ExecuteAsync(
                    """
                    INSERT OR IGNORE INTO "SessionAttachments"
                        ("Id", "SessionId", "EntryId", "PendingTurnId", "State", "LogicalKey",
                         "OriginalFileName", "Version", "RelativePath", "ContentSha256", "MimeType",
                         "ByteLength", "Kind", "CreatedAt")
                    VALUES (@attachment, @session, NULL, NULL, 'Bound', @logicalKey, 'notes.md', 1,
                            'notes.md', 'ATTACHMENT-HASH', 'text/plain', 16, 'Text', '2026-01-01T00:00:00Z')
                    """,
                    ("@attachment", attachmentId),
                    ("@session", SessionId),
                    ("@logicalKey", $"notes-{index}"));

                await ExecuteAsync(
                    $"""
                    {verb} INTO session_attachment_chunks
                        (ChunkId, GenerationId, SessionId, AttachmentId, LogicalKey, Version,
                         OriginalFileName, MimeType, ContentSha256, ChunkIndex, CharacterStart, CharacterEnd,
                         StartLine, EndLine, Content, EmbeddingDimension, ExtractedAt, IndexedAt, RetrievalScope)
                    VALUES (@id, 'generation-1', @session, @attachment, @logicalKey, 1, 'notes.md', 'text/plain',
                            'ATTACHMENT-HASH', 0, 0, 16, 1, 1, @content, @dimensions,
                            '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z', 'Latest')
                    """,
                    ("@id", id),
                    ("@session", ScopeOf(kind).Id),
                    ("@attachment", attachmentId),
                    ("@logicalKey", $"notes-{index}"),
                    ("@content", content),
                    ("@dimensions", TestDimensions));

                break;

            default:
                await ExecuteAsync(
                    $"""
                    {verb} INTO "Entries" ("Id", "SessionId", "Role", "Content", "ModelUsed", "CreatedAt", "Sequence")
                    VALUES (@id, @session, 1, @content, 'model', '2026-01-01T00:00:00Z', @sequence)
                    """,
                    ("@id", id),
                    ("@session", SessionId),
                    ("@content", content),
                    ("@sequence", index));

                break;
        }
    }

    private Task UpdateContentAsync(TapestryScopeKind kind, int index, string content) =>
        ExecuteAsync(
            $"UPDATE {TableOf(kind)} SET Content = @content WHERE {IdColumnOf(kind)} = @id",
            ("@content", content),
            ("@id", IdOf(kind, index)));

    private Task DeleteRowAsync(TapestryScopeKind kind, int index) =>
        ExecuteAsync(
            $"DELETE FROM {TableOf(kind)} WHERE {IdColumnOf(kind)} = @id",
            ("@id", IdOf(kind, index)));

    private async Task SeedAsync(TapestryScopeKind kind, int count)
    {
        for (int index = 1; index <= count; index++)
        {
            await InsertAsync(kind, index, $"body of leaf {index}");
        }
    }

    private async Task<TapestryCorpusIdentity> IdentityAsync(TapestryScopeKind kind, int maxLeaves = 1000) =>
        await _store!.GetCorpusIdentityAsync(ScopeOf(kind), maxLeaves, CancellationToken.None);

    private async Task<List<TapestryLeafSource>> LeavesAsync(TapestryScopeKind kind)
    {
        List<TapestryLeafSource> leaves = [];

        await foreach (IReadOnlyList<TapestryLeafSource> page in _store!.EnumerateLeafPagesAsync(
            ScopeOf(kind),
            TestDimensions,
            CancellationToken.None))
        {
            leaves.AddRange(page);
        }

        return leaves;
    }

    private async Task<long> CountHashesAsync(TapestryScopeKind kind) =>
        await ScalarAsync(
            "SELECT COUNT(*) FROM tapestry_leaf_hashes WHERE SourceKind = @kind",
            ("@kind", SourceKindOf(kind)));

    /// <summary>Runs <paramref name="action"/> and returns every statement SQLite executed meanwhile.</summary>
    private async Task<List<string>> TraceAsync(Func<Task> action)
    {
        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        List<string> statements = [];

        raw.sqlite3_trace(connection.Handle, (object _, string sql) => statements.Add(sql), null);

        try
        {
            await action();
        }
        finally
        {
            raw.sqlite3_trace(connection.Handle, (strdelegate_trace)null!, null);
        }

        return statements;
    }

    private static bool ReadsContent(string sql) => sql.Contains("\"Content\"", StringComparison.Ordinal);

    [SkippableTheory]
    [MemberData(nameof(Kinds))]
    public async Task The_identity_is_the_fingerprint_of_the_leaves_the_text_yields(TapestryScopeKind kind)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedAsync(kind, 3);

        TapestryCorpusIdentity identity = await IdentityAsync(kind);

        List<TapestryLeafSource> leaves = await LeavesAsync(kind);

        Assert.False(identity.ExceedsCeiling);

        Assert.Equal(3, identity.LeafCount);

        Assert.Equal(3, leaves.Count);

        // The generation records the fingerprint of the leaves it was built from, so the hash-only answer
        // has to be byte for byte the one the text gives; otherwise a built tree would never read as current.
        Assert.Equal(TapestryHash.OfCorpus(leaves), identity.Fingerprint);

        Assert.Equal(3, await CountHashesAsync(kind));
    }

    [SkippableTheory]
    [MemberData(nameof(Kinds))]
    public async Task A_second_identity_reads_no_chunk_text(TapestryScopeKind kind)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedAsync(kind, 3);

        List<string> first = await TraceAsync(async () => _ = await IdentityAsync(kind));

        // The first answer is the one that stores the hashes, so it has to read the text exactly once.
        Assert.Contains(first, ReadsContent);

        TapestryCorpusIdentity firstIdentity = await IdentityAsync(kind);

        TapestryCorpusIdentity second = default!;

        List<string> statements = await TraceAsync(async () => second = await IdentityAsync(kind));

        Assert.Equal(firstIdentity, second);

        Assert.NotEmpty(statements);

        Assert.DoesNotContain(statements, ReadsContent);
    }

    [SkippableTheory]
    [MemberData(nameof(Kinds))]
    public async Task Rewriting_a_row_changes_the_identity(TapestryScopeKind kind)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedAsync(kind, 3);

        string before = (await IdentityAsync(kind)).Fingerprint;

        await UpdateContentAsync(kind, 2, "an edited body");

        TapestryCorpusIdentity after = await IdentityAsync(kind);

        Assert.NotEqual(before, after.Fingerprint);

        Assert.Equal(TapestryHash.OfCorpus(await LeavesAsync(kind)), after.Fingerprint);
    }

    [SkippableTheory]
    [MemberData(nameof(Kinds))]
    public async Task Replacing_a_row_in_place_changes_the_identity(TapestryScopeKind kind)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedAsync(kind, 3);

        string before = (await IdentityAsync(kind)).Fingerprint;

        // INSERT OR REPLACE removes the old row without firing the delete trigger, so the insert trigger is
        // what keeps the replaced row's old hash from describing its new text.
        await InsertAsync(kind, 2, "a replacement body", replace: true);

        TapestryCorpusIdentity after = await IdentityAsync(kind);

        Assert.Equal(3, after.LeafCount);

        Assert.NotEqual(before, after.Fingerprint);

        Assert.Equal(TapestryHash.OfCorpus(await LeavesAsync(kind)), after.Fingerprint);
    }

    [SkippableTheory]
    [MemberData(nameof(Kinds))]
    public async Task Deleting_a_row_drops_its_hash_and_changes_the_identity(TapestryScopeKind kind)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedAsync(kind, 3);

        string before = (await IdentityAsync(kind)).Fingerprint;

        Assert.Equal(3, await CountHashesAsync(kind));

        await DeleteRowAsync(kind, 2);

        Assert.Equal(2, await CountHashesAsync(kind));

        TapestryCorpusIdentity after = await IdentityAsync(kind);

        Assert.Equal(2, after.LeafCount);

        Assert.NotEqual(before, after.Fingerprint);
    }

    [SkippableTheory]
    [MemberData(nameof(Kinds))]
    public async Task Inserting_a_row_changes_the_identity(TapestryScopeKind kind)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedAsync(kind, 3);

        string before = (await IdentityAsync(kind)).Fingerprint;

        await InsertAsync(kind, 4, "a fourth body");

        TapestryCorpusIdentity after = await IdentityAsync(kind);

        Assert.Equal(4, after.LeafCount);

        Assert.NotEqual(before, after.Fingerprint);
    }

    [SkippableTheory]
    [MemberData(nameof(Kinds))]
    public async Task An_update_that_leaves_the_text_alone_keeps_the_stored_hash(TapestryScopeKind kind)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedAsync(kind, 3);

        _ = await IdentityAsync(kind);

        // Same value written back to the text column, and a metadata column rewritten: neither is a change.
        await ExecuteAsync(
            $"UPDATE {TableOf(kind)} SET Content = Content WHERE {IdColumnOf(kind)} = @id",
            ("@id", IdOf(kind, 1)));

        string metadata = kind switch
        {
            TapestryScopeKind.Workspace => "IndexedAt = '2026-02-02T00:00:00Z'",
            TapestryScopeKind.SessionAttachment => "IndexedAt = '2026-02-02T00:00:00Z'",
            _ => "IsPinned = 1",
        };

        await ExecuteAsync(
            $"UPDATE {TableOf(kind)} SET {metadata} WHERE {IdColumnOf(kind)} = @id",
            ("@id", IdOf(kind, 1)));

        Assert.Equal(3, await CountHashesAsync(kind));
    }

    [SkippableTheory]
    [MemberData(nameof(Kinds))]
    public async Task Hashes_missing_from_an_upgraded_database_are_stored_on_first_use(TapestryScopeKind kind)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedAsync(kind, 3);

        TapestryCorpusIdentity before = await IdentityAsync(kind);

        // What an installation looks like right after the step that adds the table, or after any rows were
        // written while nothing was asking: no stored hash at all, or only some of them.
        await ExecuteAsync("DELETE FROM tapestry_leaf_hashes WHERE SourceKind = @kind AND SourceId <> @keep",
            ("@kind", SourceKindOf(kind)),
            ("@keep", IdOf(kind, 1)));

        Assert.Equal(1, await CountHashesAsync(kind));

        Assert.Equal(before, await IdentityAsync(kind));

        Assert.Equal(3, await CountHashesAsync(kind));
    }

    [SkippableTheory]
    [MemberData(nameof(Kinds))]
    public async Task A_blank_row_is_known_to_be_blank_and_is_not_a_leaf(TapestryScopeKind kind)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedAsync(kind, 2);

        await InsertAsync(kind, 3, " \t\n ");

        TapestryCorpusIdentity identity = await IdentityAsync(kind);

        Assert.Equal(2, identity.LeafCount);

        Assert.Equal(TapestryHash.OfCorpus(await LeavesAsync(kind)), identity.Fingerprint);

        Assert.Equal(3, await CountHashesAsync(kind));

        TapestryCorpusIdentity second = default!;

        List<string> statements = await TraceAsync(async () => second = await IdentityAsync(kind));

        Assert.Equal(identity, second);

        Assert.DoesNotContain(statements, ReadsContent);
    }

    [SkippableTheory]
    [MemberData(nameof(Kinds))]
    public async Task A_scope_past_the_ceiling_is_refused_by_counting(TapestryScopeKind kind)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedAsync(kind, 3);

        TapestryCorpusIdentity exact = await IdentityAsync(kind, maxLeaves: 3);

        Assert.False(exact.ExceedsCeiling);

        Assert.Equal(3, exact.LeafCount);

        // Nothing was stored for a scope the ceiling refuses: clear what the exact call stored, then ask again.
        await ExecuteAsync("DELETE FROM tapestry_leaf_hashes");

        TapestryCorpusIdentity over = default!;

        List<string> statements = await TraceAsync(async () => over = await IdentityAsync(kind, maxLeaves: 2));

        Assert.True(over.ExceedsCeiling);

        Assert.Equal(string.Empty, over.Fingerprint);

        // Past the ceiling the count is all that is known, and it stops one row beyond it.
        Assert.Equal(3, over.LeafCount);

        Assert.DoesNotContain(statements, ReadsContent);

        Assert.DoesNotContain(
            statements,
            static sql => sql.Contains("INSERT", StringComparison.OrdinalIgnoreCase)
                && sql.Contains("tapestry_leaf_hashes", StringComparison.Ordinal));

        Assert.Equal(0, await CountHashesAsync(kind));
    }

    [SkippableFact]
    public async Task Leaves_stream_in_pages_in_stable_id_order_and_the_identity_covers_every_page()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        int total = (TapestryStore.LeafPageSize * 2) + 5;

        await ExecuteAsync(
            """
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < @total)
            INSERT INTO workspace_file_chunks
                (ChunkId, WorkspacePath, RelativePath, ChunkIndex, Content, CharOffset, CharLength,
                 StartLine, EndLine, FileLastWriteTime, IndexedAt)
            SELECT printf('chunk-%05d', i), @scope, 'a.cs', 0, 'body of leaf ' || i, 0, 10, 1, 3,
                   '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z'
            FROM n
            """,
            ("@total", total),
            ("@scope", WorkspaceId));

        List<IReadOnlyList<TapestryLeafSource>> pages = [];

        await foreach (IReadOnlyList<TapestryLeafSource> page in _store!.EnumerateLeafPagesAsync(
            ScopeOf(TapestryScopeKind.Workspace),
            TestDimensions,
            CancellationToken.None))
        {
            pages.Add(page);
        }

        Assert.Equal([TapestryStore.LeafPageSize, TapestryStore.LeafPageSize, 5], pages.Select(static page => page.Count));

        string[] ids = [.. pages.SelectMany(static page => page).Select(static leaf => leaf.SourceId)];

        Assert.Equal(total, ids.Length);

        Assert.Equal(
            [.. Enumerable.Range(1, total).Select(static i => "chunk-" + i.ToString("D5", CultureInfo.InvariantCulture))],
            ids);

        // The identity pages its hash storing the same way and covers every one of them.
        TapestryCorpusIdentity identity = await IdentityAsync(TapestryScopeKind.Workspace, maxLeaves: total);

        Assert.Equal(total, identity.LeafCount);

        Assert.Equal(TapestryHash.OfCorpus(pages.SelectMany(static page => page)), identity.Fingerprint);

        Assert.Equal(total, await CountHashesAsync(TapestryScopeKind.Workspace));
    }

    [SkippableFact]
    public async Task A_leaf_that_leaves_the_scope_between_the_listing_and_its_page_is_absent()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedAsync(TapestryScopeKind.SessionAttachment, 3);

        List<TapestryLeafSource> leaves = [];

        await foreach (IReadOnlyList<TapestryLeafSource> page in _store!.EnumerateLeafPagesAsync(
            ScopeOf(TapestryScopeKind.SessionAttachment),
            TestDimensions,
            CancellationToken.None))
        {
            leaves.AddRange(page);
        }

        Assert.Equal(3, leaves.Count);

        // A superseded attachment version keeps its rows and loses its retrieval scope, which takes it out of
        // the scope; every later read, identity and page alike, agrees it is gone.
        await ExecuteAsync(
            "UPDATE session_attachment_chunks SET RetrievalScope = NULL WHERE ChunkId = @id",
            ("@id", IdOf(TapestryScopeKind.SessionAttachment, 2)));

        Assert.Equal(2, (await IdentityAsync(TapestryScopeKind.SessionAttachment)).LeafCount);

        Assert.Equal(2, (await LeavesAsync(TapestryScopeKind.SessionAttachment)).Count);
    }

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        DbConnection connection = _db!.Database.GetDbConnection();

        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using DbCommand command = connection.CreateCommand();

        command.CommandText = sql;

        foreach ((string name, object value) in parameters)
        {
            DbParameter parameter = command.CreateParameter();

            parameter.ParameterName = name;

            parameter.Value = value;

            command.Parameters.Add(parameter);
        }

        _ = await command.ExecuteNonQueryAsync();
    }

    private async Task<long> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        DbConnection connection = _db!.Database.GetDbConnection();

        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using DbCommand command = connection.CreateCommand();

        command.CommandText = sql;

        foreach ((string name, object value) in parameters)
        {
            DbParameter parameter = command.CreateParameter();

            parameter.ParameterName = name;

            parameter.Value = value;

            command.Parameters.Add(parameter);
        }

        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }
}
