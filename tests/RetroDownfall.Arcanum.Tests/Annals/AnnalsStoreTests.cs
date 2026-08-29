using System.Globalization;

using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Annals;
using RetroDownfall.Arcanum.Infrastructure.Lexicon;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Annals;

/// <summary>
/// Reading claim history back.
/// </summary>
/// <remarks>
/// Every case reaches its starting state by writing through <see cref="ILexiconService"/> or
/// <see cref="ISagaMemoryStore"/> with the gate on. Nothing seeds a claim row directly, because a test
/// that seeds the state it asserts can never discover that production cannot produce it.
/// </remarks>
[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class AnnalsStoreTests : IAsyncLifetime
{

    private const int TestDimensions = 64;

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    public AnnalsStoreTests(GrimoireFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {

        _dbPath = _fixture.CopyDatabase();

        _db = _fixture.CreateContext(_dbPath);

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
    public async Task A_claim_is_readable_by_the_subject_row_it_describes()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _ = await InsertMemoryAsync(CreateSagaStore(annals: true), "mem-1", "a conclusion");

        AnnalClaimHead? head = await CreateStore().GetClaimAsync(
            AnnalSubjectStore.Saga,
            "mem-1",
            CancellationToken.None);

        Assert.NotNull(head);

        Assert.Equal(AnnalSubjectStore.Saga, head.SubjectStore);

        Assert.Equal("mem-1", head.SubjectId);

        Assert.Equal(1, head.CurrentRevision);

        Assert.Equal(AnnalOperation.Assert, head.CurrentOperation);

    }

    /// <summary>
    /// A row with no claim is what a memory written while the Annals was disabled looks like, and what
    /// every row looks like before the upgrade sweep drains. It is a state, not a failure.
    /// </summary>
    [SkippableFact]
    public async Task A_row_with_no_claim_reads_as_null_rather_than_failing()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _ = await InsertMemoryAsync(CreateSagaStore(annals: false), "mem-unclaimed", "a conclusion");

        Assert.Null(
            await CreateStore().GetClaimAsync(AnnalSubjectStore.Saga, "mem-unclaimed", CancellationToken.None));

    }

    [SkippableFact]
    public async Task Versions_come_back_oldest_revision_first()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        ILexiconService lexicon = CreateLexiconService();

        Result<LexiconEntryDto> written = await lexicon.UpsertAsync(
            "config", "Project", ["first"], LexiconScope.Global, CancellationToken.None);

        _ = await lexicon.UpsertAsync(
            "config", "Project", ["second"], LexiconScope.Global, CancellationToken.None);

        _ = await lexicon.UpsertAsync(
            "config", "Project", ["third"], LexiconScope.Global, CancellationToken.None);

        IAnnalsStore store = CreateStore();

        AnnalClaimHead head = (await store.GetClaimAsync(
            AnnalSubjectStore.Lexicon,
            written.Value.Id.ToString("N"),
            CancellationToken.None))!;

        IReadOnlyList<AnnalClaimVersion> versions =
            await store.GetVersionsAsync(head.ClaimId, CancellationToken.None);

        Assert.Equal([1, 2, 3], versions.Select(static version => version.Revision));

        Assert.Equal(AnnalOperation.Assert, versions[0].Operation);

        Assert.Equal(AnnalOperation.Correct, versions[2].Operation);

    }

    /// <summary>
    /// Transaction time is not stored twice. A version's belief ends where its successor's begins, and
    /// the newest version's is still open.
    /// </summary>
    [SkippableFact]
    public async Task A_versions_transaction_time_ends_where_its_successor_was_recorded()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        ILexiconService lexicon = CreateLexiconService();

        Result<LexiconEntryDto> written = await lexicon.UpsertAsync(
            "config", "Project", ["first"], LexiconScope.Global, CancellationToken.None);

        _ = await lexicon.UpsertAsync(
            "config", "Project", ["second"], LexiconScope.Global, CancellationToken.None);

        IAnnalsStore store = CreateStore();

        AnnalClaimHead head = (await store.GetClaimAsync(
            AnnalSubjectStore.Lexicon,
            written.Value.Id.ToString("N"),
            CancellationToken.None))!;

        IReadOnlyList<AnnalClaimVersion> versions =
            await store.GetVersionsAsync(head.ClaimId, CancellationToken.None);

        Assert.Equal(versions[1].RecordedAtUtc, versions[0].RecordedUntilUtc);

        Assert.Null(versions[1].RecordedUntilUtc);

        // Valid time is a separate statement about the world, and neither version closed it.
        Assert.Null(versions[0].ValidToUtc);

        Assert.Null(versions[1].ValidToUtc);

    }

    [SkippableFact]
    public async Task A_corrections_supersedes_edge_is_readable_in_ordinal_order()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        ILexiconService lexicon = CreateLexiconService();

        Result<LexiconEntryDto> written = await lexicon.UpsertAsync(
            "config", "Project", ["first"], LexiconScope.Global, CancellationToken.None);

        _ = await lexicon.UpsertAsync(
            "config", "Project", ["second"], LexiconScope.Global, CancellationToken.None);

        IAnnalsStore store = CreateStore();

        AnnalClaimHead head = (await store.GetClaimAsync(
            AnnalSubjectStore.Lexicon,
            written.Value.Id.ToString("N"),
            CancellationToken.None))!;

        IReadOnlyList<AnnalClaimVersion> versions =
            await store.GetVersionsAsync(head.ClaimId, CancellationToken.None);

        AnnalDependencyEdge edge = Assert.Single(
            await store.GetDependenciesAsync(versions[1].VersionId, CancellationToken.None));

        Assert.Equal(AnnalDependencyRelation.Supersedes, edge.Relation);

        Assert.Equal(1, edge.Ordinal);

        Assert.Equal(versions[0].VersionId, edge.DependencyVersionId);

        // Revision one asserted nothing before it, so it has no edges at all.
        Assert.Empty(await store.GetDependenciesAsync(versions[0].VersionId, CancellationToken.None));

    }

    private static Task<SagaMemoryWriteOutcome> InsertMemoryAsync(ISagaMemoryStore store, string id, string content) =>
        store.InsertAsync(
            id,
            content,
            DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture),
            sessionId: null,
            null,
            "extraction",
            new float[TestDimensions],
            CancellationToken.None);

    private IAnnalsStore CreateStore() => new AnnalsStore(_db!);

    private ISagaMemoryStore CreateSagaStore(bool annals) =>
        new SagaMemoryStore(
            _db!,
            new WeaveIndexAvailability(),
            new TestOptionsMonitor<ArcanumSettings>(
                new ArcanumSettings
                {
                    Features = new FeatureSettings { Annals = annals },
                    Integrations = new IntegrationSettings
                    {
                        Embeddings = new EmbeddingIntegrationSettings { Dimensions = TestDimensions },
                    },
                }));

    private ILexiconService CreateLexiconService() =>
        new LexiconService(
            _db!,
            NullLogger<LexiconService>.Instance,
            new TestOptionsMonitor<ArcanumSettings>(
                new ArcanumSettings { Features = new FeatureSettings { Annals = true } }));

}
