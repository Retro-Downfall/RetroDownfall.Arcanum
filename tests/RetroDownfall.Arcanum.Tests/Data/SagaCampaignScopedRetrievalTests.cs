using System.Data.Common;
using System.Globalization;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

/// <summary>
/// Saga memory written inside one Campaign, and read back inside another.
/// </summary>
/// <remarks>
/// The scope is never handed to the store by its caller. It is derived from the owning Session's
/// canonical binding at write time, so a caller cannot state a Campaign the Session does not carry —
/// which is the same reason the turn path takes its Campaign from the resolved invocation context
/// rather than from anything in the request.
/// </remarks>
[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class SagaCampaignScopedRetrievalTests : IAsyncLifetime
{

    private const int TestDimensions = 64;

    private static readonly Guid CampaignA = new("A0000000-0000-4000-8000-00000000000A");

    private static readonly Guid CampaignB = new("B0000000-0000-4000-8000-00000000000B");

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    private SagaMemoryStore? _store;

    private WeaveIndexAvailability? _availability;

    public SagaCampaignScopedRetrievalTests(GrimoireFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {

        _dbPath = _fixture.CopyDatabase();

        _db = _fixture.CreateContext(_dbPath);

        _availability = new WeaveIndexAvailability();

        _store = new SagaMemoryStore(
            _db,
            _availability,
            new TestOptionsMonitor<ArcanumSettings>(
                new ArcanumSettings
                {
                    Integrations = new IntegrationSettings
                    {
                        Embeddings = new EmbeddingIntegrationSettings { Dimensions = TestDimensions },
                    },
                }));

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

    [Fact]
    public async Task A_memory_written_in_a_campaign_bound_session_records_that_campaign()
    {

        Guid session = await SeedCampaignSessionAsync(CampaignA);

        string id = await InsertAsync(session, "a conclusion", Vec(1f));

        Assert.Equal((SagaMemoryScopeKind.Campaign, CampaignA), await ReadScopeAsync(id));

    }

    [Fact]
    public async Task A_memory_written_in_a_global_only_session_records_global_scope()
    {

        Guid session = await SeedGlobalOnlySessionAsync();

        string id = await InsertAsync(session, "a conclusion", Vec(1f));

        Assert.Equal((SagaMemoryScopeKind.Global, (Guid?)null), await ReadScopeAsync(id));

    }

    [Fact]
    public async Task A_memory_written_without_a_session_records_global_scope()
    {

        string id = await InsertAsync(sessionId: null, "a conclusion", Vec(1f));

        Assert.Equal((SagaMemoryScopeKind.Global, (Guid?)null), await ReadScopeAsync(id));

    }

    /// <summary>
    /// A Session whose binding is unresolved supplies no authority, so a memory written under it takes
    /// none either. It is retrievable nowhere until an operator resolves the binding.
    /// </summary>
    [Fact]
    public async Task A_memory_written_in_a_legacy_unresolved_session_records_no_authority()
    {

        Guid session = await SeedLegacyUnresolvedSessionAsync();

        string id = await InsertAsync(session, "a conclusion", Vec(1f));

        Assert.Equal((SagaMemoryScopeKind.LegacyUnresolved, (Guid?)null), await ReadScopeAsync(id));

    }

    /// <summary>
    /// The acceptance criterion: Campaign A's conclusion is not a candidate inside Campaign B, and the
    /// installation-scoped one is a candidate in both.
    /// </summary>
    [Fact]
    public async Task A_campaign_scoped_search_sees_its_own_campaign_and_the_global_memories_only()
    {

        SeededCorpus corpus = await SeedCorpusAsync();

        Assert.Equal(
            Ordered(corpus.GlobalId, corpus.CampaignAId),
            await SearchAsync(CampaignA));

        Assert.Equal(
            Ordered(corpus.GlobalId, corpus.CampaignBId),
            await SearchAsync(CampaignB));

    }

    /// <summary>
    /// A turn that resolved to no Campaign draws on installation-scoped memory alone.
    /// </summary>
    [Fact]
    public async Task A_search_with_no_resolved_campaign_sees_only_the_global_memories()
    {

        SeededCorpus corpus = await SeedCorpusAsync();

        Assert.Equal(Ordered(corpus.GlobalId), await SearchAsync(campaignId: null));

    }

    /// <summary>
    /// Unresolved ownership is never installation-global, so an unresolved memory surfaces in no scope
    /// at all — including the one its own Session would have had.
    /// </summary>
    [Fact]
    public async Task An_unresolved_memory_is_a_candidate_in_no_scope()
    {

        SeededCorpus corpus = await SeedCorpusAsync();

        Assert.DoesNotContain(corpus.UnresolvedId, await SearchAsync(CampaignA));

        Assert.DoesNotContain(corpus.UnresolvedId, await SearchAsync(campaignId: null));

    }

    /// <summary>
    /// The filter must not depend on whether a native vector accelerator is present.
    /// </summary>
    /// <remarks>
    /// A predicate applied on only one of the two search paths would change what a turn recalls based on
    /// whether an optional asset shipped, which is the failure this criterion exists to catch. Running
    /// the same corpus and the same query under both availability states and demanding an identical,
    /// correctly scoped answer is what pins it: routing the scoped search through the unfiltered vec arm
    /// fails here even though every other test in this file still passes.
    /// </remarks>
    [Fact]
    public async Task A_campaign_scoped_search_answers_identically_whether_or_not_vec_is_available()
    {

        SeededCorpus corpus = await SeedCorpusAsync();

        _availability!.SetAvailable(false);

        string[] managed = await SearchAsync(CampaignA);

        _availability.SetAvailable(true);

        string[] accelerated = await SearchAsync(CampaignA);

        Assert.Equal(managed, accelerated);

        Assert.Equal(Ordered(corpus.GlobalId, corpus.CampaignAId), accelerated);

    }

    /// <summary>
    /// The acceptance criterion for retirement's structural exclusion: two memories the same
    /// Campaign-scoped search would otherwise rank identically, one of them retired, and only the
    /// survivor comes back. Retirement deletes the embedding rather than adding a predicate, so this
    /// proves the design by construction rather than by a filter every call site would have to agree
    /// about.
    /// </summary>
    [Fact]
    public async Task A_retired_memory_is_excluded_from_a_campaign_scoped_search()
    {

        Guid session = await SeedCampaignSessionAsync(CampaignA);

        float[] shared = Vec(1f);

        string survivorId = await InsertAsync(session, "a conclusion that stays", shared);

        string retiredId = await InsertAsync(session, "a conclusion that goes", shared);

        SagaCurationOutcome outcome = await _store!.RetireAsync(
            retiredId,
            AnnalContentDigest.ForSagaMemory("a conclusion that goes"),
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        Assert.Equal(SagaCurationOutcomeKind.Applied, outcome.Kind);

        Assert.Equal([survivorId], await SearchAsync(CampaignA));

    }

    private sealed record SeededCorpus(
        string GlobalId,
        string CampaignAId,
        string CampaignBId,
        string UnresolvedId);

    /// <summary>
    /// One memory per scope, every one of them written through the store so its classification is the
    /// store's rather than the suite's.
    /// </summary>
    /// <remarks>
    /// The embeddings are deliberately identical, so similarity ranking cannot be what separates the
    /// results. Anything a search returns, or fails to return, is the scope predicate's doing.
    /// </remarks>
    private async Task<SeededCorpus> SeedCorpusAsync()
    {

        float[] shared = Vec(1f);

        string globalId = await InsertAsync(sessionId: null, "an installation-scoped conclusion", shared);

        string campaignAId = await InsertAsync(
            await SeedCampaignSessionAsync(CampaignA),
            "a conclusion from campaign A",
            shared);

        string campaignBId = await InsertAsync(
            await SeedCampaignSessionAsync(CampaignB),
            "a conclusion from campaign B",
            shared);

        string unresolvedId = await InsertAsync(
            await SeedLegacyUnresolvedSessionAsync(),
            "a conclusion nobody owns",
            shared);

        return new SeededCorpus(globalId, campaignAId, campaignBId, unresolvedId);

    }

    /// <summary>
    /// The ids a search is expected to admit, in the same stable order <see cref="SearchAsync"/> returns.
    /// </summary>
    /// <remarks>
    /// Membership is the claim, not rank: every seeded memory carries the same embedding precisely so
    /// similarity cannot be what separates them. Ordering both sides the same way keeps the comparison
    /// about which memories are candidates.
    /// </remarks>
    private static string[] Ordered(params string[] ids) => [.. ids.Order(StringComparer.Ordinal)];

    /// <summary>Ids of every candidate the scoped search admits, ordered so a comparison is stable.</summary>
    private async Task<string[]> SearchAsync(Guid? campaignId)
    {

        DivinationService divination = new(
            _db!,
            _availability!,
            NullLogger<DivinationService>.Instance);

        Result<DivinationResult[]> search = await divination.SearchCampaignScopedAsync(
            "saga_memory_embeddings_vec",
            "MemoryId",
            "Embedding",
            SagaStorageKeys.CampaignScope(campaignId),
            new Embedding<float>(Vec(1f)),
            maxResults: 50,
            similarityThreshold: 0.5f,
            CancellationToken.None);

        Assert.True(search.IsSuccess);

        return [.. search.Value.Select(static hit => hit.Id).Order(StringComparer.Ordinal)];

    }

    private async Task<string> InsertAsync(Guid? sessionId, string content, float[] embedding)
    {

        string id = Guid.NewGuid().ToString();

        await _store!.InsertAsync(
            id,
            content,
            DateTimeOffset.UtcNow,
            sessionId,
            tags: null,
            source: "test",
            embedding,
            CancellationToken.None);

        return id;

    }

    private async Task<(SagaMemoryScopeKind Kind, Guid? CampaignId)> ReadScopeAsync(string memoryId)
    {

        await using DbCommand command = Connection.CreateCommand();

        command.CommandText = """SELECT ScopeKindCode, CampaignId FROM "saga_memories" WHERE "Id" = $id;""";

        DbParameter id = command.CreateParameter();

        id.ParameterName = "$id";

        id.Value = memoryId;

        command.Parameters.Add(id);

        await using DbDataReader reader = await command.ExecuteReaderAsync(CancellationToken.None);

        Assert.True(await reader.ReadAsync(CancellationToken.None));

        return (
            (SagaMemoryScopeKind)reader.GetInt32(0),
            reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1)));

    }

    private async Task<Guid> SeedCampaignSessionAsync(Guid campaignId)
    {

        await ExecuteAsync(
            """
            INSERT OR IGNORE INTO "Campaigns"
                ("Id", "Name", "NameLower", "Path", "Type", "Settings", "CreatedAt", "UpdatedAt")
            VALUES ($id, $name, $name, $path, 0, '{}', $now, $now);
            """,
            ("$id", campaignId.ToString()),
            ("$name", campaignId.ToString("N")),
            ("$path", $"/campaigns/{campaignId:N}"),
            ("$now", Timestamp));

        return await SeedSessionAsync(campaignId, bindingKindCode: 2);

    }

    private Task<Guid> SeedGlobalOnlySessionAsync() => SeedSessionAsync(null, bindingKindCode: 1);

    private Task<Guid> SeedLegacyUnresolvedSessionAsync() => SeedSessionAsync(null, bindingKindCode: 3);

    private async Task<Guid> SeedSessionAsync(Guid? campaignId, long bindingKindCode)
    {

        Guid sessionId = Guid.NewGuid();

        await ExecuteAsync(
            """
            INSERT INTO "Sessions" ("Id", "CampaignId", "Status", "CreatedAt", "UpdatedAt")
            VALUES ($id, $campaignId, 'active', $now, $now);
            """,
            ("$id", sessionId.ToString()),
            ("$campaignId", campaignId?.ToString()),
            ("$now", Timestamp));

        // The same false-by-default scope production borrows. Nothing may state a Session's authority
        // without it, this suite included.
        using CovenantSqliteAuthorizationScope scope = CovenantSqliteConnectionInitializer.Instance
            .Authorize((SqliteConnection)Connection, CovenantSqliteAuthorizationKind.SessionBindingWrite);

        await ExecuteAsync(
            """
            INSERT INTO session_campaign_bindings (SessionId, BindingKindCode, CampaignId, BoundAtUtc)
            VALUES ($id, $kind, $campaignId, $now);
            """,
            ("$id", sessionId.ToString()),
            ("$kind", bindingKindCode),
            ("$campaignId", bindingKindCode == 2 ? campaignId?.ToString() : null),
            ("$now", Timestamp));

        return sessionId;

    }

    private DbConnection Connection => _db!.Database.GetDbConnection();

    private static string Timestamp =>
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).ToString("o", CultureInfo.InvariantCulture);

    private async Task ExecuteAsync(string sql, params (string Name, object? Value)[] parameters)
    {

        await using DbCommand command = Connection.CreateCommand();

        command.CommandText = sql;

        foreach ((string name, object? value) in parameters)
        {

            DbParameter parameter = command.CreateParameter();

            parameter.ParameterName = name;

            parameter.Value = value ?? DBNull.Value;

            command.Parameters.Add(parameter);

        }

        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);

    }

    private static float[] Vec(params float[] leading)
    {

        float[] result = new float[TestDimensions];

        leading.AsSpan().CopyTo(result);

        return result;

    }

}
