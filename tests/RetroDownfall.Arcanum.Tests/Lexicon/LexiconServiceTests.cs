using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Lexicon;
using RetroDownfall.Arcanum.Tests.Fixtures;
using SQLitePCL;

namespace RetroDownfall.Arcanum.Tests.Lexicon;

/// <summary>
/// <see cref="LexiconService"/> round-trip persistence against the real Grimoire schema (raw-SQL
/// <c>lexicon_entries</c> + FTS5 <c>lexicon_fts</c> declared in <c>Data/Schema/</c>).
/// </summary>
[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class LexiconServiceTests : IAsyncLifetime
{

    private const int FormerMaxFactsPerUpsert = 32;

    private const int FormerMaxFactsRetainedPerEntry = 256;

    private const int FormerMaxFactLength = 1024;

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    private LexiconService? _service;

    public LexiconServiceTests(GrimoireFixture fixture)
    {

        _fixture = fixture;

    }

    public Task InitializeAsync()
    {

        _dbPath = _fixture.CopyDatabase();

        _db = _fixture.CreateContext(_dbPath);

        _service = new LexiconService(_db, NullLogger<LexiconService>.Instance);

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

    }

    [SkippableFact]
    public async Task UpsertAsync_CreatesNewEntityWithGeneralType()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Result<LexiconEntryDto> result = await _service!.UpsertAsync("Alice", null, ["Prefers concise answers."], CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal("Alice", result.Value.Name);

        Assert.Equal(LexiconLimits.DefaultType, result.Value.Type);

        Assert.Single(result.Value.Facts);

        Assert.Equal("Prefers concise answers.", result.Value.Facts[0]);

    }

    [SkippableFact]

    public async Task UpsertAsync_AttachmentDerivedFact_RoundTripsTypedProvenanceAsUnavailableWhenSourceWasDeleted()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        Guid deletedAttachmentId = Guid.NewGuid();

        AttachmentMemoryProvenance provenance = new(
            sessionId,
            deletedAttachmentId,
            "privacy-policy",
            2,
            "content-hash",
            DateTimeOffset.Parse("2026-08-01T12:00:00Z"),
            "WorkspaceFile",
            AttachmentSourceAvailability.Available);

        Result<LexiconEntryDto> result = await _service!.UpsertAsync(
            "Privacy policy",
            "Document",
            ["Retention is thirty days."],
            provenance,
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Result<LexiconEntryDto?> reloaded = await _service.GetByNameAsync(
            "Privacy policy",
            CancellationToken.None);

        LexiconFactProvenance factProvenance = Assert.Single(
            reloaded.Value!.FactProvenance ?? []);

        Assert.Equal("Retention is thirty days.", factProvenance.Fact);

        Assert.Equal(deletedAttachmentId, factProvenance.Source.AttachmentId);

        Assert.Equal(AttachmentSourceAvailability.Unavailable, factProvenance.Source.Availability);

    }

    [SkippableFact]
    public async Task UpsertAsync_IsCaseInsensitiveByName()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await _service!.UpsertAsync("Alice", "Person", ["Likes blue."], CancellationToken.None);

        Result<LexiconEntryDto> second = await _service.UpsertAsync("ALICE", null, ["Works on Arcanum."], CancellationToken.None);

        Assert.True(second.IsSuccess);

        Assert.Equal("Person", second.Value.Type);

        Assert.Equal(2, second.Value.Facts.Length);

        Result<IReadOnlyList<LexiconEntryDto>> matches = await _service.MatchEntitiesAsync(["alice"], 10, CancellationToken.None);

        LexiconEntryDto entry = Assert.Single(matches.Value);

        Assert.Equal(2, entry.Facts.Length);

    }

    [SkippableFact]
    public async Task UpsertAsync_AppendsNonDuplicateFactsAndPreservesTypeWhenBlank()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await _service!.UpsertAsync("Project Phoenix", "Project", ["Uses PostgreSQL."], CancellationToken.None);

        Result<LexiconEntryDto> updated = await _service.UpsertAsync("project phoenix", "  ", ["Uses PostgreSQL.", "Deployment target is Linux."], CancellationToken.None);

        Assert.True(updated.IsSuccess);

        Assert.Equal("Project", updated.Value.Type);

        Assert.Equal(2, updated.Value.Facts.Length);

        Assert.Contains("Deployment target is Linux.", updated.Value.Facts);

    }

    [SkippableFact]
    public async Task UpsertAsync_RefreshesTypeWhenProvided()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await _service!.UpsertAsync("Bob", "Person", ["Initial."], CancellationToken.None);

        Result<LexiconEntryDto> updated = await _service.UpsertAsync("Bob", "Contributor", ["Another."], CancellationToken.None);

        Assert.Equal("Contributor", updated.Value.Type);

    }

    [SkippableFact]
    public async Task UpsertAsync_RejectsEmptyName()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Result<LexiconEntryDto> result = await _service!.UpsertAsync("  ", "Person", ["Fact."], CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Lexicon.InvalidName, result.Error.Code);

    }

    [SkippableFact]
    public async Task UpsertAsync_RejectsEmptyFacts()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Result<LexiconEntryDto> result = await _service!.UpsertAsync("Carol", "Person", ["  ", ""], CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Lexicon.InvalidFact, result.Error.Code);

    }

    [SkippableFact]
    public async Task DeleteByNameAsync_RemovesEntryAndFtsRow()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await _service!.UpsertAsync("Deletable", "Project", ["Will be removed."], CancellationToken.None);

        Result<bool> deleted = await _service.DeleteByNameAsync("deletable", CancellationToken.None);

        Assert.True(deleted.IsSuccess);

        Assert.True(deleted.Value);

        Result<IReadOnlyList<LexiconEntryDto>> matches = await _service.MatchEntitiesAsync(["deletable"], 10, CancellationToken.None);

        Assert.Empty(matches.Value);

        Result<bool> secondDelete = await _service.DeleteByNameAsync("deletable", CancellationToken.None);

        Assert.True(secondDelete.IsSuccess);

        Assert.False(secondDelete.Value);

    }

    /// <summary>
    /// <c>DELETE /api/memory/lexicon/{name}</c> hands the handler <c>context.RequestAborted</c>, so an
    /// aborted request cancels the token while <c>BEGIN IMMEDIATE</c> already holds the RESERVED write
    /// lock. Issuing the <c>ROLLBACK</c> on that same token means <c>ExecuteNonQueryAsync</c> returns a
    /// cancelled task before any SQL is sent, so the deterministic release the code exists to perform
    /// never happens and the transaction is stranded on the pooled context's shared connection.
    /// </summary>
    [SkippableFact]
    public async Task DeleteByNameAsync_RollsBackWhenTheCallerCancelsMidTransaction()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await _service!.UpsertAsync("Abandoned", "Project", ["Written before the abort."], CancellationToken.None);

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        using CancellationTokenSource cts = new();

        bool armed = true;

        // SQLite's trace hook fires while BEGIN IMMEDIATE is executing — after DbCommand has already
        // checked the token — so the write lock is genuinely taken and only the statements after it
        // observe the cancellation. That is exactly the aborted-request window.
        strdelegate_trace trace = (object _, string statement) =>
        {

            if (armed && statement.Contains("BEGIN IMMEDIATE", StringComparison.Ordinal))
            {

                armed = false;

                cts.Cancel();

            }

        };

        raw.sqlite3_trace(connection.Handle, trace, null);

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _service.DeleteByNameAsync("abandoned", cts.Token));

        // sqlite3_get_autocommit returns 0 while an explicit transaction is open.
        Assert.NotEqual(0, raw.sqlite3_get_autocommit(connection.Handle));

    }

    [SkippableFact]
    public async Task MatchEntitiesAsync_ExactNameHitsSurfaceBeforeFtsHits()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await _service!.UpsertAsync("Alice", "Person", ["Works on Arcanum."], CancellationToken.None);

        await _service.UpsertAsync("Project Phoenix", "Project", ["Uses PostgreSQL for persistence."], CancellationToken.None);

        // "Alice" resolves by exact name; "PostgreSQL" is unresolved (no entity named PostgreSQL)
        // so it falls through to FTS, which matches Project Phoenix via FactsText.
        Result<IReadOnlyList<LexiconEntryDto>> matches = await _service.MatchEntitiesAsync(["Alice", "PostgreSQL"], 10, CancellationToken.None);

        Assert.Equal(2, matches.Value.Count);

        Assert.Equal("Alice", matches.Value[0].Name);

        Assert.Equal("Project Phoenix", matches.Value[1].Name);

    }

    [SkippableFact]
    public async Task MatchEntitiesAsync_MatchesByFactTextViaFts()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await _service!.UpsertAsync("Project Phoenix", "Project", ["Uses PostgreSQL for persistence."], CancellationToken.None);

        Result<IReadOnlyList<LexiconEntryDto>> matches = await _service.MatchEntitiesAsync(["PostgreSQL"], 10, CancellationToken.None);

        LexiconEntryDto entry = Assert.Single(matches.Value);

        Assert.Equal("Project Phoenix", entry.Name);

    }

    [SkippableFact]
    public async Task MatchEntitiesAsync_EmptyEntityListReturnsEmpty()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Result<IReadOnlyList<LexiconEntryDto>> matches = await _service!.MatchEntitiesAsync([], 10, CancellationToken.None);

        Assert.True(matches.IsSuccess);

        Assert.Empty(matches.Value);

    }

    [SkippableFact]
    public async Task MatchEntitiesAsync_SanitizesFtsSpecialCharacters()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await _service!.UpsertAsync("Stable", "Project", ["Holds daemon_state."], CancellationToken.None);

        Result<IReadOnlyList<LexiconEntryDto>> matches = await _service.MatchEntitiesAsync(["daemon_state:foo OR *"], 10, CancellationToken.None);

        Assert.True(matches.IsSuccess);

    }

    [SkippableFact]
    public async Task UpdateFtsText_RetiresOldFactAndIndexesNewFact()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await _service!.UpsertAsync("Eve", "Person", ["Old fact about turtles."], CancellationToken.None);

        Result<IReadOnlyList<LexiconEntryDto>> oldMatch = await _service.MatchEntitiesAsync(["turtles"], 10, CancellationToken.None);

        Assert.Single(oldMatch.Value);

        await _service.UpsertAsync("Eve", null, ["New fact about parrots."], CancellationToken.None);

        Result<IReadOnlyList<LexiconEntryDto>> parrotMatch = await _service.MatchEntitiesAsync(["parrots"], 10, CancellationToken.None);

        Assert.Single(parrotMatch.Value);

    }

    [SkippableFact]
    public async Task GetByNameAsync_ReturnsNullWhenMissing()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Result<LexiconEntryDto?> result = await _service!.GetByNameAsync("Nobody", CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Null(result.Value);

    }

    [SkippableFact]
    public async Task UpsertAsync_preserves_facts_beyond_the_former_request_total()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        List<string> many = Enumerable.Range(0, FormerMaxFactsPerUpsert + 5)
            .Select(i => $"fact {i}")
            .ToList();

        Result<LexiconEntryDto> result = await _service!.UpsertAsync(
            "Uncapped",
            "Project",
            many,
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(many, result.Value.Facts);

    }

    [SkippableFact]
    public async Task UpsertAsync_preserves_old_and_new_facts_beyond_the_former_retained_total()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        List<string> allFacts = Enumerable
            .Range(0, FormerMaxFactsRetainedPerEntry + 1)
            .Select(static index => $"durable fact {index}")
            .ToList();

        foreach (string[] page in allFacts.Chunk(FormerMaxFactsPerUpsert))
        {

            Result<LexiconEntryDto> appended = await _service!.UpsertAsync(
                "Durable",
                "Project",
                page,
                CancellationToken.None);

            Assert.True(appended.IsSuccess);

        }

        Result<LexiconEntryDto?> reloaded = await _service!.GetByNameAsync(
            "Durable",
            CancellationToken.None);

        Assert.True(reloaded.IsSuccess);

        Assert.Equal(allFacts, reloaded.Value!.Facts);

    }

    [SkippableFact]
    public async Task UpsertAsync_preserves_fact_text_beyond_the_former_scalar_limit()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string longFact = new('x', FormerMaxFactLength + 257);

        Result<LexiconEntryDto> result = await _service!.UpsertAsync(
            "Long fact",
            "Project",
            [longFact],
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(longFact, Assert.Single(result.Value.Facts));

    }

    [SkippableFact]

    public async Task ListAsync_ReturnsEveryEntityInStableNameOrder()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _ = await _service!.UpsertAsync(
            "Zebra",
            "Project",
            ["Last alphabetically."],
            CancellationToken.None);

        _ = await _service.UpsertAsync(
            "alpha",
            "Person",
            ["First alphabetically."],
            CancellationToken.None);

        Result<IReadOnlyList<LexiconEntryDto>> result = await _service
            .ListAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(
            ["alpha", "Zebra"],
            result.Value.Select(static entry => entry.Name).ToArray());

    }

    /// <summary>
    /// Provenance is written only when a call carries an attachment, and facts are append-only, so an
    /// upsert that leaves the provenance rows exactly as they already are has nothing to write. The
    /// write path deleted every row for the entry and re-inserted the survivors one freshly-prepared
    /// statement at a time regardless — inside the <c>BEGIN IMMEDIATE</c> critical section, on every
    /// single upsert. The Unseen Servant rewrites its <c>daemon_state</c> entry on every waking cycle
    /// and that entry is never deletable, so the churn has no bound.
    /// </summary>
    [SkippableFact]
    public async Task UpsertAsync_DoesNotRewriteProvenanceWhenNothingAboutItChanged()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        AttachmentMemoryProvenance provenance = new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "notes.md",
            1,
            "content-hash",
            DateTimeOffset.Parse("2026-08-01T12:00:00Z"),
            "WorkspaceFile",
            AttachmentSourceAvailability.Available);

        Result<LexiconEntryDto> seeded = await _service!.UpsertAsync(
            "Aetherium",
            "Project",
            ["Ships on Tuesday."],
            provenance,
            CancellationToken.None);

        Assert.True(seeded.IsSuccess);

        List<string> statements = CaptureStatements();

        // Same entity, same fact, same attachment: the stored rows already say exactly this.
        Result<LexiconEntryDto> repeated = await _service.UpsertAsync(
            "Aetherium",
            "Project",
            ["Ships on Tuesday."],
            provenance,
            CancellationToken.None);

        Assert.True(repeated.IsSuccess);

        // And an append with no attachment at all — the daemon's shape — leaves provenance untouched.
        Result<LexiconEntryDto> appended = await _service.UpsertAsync(
            "Aetherium",
            "Project",
            ["Also ships on Wednesday."],
            CancellationToken.None);

        Assert.True(appended.IsSuccess);

        Assert.DoesNotContain(statements, IsProvenanceWrite);

        Result<LexiconEntryDto?> reloaded = await _service.GetByNameAsync("Aetherium", CancellationToken.None);

        LexiconFactProvenance surviving = Assert.Single(reloaded.Value!.FactProvenance ?? []);

        Assert.Equal("Ships on Tuesday.", surviving.Fact);

        Assert.Equal(provenance.AttachmentId, surviving.Source.AttachmentId);

    }

    private static bool IsProvenanceWrite(string statement) =>
        statement.Contains("lexicon_fact_attachment_provenance", StringComparison.Ordinal)
        && (statement.Contains("DELETE", StringComparison.Ordinal)
            || statement.Contains("INSERT", StringComparison.Ordinal));

    [SkippableFact]
    public async Task ListAsync_HydratesProvenanceForEveryEntityWithOneQuery()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedProvenancedEntitiesAsync(SeededEntityCount);

        List<string> statements = CaptureStatements();

        Result<IReadOnlyList<LexiconEntryDto>> result = await _service!.ListAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(SeededEntityCount, result.Value.Count);

        AssertProvenanceMatchesFacts(result.Value);

        Assert.Equal(1, CountProvenanceQueries(statements));

    }

    [SkippableFact]
    public async Task MatchEntitiesAsync_HydratesProvenanceForEveryMatchWithOneQuery()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string[] names = await SeedProvenancedEntitiesAsync(SeededEntityCount);

        List<string> statements = CaptureStatements();

        Result<IReadOnlyList<LexiconEntryDto>> matches = await _service!.MatchEntitiesAsync(
            names,
            10,
            CancellationToken.None);

        Assert.True(matches.IsSuccess);

        Assert.Equal(SeededEntityCount, matches.Value.Count);

        AssertProvenanceMatchesFacts(matches.Value);

        Assert.Equal(1, CountProvenanceQueries(statements));

    }

    private const int SeededEntityCount = 5;

    private async Task<string[]> SeedProvenancedEntitiesAsync(int count)
    {

        Guid sessionId = Guid.NewGuid();

        string[] names = new string[count];

        for (int index = 0; index < count; index++)
        {

            AttachmentMemoryProvenance provenance = new(
                sessionId,
                Guid.NewGuid(),
                $"source-{index}",
                1,
                $"content-hash-{index}",
                DateTimeOffset.Parse("2026-08-01T12:00:00Z"),
                "WorkspaceFile",
                AttachmentSourceAvailability.Available);

            names[index] = $"Entity {index}";

            Result<LexiconEntryDto> seeded = await _service!.UpsertAsync(
                names[index],
                "Project",
                [$"Fact {index}."],
                provenance,
                CancellationToken.None);

            Assert.True(seeded.IsSuccess);

        }

        return names;

    }

    private static void AssertProvenanceMatchesFacts(IReadOnlyList<LexiconEntryDto> entries)
    {

        foreach (LexiconEntryDto entry in entries)
        {

            LexiconFactProvenance provenance = Assert.Single(entry.FactProvenance ?? []);

            Assert.Equal(Assert.Single(entry.Facts), provenance.Fact);

        }

    }

    /// <summary>
    /// Records every statement SQLite prepares on the fixture connection from this point on. Batched
    /// provenance hydration is indistinguishable from the per-entry (N+1) shape in the returned DTOs,
    /// so the only place the regression is observable is the statement stream itself.
    /// </summary>
    private List<string> CaptureStatements()
    {

        List<string> statements = [];

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        strdelegate_trace trace = (object _, string statement) => statements.Add(statement);

        raw.sqlite3_trace(connection.Handle, trace, null);

        return statements;

    }

    private static int CountProvenanceQueries(List<string> statements) =>
        statements.Count(static statement =>
            statement.Contains("lexicon_fact_attachment_provenance", StringComparison.Ordinal));

    /// <summary>
    /// A rollback that fails in an unanticipated way must not replace the failure that caused it.
    /// </summary>
    /// <remarks>
    /// Both callers are shaped <c>catch { await TryRollbackAsync(...); throw; }</c>, and an exception
    /// raised inside a catch block discards the one being handled. The outer handler would then see the
    /// rollback's exception instead of the real failure — and because it filters on
    /// <c>ex is not OperationCanceledException</c>, an aborted request whose rollback threw would stop
    /// being reported as a cancellation and start being reported as a generic Lexicon write failure.
    /// Best-effort has to mean best-effort for every failure kind, not for three enumerated ones.
    /// </remarks>
    [Fact]
    public async Task A_rollback_that_fails_unexpectedly_never_replaces_the_original_failure()
    {

        MethodInfo rollback = typeof(LexiconService).GetMethod(
                "TryRollbackAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("TryRollbackAsync is no longer where this suite expects it.");

        using HostileConnection connection = new();

        // NotSupportedException is neither one of the three anticipated kinds nor derived from any.
        await (Task)rollback.Invoke(_service!, [connection, "entity"])!;

        Assert.True(connection.RollbackAttempted);

    }

    /// <summary>
    /// A connection whose command execution fails in a way the rollback's author did not enumerate.
    /// </summary>
    private sealed class HostileConnection : DbConnection
    {

        public bool RollbackAttempted { get; private set; }

        [AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;

        public override string Database => string.Empty;

        public override string DataSource => string.Empty;

        public override string ServerVersion => string.Empty;

        public override ConnectionState State => ConnectionState.Open;

        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

        public override void Close()
        {
        }

        public override void Open()
        {
        }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
            throw new NotSupportedException();

        protected override DbCommand CreateDbCommand()
        {

            RollbackAttempted = true;

            return new HostileCommand(this);

        }

    }

    private sealed class HostileCommand(DbConnection connection) : DbCommand
    {

        [AllowNull]
        public override string CommandText { get; set; } = string.Empty;

        public override int CommandTimeout { get; set; }

        public override CommandType CommandType { get; set; } = CommandType.Text;

        public override bool DesignTimeVisible { get; set; }

        public override UpdateRowSource UpdatedRowSource { get; set; }

        protected override DbConnection? DbConnection { get; set; } = connection;

        protected override DbParameterCollection DbParameterCollection { get; } = new HostileParameterCollection();

        protected override DbTransaction? DbTransaction { get; set; }

        public override void Cancel()
        {
        }

        public override int ExecuteNonQuery() =>
            throw new NotSupportedException("The provider refused the statement outright.");

        public override object? ExecuteScalar() => throw new NotSupportedException();

        public override void Prepare() => throw new NotSupportedException();

        protected override DbParameter CreateDbParameter() => throw new NotSupportedException();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
            throw new NotSupportedException();

    }

    private sealed class HostileParameterCollection : DbParameterCollection
    {

        private readonly List<object> _items = [];

        public override int Count => _items.Count;

        public override object SyncRoot => _items;

        public override int Add(object value)
        {

            _items.Add(value);

            return _items.Count - 1;

        }

        public override void AddRange(Array values) => throw new NotSupportedException();

        public override void Clear() => _items.Clear();

        public override bool Contains(object value) => _items.Contains(value);

        public override bool Contains(string value) => false;

        public override void CopyTo(Array array, int index) => throw new NotSupportedException();

        public override IEnumerator GetEnumerator() => _items.GetEnumerator();

        public override int IndexOf(object value) => _items.IndexOf(value);

        public override int IndexOf(string parameterName) => -1;

        public override void Insert(int index, object value) => _items.Insert(index, value);

        public override void Remove(object value) => _items.Remove(value);

        public override void RemoveAt(int index) => _items.RemoveAt(index);

        public override void RemoveAt(string parameterName) => throw new NotSupportedException();

        protected override DbParameter GetParameter(int index) => throw new NotSupportedException();

        protected override DbParameter GetParameter(string parameterName) => throw new NotSupportedException();

        protected override void SetParameter(int index, DbParameter value) => throw new NotSupportedException();

        protected override void SetParameter(string parameterName, DbParameter value) =>
            throw new NotSupportedException();

    }

}
