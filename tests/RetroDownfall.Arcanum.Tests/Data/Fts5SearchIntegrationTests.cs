using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

[Collection("Grimoire")]
public sealed class Fts5SearchIntegrationTests(GrimoireFixture fixture) : IAsyncLifetime
{
    private static readonly string[] Corpus =
    [
        "schema migration bright",
        "schema ancient amber",
        "migration bright azure",
        "distant schema x x migration cobalt",
        "quoted \"beacon\" C++ café OR 𐐀𐐁",
    ];

    private string _databasePath = string.Empty;

    private ArcanumDbContext _db = null!;

    public static TheoryData<string, int> NativeQueries => new()
    {
        { "schema OR migration", 15 },
        { "schema AND migration", 9 },
        { "schema NOT migration", 2 },
        { "\"schema migration\"", 1 },
        { "schema + migration", 1 },
        { "migrat*", 13 },
        { "NEAR(schema migration, 0)", 1 },
        { "NEAR(schema migration, 2)", 9 },
        { "^schema", 3 },
        { "Content : (schema OR migration)", 15 },
        { "{Content}:(\"schema migration\" OR azure)", 5 },
        { "-{Content}:schema", 0 },
        { "Content:^schema + migration", 1 },
        { "(schema OR azure) NOT ancient", 13 },
        { "\"quoted \"\"beacon\"\"\"", 16 },
        { "\"C++\"", 16 },
        { "café", 16 },
        { "\"OR\"", 16 },
        { "\"schema migr\"*", 1 },
        { "Id:schema", 0 },
        { "\"\"", 0 },
        { "\"***\"", 0 },
        { "𐐀𐐁", 16 },
        { "schema OR migration AND azure", 15 },
    };

    public Task InitializeAsync()
    {
        _databasePath = fixture.CopyDatabase();

        _db = fixture.CreateContext(_databasePath);

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();

        File.Delete(_databasePath);
    }

    [SkippableTheory]
    [MemberData(nameof(NativeQueries))]
    public async Task Archive_search_preserves_native_FTS5_semantics(string query, int expectedMask)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _ = await SeedAsync();

        GrimoireRepository repository = new(
            _db,
            new NoOpSessionAttachmentStore(),
            NullLogger<GrimoireRepository>.Instance,
            new TestOptionsSnapshot<ArcanumSettings>(new ArcanumSettings()),
            covenantKernel: null,
            availabilityRepublisher: null,
            FixtureOrdinaryConnectionFactory.For(_db),
            FixtureLabeledArtifactGuard.For(_db), memoryDelegation: null);

        string result = (await repository.SearchArchivesAsync(query, 50, CancellationToken.None)).Value;

        for (int index = 0; index < Corpus.Length; index++)
        {
            Assert.True(result.Contains(Corpus[index], StringComparison.Ordinal) == ((expectedMask & (1 << index)) != 0),
                $"Query {query}, document {index}: expected mask {expectedMask}, actual result: {result}");
        }
    }

    [SkippableTheory]
    [MemberData(nameof(NativeQueries))]
    public async Task Session_search_preserves_native_FTS5_semantics(string query, int expectedMask)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid[] sessions = await SeedAsync();

        SessionRepository repository = new(_db, new NoOpSessionAttachmentStore(), fixture.CreateOptionsMonitor(), FixtureOrdinaryConnectionFactory.For(_db));

        SessionQueryResult result = (await repository.QueryAsync(new SessionQueryRequest(Search: query, Limit: 50), CancellationToken.None)).Value;

        Guid[] expected = sessions.Where((_, index) => (expectedMask & (1 << index)) != 0).Order().ToArray();

        Assert.Equal(expected, result.Summaries.Select(static item => item.Id).Order().ToArray());
    }

    [SkippableTheory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("schema OR")]
    [InlineData("\"schema")]
    [InlineData("unknown:schema")]
    [InlineData("NEAR(schema, nope)")]
    [InlineData("schema; DROP TABLE Entries")]
    public async Task Invalid_native_expression_is_a_typed_failure_even_without_entries(string query)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SessionRepository sessions = new(_db, new NoOpSessionAttachmentStore(), fixture.CreateOptionsMonitor(), FixtureOrdinaryConnectionFactory.For(_db));

        _ = await sessions.CreateAsync(null, query, CancellationToken.None);

        TestCapturingLogger<GrimoireRepository> logger = new();

        GrimoireRepository archives = new(
            _db, new NoOpSessionAttachmentStore(), logger,
            new TestOptionsSnapshot<ArcanumSettings>(new ArcanumSettings()),
            covenantKernel: null, availabilityRepublisher: null,
            FixtureOrdinaryConnectionFactory.For(_db), FixtureLabeledArtifactGuard.For(_db), memoryDelegation: null);

        object archiveResponse = await archives.SearchArchivesAsync(query, 50, CancellationToken.None);

        Result<string> archive = Assert.IsType<Result<string>>(archiveResponse);

        Assert.True(archive.IsFailure);

        Assert.Equal(ErrorCodes.Validation.InvalidQuery, archive.Error.Code);

        object sessionResponse = await sessions.QueryAsync(new SessionQueryRequest(Search: query), CancellationToken.None);

        Result<SessionQueryResult> session = Assert.IsType<Result<SessionQueryResult>>(sessionResponse);

        Assert.True(session.IsFailure);

        Assert.Equal(ErrorCodes.Validation.InvalidQuery, session.Error.Code);

        Assert.Empty(logger.Entries);
    }

    [SkippableFact]
    public async Task Expressions_at_the_length_boundary_are_preserved_and_overlong_expressions_are_rejected()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _ = await SeedAsync();

        SessionRepository sessions = new(_db, new NoOpSessionAttachmentStore(), fixture.CreateOptionsMonitor(), FixtureOrdinaryConnectionFactory.For(_db));

        GrimoireRepository archives = CreateArchives();

        int limit = new ArcanumSettings().ResolveIntelligence().ArchiveSearchMaxQueryLength;

        string expression = "schema" + new string(' ', limit - 9) + "OR";

        Assert.True((await archives.SearchArchivesAsync(expression + "x", 50)).IsSuccess);

        Assert.True((await sessions.QueryAsync(new SessionQueryRequest(Search: expression + "x"), CancellationToken.None)).IsSuccess);

        string overlong = "schema" + new string(' ', limit - 5) + "OR migration";

        Assert.Equal(ErrorCodes.Validation.InvalidQuery, (await archives.SearchArchivesAsync(overlong, 50)).Error.Code);

        Assert.Equal(ErrorCodes.Validation.InvalidQuery, (await sessions.QueryAsync(new SessionQueryRequest(Search: overlong), CancellationToken.None)).Error.Code);
    }

    [SkippableFact]
    public async Task Storage_failure_and_cancellation_are_not_misreported_as_invalid_or_empty_search()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SessionRepository sessions = new(_db, new NoOpSessionAttachmentStore(), fixture.CreateOptionsMonitor(), FixtureOrdinaryConnectionFactory.For(_db));

        GrimoireRepository archives = CreateArchives();

        using CancellationTokenSource canceled = new();

        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => archives.SearchArchivesAsync("schema", 50, canceled.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sessions.QueryAsync(new SessionQueryRequest(Search: "schema"), canceled.Token));

        await _db.Database.ExecuteSqlRawAsync("DROP TABLE Entries_fts");

        await Assert.ThrowsAsync<SqliteException>(() => archives.SearchArchivesAsync("schema", 50));

        await Assert.ThrowsAsync<SqliteException>(() => sessions.QueryAsync(new SessionQueryRequest(Search: "schema"), CancellationToken.None));
    }

    [SkippableFact]
    public async Task Valid_title_only_and_omitted_search_remain_additive_and_injection_cannot_change_storage()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SessionRepository sessions = new(_db, new NoOpSessionAttachmentStore(), fixture.CreateOptionsMonitor(), FixtureOrdinaryConnectionFactory.For(_db));

        Session titleOnly = await sessions.CreateAsync(null, "schema migration", CancellationToken.None);

        Assert.Equal(titleOnly.Id, Assert.Single((await sessions.QueryAsync(new SessionQueryRequest(Search: "schema"), CancellationToken.None)).Value.Summaries).Id);

        Result<string> injection = await CreateArchives().SearchArchivesAsync("schema'; DROP TABLE Entries; --", 50);

        Assert.Equal(ErrorCodes.Validation.InvalidQuery, injection.Error.Code);

        Assert.Equal(titleOnly.Id, Assert.Single((await sessions.QueryAsync(new SessionQueryRequest(), CancellationToken.None)).Value.Summaries).Id);

        Assert.True((await sessions.AddEntryAsync(titleOnly.Id, new Entry
        {
            Id = Guid.NewGuid(),
            Role = MessageRole.User,
            Content = "schema survived",
            ModelUsed = "test-model",
            CreatedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None)).IsSuccess);

        Assert.Contains("schema survived", (await CreateArchives().SearchArchivesAsync("schema", 50)).Value, StringComparison.Ordinal);
    }

    [SkippableTheory]
    [InlineData("*", false, false)]
    [InlineData("*", false, true)]
    [InlineData("*foo", false, false)]
    [InlineData("*foo", false, true)]
    [InlineData("*private-query-sentinel", false, false)]
    [InlineData("*private-query-sentinel", false, true)]
    [InlineData("foo*", true, false)]
    [InlineData("foo*", true, true)]
    [InlineData("\"*\"", true, false)]
    [InlineData("\"*\"", true, true)]
    [InlineData("\"*foo\"", true, false)]
    [InlineData("\"*foo\"", true, true)]
    public async Task Native_leading_star_is_actionable_without_disclosing_the_expression(string expression, bool valid, bool archive)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Assert.True(expression.Length < new ArcanumSettings().ResolveIntelligence().ArchiveSearchMaxQueryLength);

        TestCapturingLogger<GrimoireRepository> logger = new();

        Result result;

        if (archive)
        {
            GrimoireRepository repository = new(_db, new NoOpSessionAttachmentStore(), logger,
                new TestOptionsSnapshot<ArcanumSettings>(new ArcanumSettings()), covenantKernel: null, availabilityRepublisher: null,
                FixtureOrdinaryConnectionFactory.For(_db), FixtureLabeledArtifactGuard.For(_db), memoryDelegation: null);

            result = await repository.SearchArchivesAsync(expression, 50);
        }
        else
        {
            SessionRepository repository = new(_db, new NoOpSessionAttachmentStore(), fixture.CreateOptionsMonitor(), FixtureOrdinaryConnectionFactory.For(_db));

            result = await repository.QueryAsync(new SessionQueryRequest(Search: expression), CancellationToken.None);
        }

        Assert.Equal(valid, result.IsSuccess);

        if (!valid)
        {
            Assert.Equal(ErrorCodes.Validation.InvalidQuery, result.Error.Code);

            Assert.Contains("prefix", result.Error.Message, StringComparison.OrdinalIgnoreCase);

            Assert.Contains("quote", result.Error.Message, StringComparison.OrdinalIgnoreCase);

            Assert.DoesNotContain("unknown special query", result.Error.Message, StringComparison.Ordinal);

            Assert.DoesNotContain("private-query-sentinel", result.Error.Message, StringComparison.Ordinal);
        }

        Assert.Empty(logger.Entries);
    }

    [SkippableTheory]
    [InlineData(25, false)]
    [InlineData(25, true)]
    [InlineData(100, false)]
    [InlineData(100, true)]
    public async Task Native_parser_nesting_limit_is_actionable_without_disclosing_the_expression(int depth, bool archive)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string expression = new string('(', depth) + "schema" + new string(')', depth);

        Assert.True(expression.Length < new ArcanumSettings().ResolveIntelligence().ArchiveSearchMaxQueryLength);

        TestCapturingLogger<GrimoireRepository> logger = new();

        Result result;

        if (archive)
        {
            GrimoireRepository repository = new(_db, new NoOpSessionAttachmentStore(), logger,
                new TestOptionsSnapshot<ArcanumSettings>(new ArcanumSettings()), covenantKernel: null, availabilityRepublisher: null,
                FixtureOrdinaryConnectionFactory.For(_db), FixtureLabeledArtifactGuard.For(_db), memoryDelegation: null);

            result = await repository.SearchArchivesAsync(expression, 50);
        }
        else
        {
            SessionRepository repository = new(_db, new NoOpSessionAttachmentStore(), fixture.CreateOptionsMonitor(), FixtureOrdinaryConnectionFactory.For(_db));

            result = await repository.QueryAsync(new SessionQueryRequest(Search: expression), CancellationToken.None);
        }

        Assert.Equal(depth == 25, result.IsSuccess);

        if (depth == 100)
        {
            Assert.Equal(ErrorCodes.Validation.InvalidQuery, result.Error.Code);

            Assert.Contains("nesting", result.Error.Message, StringComparison.OrdinalIgnoreCase);

            Assert.DoesNotContain(expression, result.Error.Message, StringComparison.Ordinal);
        }

        Assert.Empty(logger.Entries);
    }

    private GrimoireRepository CreateArchives() => new(
        _db, new NoOpSessionAttachmentStore(), NullLogger<GrimoireRepository>.Instance,
        new TestOptionsSnapshot<ArcanumSettings>(new ArcanumSettings()), covenantKernel: null, availabilityRepublisher: null,
        FixtureOrdinaryConnectionFactory.For(_db), FixtureLabeledArtifactGuard.For(_db), memoryDelegation: null);

    private async Task<Guid[]> SeedAsync()
    {
        SessionRepository repository = new(_db, new NoOpSessionAttachmentStore(), fixture.CreateOptionsMonitor(), FixtureOrdinaryConnectionFactory.For(_db));

        List<Guid> sessions = [];

        for (int index = 0; index < Corpus.Length; index++)
        {
            Session session = await repository.CreateAsync(null, $"chronicle {index}", CancellationToken.None);

            sessions.Add(session.Id);

            _ = await repository.AddEntryAsync(session.Id, new Entry
            {
                Id = Guid.NewGuid(),
                Role = MessageRole.User,
                Content = Corpus[index],
                ModelUsed = "test-model",
                CreatedAt = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero).AddSeconds(index),
            }, CancellationToken.None);
        }

        return [.. sessions];
    }
}
