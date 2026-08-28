using System.Data.Common;
using System.Globalization;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
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

    /// <summary>
    /// One Campaign, two Sessions bound by the two production writers that disagreed about how to spell
    /// it, and a Campaign-scoped search that returns both of their memories rather than one.
    /// </summary>
    /// <remarks>
    /// <b>This counts what comes back, and that is the whole design of the case.</b> The defect was not
    /// that a column held the wrong text - it was that a join silently returned half of a Campaign's
    /// memories and reported nothing at all about the rest, so an assertion that a column now reads
    /// uppercase would have been green while recall stayed halved.
    ///
    /// <para><b>What reverts it is now more than one thing, and a mutation against this case needs to
    /// know which tree it is on.</b> On the shipped tree, reverting the turn-begin repository's rendering
    /// alone reds this case <i>at the seed</i>, with
    /// <c>session_campaign_bindings.CampaignId must be stored as an uppercase dashed 36-character
    /// identity</c> surfacing as "The session could not be created." - the guard refuses the binding
    /// before any memory is written, so what comes back is an abort and not a halved count. Measured.
    ///
    /// <para>To see the halving itself the guards have to come out too, and then the classifier decides
    /// it: with all four Campaign identity guards removed, reverting that writer alone leaves this case
    /// <b>green</b>, because <see cref="SagaMemoryScopeClassifier"/> canonicalizes the identity it hands
    /// on and a memory records the right Campaign whatever spelling the binding beside it holds.
    /// Reverting the classifier as well returns one memory where this demands two. All three measured,
    /// none inferred.</para>
    ///
    /// <para>The writer conversion is still load-bearing for the column's own exact reader, which is the
    /// Campaign memory reset's watermark selection, and that has a case of its own.</para>
    ///
    /// <para>Both Sessions are bound through a real writer rather than seeded.
    /// <c>CreateBoundSessionAsync</c> is the path every Session created since the binding table shipped
    /// took; the core data initializer's backfill is the path every Session that predates it took. They
    /// are the two halves, and a fixture stating either spelling itself would prove nothing about which
    /// half a reader can see.</para>
    ///
    /// <para>The embeddings are identical, so nothing about similarity can separate the two results.</para>
    /// </remarks>
    [Fact]
    public async Task A_campaign_scoped_search_returns_the_memories_of_both_binding_writers_sessions()
    {

        await SeedCampaignAsync(CampaignA);

        Guid boundByRepository = await SessionBindingWriters.BoundByTheRepositoryAsync(
            _db!, CampaignA, CancellationToken.None);

        Guid boundByInitializer = await SessionBindingWriters.BoundByTheInitializerAsync(
            _db!, CampaignA, CancellationToken.None);

        Assert.NotEqual(boundByRepository, boundByInitializer);

        float[] shared = Vec(1f);

        string fromRepository = await InsertAsync(boundByRepository, "a conclusion from a new session", shared);

        string fromInitializer = await InsertAsync(
            boundByInitializer, "a conclusion from an upgraded session", shared);

        Assert.Equal(Ordered(fromRepository, fromInitializer), await SearchAsync(CampaignA));

    }

    /// <summary>
    /// The operator listing draws on the same candidate set the search does, for Sessions bound either
    /// way.
    /// </summary>
    /// <remarks>
    /// A separate case rather than an extra assertion above, because it is a second reader with its own
    /// parameter binding: the listing and the search halved independently, and one of them being fixed
    /// says nothing about the other. "Inspection matches retrieval" is only true if both see both.
    /// </remarks>
    [Fact]
    public async Task A_campaign_scoped_listing_shows_the_memories_of_both_binding_writers_sessions()
    {

        await SeedCampaignAsync(CampaignA);

        float[] shared = Vec(1f);

        string fromRepository = await InsertAsync(
            await SessionBindingWriters.BoundByTheRepositoryAsync(_db!, CampaignA, CancellationToken.None),
            "a listed conclusion",
            shared);

        string fromInitializer = await InsertAsync(
            await SessionBindingWriters.BoundByTheInitializerAsync(_db!, CampaignA, CancellationToken.None),
            "another listed conclusion",
            shared);

        SagaMemoryDto[] listed = await _store!.ListAsync(
            query: null,
            sessionId: null,
            MemoryScope.Resolve(campaignScopingEnabled: true, CampaignA),
            limit: 50,
            offset: 0,
            CancellationToken.None);

        string[] listedIds = [.. listed.Select(static memory => memory.Id).Order(StringComparer.Ordinal)];

        Assert.Equal(Ordered(fromRepository, fromInitializer), listedIds);

    }

    /// <summary>
    /// A retirement recorded before the Campaign spelling was settled still refuses the memory it was
    /// made about.
    /// </summary>
    /// <remarks>
    /// <b>This is the one thing settling the column could have broken, and it would have broken it
    /// silently.</b> The Campaign identity is part of a suppression's preimage, and until version 5 the
    /// identity a retirement hashed was whichever spelling its Session's binding carried - the minority
    /// form for every Session created through the turn-begin path. A digest cannot be recomputed
    /// afterwards, because retirement deletes the content that is its preimage, so those rows are the
    /// only copy: a write path that asked only about the settled spelling would let the next extraction
    /// pass re-add exactly what an operator retired, and nothing would say so.
    ///
    /// <para>The suppression row is written directly rather than through <c>RetireAsync</c>, because the
    /// state under test is one no current writer can produce - version 5's guard refuses a memory
    /// carrying the minority Campaign spelling outright. The digest itself is computed by the production
    /// function over the value that path really hashed, so what is seeded is the row a shipped
    /// retirement left behind rather than a shape chosen to satisfy the check.</para>
    /// </remarks>
    [Fact]
    public async Task A_retirement_recorded_before_the_campaign_spelling_settled_still_suppresses_its_memory()
    {

        Guid session = await SeedCampaignSessionAsync(CampaignA);

        // Retiring something establishes the installation's suppression key, which nothing else creates.
        string retired = await InsertAsync(session, "a conclusion that goes", Vec(1f));

        SagaCurationOutcome outcome = await _store!.RetireAsync(
            retired,
            AnnalContentDigest.ForSagaMemory("a conclusion that goes"),
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        Assert.Equal(SagaCurationOutcomeKind.Applied, outcome.Kind);

        const string Legacy = "a conclusion retired before the spelling settled";

        // The spelling GrimoireRepository.InsertBindingAsync rendered before this change, which the Saga
        // store then copied into saga_memories.CampaignId and the retirement hashed.
        string legacyCampaign = CampaignA.ToString("D").ToLowerInvariant();

        byte[] key = Assert.IsType<byte[]>(
            await SagaSuppressionKeyStore.ReadAsync(
                (SqliteConnection)Connection, transaction: null, CancellationToken.None));

        await ExecuteAsync(
            """
            INSERT INTO saga_retirement_suppressions (
                SuppressionDigest, ScopeKindCode, CampaignId, RetiredAtUtc)
            VALUES ($digest, 2, $campaignId, $now);
            """,
            // The column and the digest are filled from one string, because a shipped retirement filled
            // them from one value: the memory's own stored CampaignId. Seeding the column canonical while
            // hashing the minority form described a row no installation ever held - harmless to the
            // assertion, which selects on the digest alone, and still a fixture claiming something false.
            ("$digest", SagaSuppressionDigest.Compute(
                key,
                SagaMemoryScopeKind.Campaign,
                legacyCampaign,
                Legacy)),
            ("$campaignId", legacyCampaign),
            ("$now", Timestamp));

        SagaMemoryWriteOutcome written = await _store.InsertAsync(
            Guid.NewGuid().ToString(),
            Legacy,
            DateTimeOffset.UtcNow,
            session,
            tags: null,
            source: "test",
            Vec(1f),
            CancellationToken.None);

        Assert.Equal(SagaMemoryWriteOutcome.Suppressed, written);

    }

    /// <summary>
    /// A memory retired before the Campaign spelling was settled can be un-retired, and the content it
    /// held is writable again afterwards.
    /// </summary>
    /// <remarks>
    /// <b>The sibling of the case above, and the half that was missed.</b> The write path was taught to
    /// ask for both digests and the release path was not, so an operator could retire a memory, upgrade,
    /// change their mind, and reinstate it - and the delete would match nothing, the suppression would
    /// stand, and the next extraction pass would refuse the content again with no error anywhere. Both
    /// paths now take their pair from one place, so they cannot drift apart a second time.
    ///
    /// <para><b>What this counts is the release, not the row.</b> It writes the same content back
    /// through the store afterwards and demands <see cref="SagaMemoryWriteOutcome.Written"/>. Asserting
    /// that a suppression row disappeared would pass a release that deleted the wrong row; only asking
    /// the write path whether the content is still refused proves the suppression is actually gone.</para>
    ///
    /// <para>The pre-upgrade state is built by replacing the suppression the production retirement just
    /// wrote with the one the same retirement would have written before the spelling settled - same key,
    /// same production digest function, same content and scope, the Campaign rendered the way the
    /// turn-begin repository rendered it. The memory row itself stays canonical, because the sweep
    /// repairs it; that asymmetry is exactly the state an upgraded installation is in.</para>
    /// </remarks>
    [Fact]
    public async Task A_memory_retired_before_the_campaign_spelling_settled_can_be_reinstated()
    {

        Guid session = await SeedCampaignSessionAsync(CampaignA);

        const string Content = "a conclusion the operator changed their mind about";

        string id = await InsertAsync(session, Content, Vec(1f));

        SagaCurationOutcome retired = await _store!.RetireAsync(
            id,
            AnnalContentDigest.ForSagaMemory(Content),
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        Assert.Equal(SagaCurationOutcomeKind.Applied, retired.Kind);

        string legacyCampaign = CampaignA.ToString("D").ToLowerInvariant();

        byte[] key = Assert.IsType<byte[]>(
            await SagaSuppressionKeyStore.ReadAsync(
                (SqliteConnection)Connection, transaction: null, CancellationToken.None));

        // Stand the row down to the shape a retirement made before the upgrade left behind.
        await ExecuteAsync("DELETE FROM saga_retirement_suppressions;");

        await ExecuteAsync(
            """
            INSERT INTO saga_retirement_suppressions (
                SuppressionDigest, ScopeKindCode, CampaignId, RetiredAtUtc)
            VALUES ($digest, 2, $campaignId, $now);
            """,
            ("$digest", SagaSuppressionDigest.Compute(
                key,
                SagaMemoryScopeKind.Campaign,
                legacyCampaign,
                Content)),
            ("$campaignId", legacyCampaign),
            ("$now", Timestamp));

        SagaCurationOutcome reinstated = await _store.ReinstateAsync(
            id,
            AnnalContentDigest.ForSagaMemory(Content),
            Vec(1f),
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        Assert.Equal(SagaCurationOutcomeKind.Applied, reinstated.Kind);

        SagaMemoryWriteOutcome rewritten = await _store.InsertAsync(
            Guid.NewGuid().ToString(),
            Content,
            DateTimeOffset.UtcNow,
            session,
            tags: null,
            source: "test",
            Vec(1f),
            CancellationToken.None);

        Assert.Equal(SagaMemoryWriteOutcome.Written, rewritten);

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

        _ = await _store!.InsertAsync(
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

        await SeedCampaignAsync(campaignId);

        return await SeedSessionAsync(campaignId, bindingKindCode: 2);

    }

    private Task SeedCampaignAsync(Guid campaignId) =>
        ExecuteAsync(
            """
            INSERT OR IGNORE INTO "Campaigns"
                ("Id", "Name", "NameLower", "Path", "Type", "Settings", "CreatedAt", "UpdatedAt")
            VALUES ($id, $name, $name, $path, 0, '{}', $now, $now);
            """,
            ("$id", Canonical(campaignId)),
            ("$name", campaignId.ToString("N")),
            ("$path", $"/campaigns/{campaignId:N}"),
            ("$now", Timestamp));

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
            ("$id", Canonical(sessionId)),
            ("$campaignId", campaignId is { } bound ? Canonical(bound) : null),
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
            // Canonical, because the foreign key leaves no choice: session_campaign_bindings.SessionId
            // is declared REFERENCES "Sessions"("Id") and foreign keys are both set and verified on
            // every connection, so this column holds whatever the parent holds - and the parent is
            // written by the object-relational writer, which renders it uppercase.
            ("$id", Canonical(sessionId)),
            ("$kind", bindingKindCode),
            // Canonical, and the reason is now the same as the SessionId above rather than an asymmetry.
            // session_campaign_bindings.CampaignId still carries no foreign key - it is the historical
            // authority identity, so a Campaign deletion can clear its own row without rewriting it - but
            // its two writers no longer disagree: the core data initializer always canonicalized, and
            // GrimoireRepository.InsertBindingAsync now does too. The Saga store once copied that spelling
            // into saga_memories.CampaignId - it now canonicalizes the identity first, so a memory no
            // longer inherits it - and DivinationService and DataRetentionService bind that column back
            // exactly. This seed once rendered a bare ToString(), which is the spelling that made recall
            // return half of a Campaign's memories, and the version-5 guard now refuses it outright.
            ("$campaignId", bindingKindCode == 2 ? Canonical(campaignId!.Value) : null),
            ("$now", Timestamp));

        return sessionId;

    }

    /// <summary>
    /// The spelling every writer of these columns renders: uppercase, dashed, 36 characters.
    /// </summary>
    /// <remarks>
    /// The Campaign and the Session are written by the object-relational writer in production, which the
    /// SQLite value binder uppercases unconditionally, and <c>session_campaign_bindings</c> names both
    /// under a foreign key. A bare <c>ToString()</c> seeded the one spelling no writer produces.
    /// </remarks>
    private static string Canonical(Guid identity) => identity.ToString("D").ToUpperInvariant();

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
