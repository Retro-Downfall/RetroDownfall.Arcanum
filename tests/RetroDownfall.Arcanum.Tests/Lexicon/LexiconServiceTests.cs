using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Lexicon;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Lexicon;

/// <summary>
/// <see cref="LexiconService"/> round-trip persistence against the real Grimoire schema (raw-SQL
/// <c>lexicon_entries</c> + FTS5 <c>lexicon_fts</c> created by <see cref="LexiconSchemaInitializer"/>).
/// </summary>
[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class LexiconServiceTests : IAsyncLifetime
{

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
    public async Task UpsertAsync_CapsFactsPerUpsert()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        List<string> many = Enumerable.Range(0, LexiconLimits.MaxFactsPerUpsert + 5)
            .Select(i => $"fact {i}")
            .ToList();

        Result<LexiconEntryDto> result = await _service!.UpsertAsync("Capped", "Project", many, CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(LexiconLimits.MaxFactsPerUpsert, result.Value.Facts.Length);

    }

}
