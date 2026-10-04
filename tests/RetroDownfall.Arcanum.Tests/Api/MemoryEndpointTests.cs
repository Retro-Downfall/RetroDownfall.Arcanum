using System.Net;

using System.Net.Http.Json;

using System.Reflection;

using System.Text.Json;

using Microsoft.Data.Sqlite;

using Microsoft.EntityFrameworkCore;

using Microsoft.Extensions.AI;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.DependencyInjection.Extensions;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Api.Tower;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Lexicon;

using RetroDownfall.Arcanum.Core.Weave;

using RetroDownfall.Arcanum.Core.Memory;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Core.Storage.Entities;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Core.Tower;

using RetroDownfall.Arcanum.Infrastructure.Covenant;

using RetroDownfall.Arcanum.Tests.Covenant;

using RetroDownfall.Arcanum.Tests.Data;

using RetroDownfall.Arcanum.Tests.Data.Covenant;

using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Lexicon;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]

public sealed class MemoryEndpointTests
{
    [SkippableFact]
    public async Task Explain_excludes_retired_Lexicon_rows_while_operator_inspection_keeps_them()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using GrimoireFixture grimoire = new();

        await using CorrectionFixture owner = new(grimoire);

        await owner.SeedAsync();

        LexiconEntryDetail before = await owner.ShowAsync();

        Assert.True((await owner.Service.RetireAsync(before.Target, null)).IsSuccess);

        await using ArcanumWebApplicationFactory factory = new()
        {
            ServiceOverrides = services => services.AddSingleton<ILexiconCurationService>(owner.Service),
        };

        using HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage explained = await client.GetAsync("/api/memory/explain");

        ApiResponse<MemoryExplainDto>? explain = await ReadAsync(explained, ArcanumJsonContext.Default.ApiResponseMemoryExplainDto);

        Assert.False(EligibleSource(explain!.Data!, "Lexicon"));

        using HttpResponseMessage listed = await client.GetAsync("/api/memory/lexicon");

        ApiResponse<LexiconListDto>? list = await ReadAsync(listed, ArcanumJsonContext.Default.ApiResponseLexiconListDto);

        Assert.Equal(LexiconRetrievalEligibility.Retired, Assert.Single(list!.Data!.Entries).Eligibility);

        using HttpResponseMessage sourced = await client.GetAsync("/api/memory/sources");

        ApiResponse<MemorySourcesDto>? sources = await ReadAsync(sourced, ArcanumJsonContext.Default.ApiResponseMemorySourcesDto);

        Assert.Equal(1, Assert.Single(sources!.Data!.Sources, source => source.Name == "Lexicon").Count);

        LexiconEntryDetail retired = await owner.ShowAsync();

        Assert.True((await owner.Service.ReinstateAsync(retired.Target, null)).IsSuccess);

        using HttpResponseMessage reinstated = await client.GetAsync("/api/memory/explain");

        ApiResponse<MemoryExplainDto>? active = await ReadAsync(reinstated, ArcanumJsonContext.Default.ApiResponseMemoryExplainDto);

        Assert.True(EligibleSource(active!.Data!, "Lexicon"));
    }

    /// <summary>
    /// A Saga hit says whether a turn can still recall it, beside the text an operator found: the
    /// listing reads memory rows, so a retired memory matches a search exactly as a live one does.
    /// </summary>
    /// <remarks>
    /// Both memories are written through the store's own insert and curated through the mapped routes,
    /// so the lifecycle a hit carries is the one production recorded, not one the test stated.
    /// </remarks>
    [SkippableFact]
    public async Task Search_marks_retired_Saga_hits_with_lifecycle_and_eligibility()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateSagaEnabledFactory();

        using HttpClient client = factory.CreateAuthenticatedClient();

        await SeedSagaMemoryAsync(factory, "mem-live", "operator likes tea", sessionId: null);

        await SeedSagaMemoryAsync(factory, "mem-retired", "operator likes tea", sessionId: null);

        using HttpResponseMessage pinned = await client.PostAsync("/api/memory/saga/mem-live/pin", content: null);

        Assert.Equal(HttpStatusCode.OK, pinned.StatusCode);

        await RetireSagaMemoryAsync(client, "mem-retired");

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/memory/search",
            new MemorySearchRequest("tea", MemorySearchScope.Saga),
            ArcanumJsonContext.Default.MemorySearchRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string body = await response.Content.ReadAsStringAsync();

        ApiResponse<MemorySearchResponse>? envelope = JsonSerializer.Deserialize(
            body,
            ArcanumJsonContext.Default.ApiResponseMemorySearchResponse);

        MemorySearchResultDto live = Assert.Single(envelope!.Data!.Results, static hit => hit.SourceId == "mem-live");

        MemorySearchResultDto retired = Assert.Single(envelope.Data.Results, static hit => hit.SourceId == "mem-retired");

        Assert.Equal(SagaRetrievalEligibility.Eligible, live.SagaEligibility);

        Assert.NotNull(live.SagaLifecycle!.PinnedAtUtc);

        Assert.Null(live.SagaLifecycle.RetiredAtUtc);

        Assert.Equal(SagaRetrievalEligibility.Retired, retired.SagaEligibility);

        Assert.NotNull(retired.SagaLifecycle!.RetiredAtUtc);

        Assert.Contains("\"sagaEligibility\":\"Retired\"", body, StringComparison.Ordinal);

        Assert.EndsWith("; retired", retired.Provenance, StringComparison.Ordinal);

        Assert.False(live.Provenance.EndsWith("; retired", StringComparison.Ordinal));
    }

    /// <summary>
    /// Explain reports Saga as a next-turn source only when a turn in this scope could actually reach
    /// a memory: one that is not retired, still has an embedding, and is owned by this scope.
    /// </summary>
    /// <remarks>
    /// Status keeps counting what is stored, so the two answers diverge on purpose once a memory is
    /// retired: the row is still there, and no turn can recall it.
    /// </remarks>
    [SkippableFact]
    public async Task Explain_excludes_retired_and_out_of_scope_Saga_rows()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using (ArcanumWebApplicationFactory factory = CreateSagaEnabledFactory())
        {
            using HttpClient client = factory.CreateAuthenticatedClient();

            await SeedSagaMemoryAsync(factory, "mem-global", "an installation-scoped conclusion", sessionId: null);

            Assert.True(await SagaExplainEligibleAsync(client, sessionId: null));

            await RetireSagaMemoryAsync(client, "mem-global");

            Assert.False(await SagaExplainEligibleAsync(client, sessionId: null));

            using HttpResponseMessage statusResponse = await client.GetAsync("/api/memory/status");

            ApiResponse<MemoryStatusDto>? status = await ReadAsync(
                statusResponse,
                ArcanumJsonContext.Default.ApiResponseMemoryStatusDto);

            Assert.Equal(1, Assert.Single(status!.Data!.Stores, static store => store.Name == "Saga").Count);
        }

        Guid campaignA = new("A0000000-0000-4000-8000-0000000000E1");

        Guid campaignB = new("B0000000-0000-4000-8000-0000000000E2");

        await using (ArcanumWebApplicationFactory scoped = CreateSagaEnabledFactory(campaignScopedMemory: true))
        {
            using HttpClient client = scoped.CreateAuthenticatedClient();

            Guid sessionA = await SeedCampaignSessionAsync(scoped, campaignA);

            Guid sessionB = await SeedCampaignSessionAsync(scoped, campaignB);

            await SeedSagaMemoryAsync(scoped, "mem-b", "campaign B concluded something", sessionB);

            Assert.False(await SagaExplainEligibleAsync(client, sessionA));

            await SeedSagaMemoryAsync(scoped, "mem-global", "an installation-scoped conclusion", sessionId: null);

            Assert.True(await SagaExplainEligibleAsync(client, sessionA));
        }
    }

    /// <summary>
    /// A memory whose row survives but whose embedding does not is something no turn can rank, and
    /// both explain and search have to say so, while status keeps counting the row.
    /// </summary>
    /// <remarks>
    /// Retiring a memory removes its embedding in the same transaction, so the retirement cases cannot
    /// tell "retired" from "embedding gone"; this case removes only the embedding. It does so through
    /// the restore worker that drops vectors taken under another width, which is how a real
    /// installation reaches this state, rather than by deleting a row on the test's own account.
    /// </remarks>
    [SkippableFact]
    public async Task Explain_and_search_report_a_Saga_memory_whose_embedding_is_gone()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateSagaEnabledFactory();

        using HttpClient client = factory.CreateAuthenticatedClient();

        await SeedSagaMemoryAsync(factory, "mem-unembedded", "operator likes tea", sessionId: null);

        Assert.True(await SagaExplainEligibleAsync(client, sessionId: null));

        await DropSagaEmbeddingsTakenUnderAnotherWidthAsync(factory);

        Assert.False(await SagaExplainEligibleAsync(client, sessionId: null));

        using HttpResponseMessage statusResponse = await client.GetAsync("/api/memory/status");

        ApiResponse<MemoryStatusDto>? status = await ReadAsync(
            statusResponse,
            ArcanumJsonContext.Default.ApiResponseMemoryStatusDto);

        Assert.Equal(1, Assert.Single(status!.Data!.Stores, static store => store.Name == "Saga").Count);

        MemorySearchResultDto hit = Assert.Single(await SearchSagaAsync(client, "tea"));

        Assert.Equal("mem-unembedded", hit.SourceId);

        Assert.Equal(SagaRetrievalEligibility.EmbeddingMissing, hit.SagaEligibility);

        Assert.Null(hit.SagaLifecycle!.RetiredAtUtc);
    }

    /// <summary>
    /// Whether a memory nobody resolved the ownership of can be recalled depends on whether Campaign
    /// scoping is on, and every surface has to give the answer retrieval would.
    /// </summary>
    /// <remarks>
    /// With scoping off a turn ranks every embedded memory whoever owns it, so the memory is
    /// <c>Eligible</c> on the detail route and in search, and explain counts it. With scoping on it is
    /// retrievable nowhere: the detail route says <c>OwnershipUnresolved</c>, search, which lists by the
    /// same ownership a turn ranks by, does not return it, and explain does not count it.
    ///
    /// <para>The Session exists with no binding row, which is the state the scope classifier reads as
    /// "ownership never resolved"; the memory is written through the store's own insert.</para>
    /// </remarks>
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unresolved_ownership_is_reported_the_way_retrieval_treats_it(bool campaignScopedMemory)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateSagaEnabledFactory(campaignScopedMemory);

        using HttpClient client = factory.CreateAuthenticatedClient();

        Guid unbound = await SeedUnboundSessionAsync(factory);

        await SeedSagaMemoryAsync(factory, "mem-unresolved", "operator likes tea", unbound);

        using HttpResponseMessage shown = await client.GetAsync("/api/memory/saga/mem-unresolved");

        ApiResponse<SagaMemoryDetail>? detail = await ReadAsync(shown, ArcanumJsonContext.Default.ApiResponseSagaMemoryDetail);

        Assert.Equal(SagaMemoryScopeKind.LegacyUnresolved, detail!.Data!.Memory.ScopeKind);

        MemorySearchResultDto[] hits = await SearchSagaAsync(client, "tea");

        if (campaignScopedMemory)
        {
            Assert.Equal(SagaRetrievalEligibility.OwnershipUnresolved, detail.Data.Eligibility);

            Assert.DoesNotContain(hits, static hit => hit.SourceId == "mem-unresolved");

            Assert.False(await SagaExplainEligibleAsync(client, sessionId: null));
        }
        else
        {
            Assert.Equal(SagaRetrievalEligibility.Eligible, detail.Data.Eligibility);

            MemorySearchResultDto hit = Assert.Single(hits);

            Assert.Equal(SagaRetrievalEligibility.Eligible, hit.SagaEligibility);

            Assert.DoesNotContain("retrievable nowhere", hit.Provenance, StringComparison.Ordinal);

            Assert.True(await SagaExplainEligibleAsync(client, sessionId: null));
        }
    }

    [SkippableTheory]
    [InlineData(false, "purged")]
    [InlineData(true, "purged")]
    [InlineData(true, "blocked")]
    [InlineData(true, "failed")]
    public async Task Hard_delete_resolves_retired_protected_identity_before_conditional_purge(bool retired, string disposition)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using GrimoireFixture grimoire = new();

        await using CorrectionFixture owner = new(grimoire);

        await owner.SeedAsync();

        await owner.ProtectAsync();

        LexiconEntryDetail before = await owner.ShowProtectedAsync();

        using LeaseRegistration registration = new(CovenantLeaseKind.Write);

        await using CovenantWriteLease lease = new(registration);

        if (retired)
        {
            Assert.True((await owner.Service.RetireAsync(before.Target, lease)).IsSuccess);
        }

        string[] snapshot = await owner.SnapshotAsync();

        await using ArcanumDbContext purgeDb = grimoire.CreateContext(owner.Path);

        await using ArcanumWebApplicationFactory factory = new()
        {
            ServiceOverrides = services =>
            {
                services.AddSingleton<ILexiconService>(owner.Concrete);

                services.AddSingleton<ICovenantSensitiveArtifactPurger>(new LifecyclePurger(owner, purgeDb, disposition));
            },
        };

        HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.DeleteAsync("/api/memory/lexicon/Entity");

        if (disposition == "purged")
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

            Assert.Equal(0L, await owner.ScalarAsync("SELECT count(*) FROM artifact_sensitivity"));

            Assert.Equal(0L, await owner.ScalarAsync("SELECT count(*) FROM lexicon_entries"));

            Assert.Equal(0L, await owner.ScalarAsync("SELECT count(*) FROM annal_versions"));

            Assert.Equal(0L, await owner.ScalarAsync("SELECT count(*) FROM lexicon_annal_fact_provenance"));
        }
        else
        {
            Assert.False(response.IsSuccessStatusCode);

            Assert.Equal(snapshot, await owner.SnapshotAsync());
        }
    }

    [SkippableFact]
    public async Task Hard_delete_refuses_unavailable_all_lifecycle_identity_lookup()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new()
        {
            ServiceOverrides = services => services.AddSingleton<ILexiconService>(new LegacyDeletionLexicon()),
        };

        HttpResponseMessage response = await factory.CreateAuthenticatedClient().DeleteAsync("/api/memory/lexicon/Entity");

        Assert.False(response.IsSuccessStatusCode);

        string body = await response.Content.ReadAsStringAsync();

        Assert.Contains(ErrorCodes.Lexicon.SearchFailed, body, StringComparison.Ordinal);
    }

    private readonly ArcanumWebApplicationFactory _factory;

    public MemoryEndpointTests(ArcanumWebApplicationFactory factory)
    {

        _factory = factory;

    }

    [SkippableFact]

    public async Task Status_retains_read_only_admission_through_all_count_commands()
    {

        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        FixtureOrdinaryConnectionFactory connections = new();

        await using ArcanumWebApplicationFactory admitted = new()
        {
            ServiceOverrides = services =>
            {

                // Appended rather than substituted: the last registration is what
                // GetRequiredService returns, so the production descriptor stays composed.
                services.AddSingleton<IGrimoireOrdinaryConnectionFactory>(connections);

            },
        };

        HttpClient client = admitted.CreateAuthenticatedClient();

        using ScopedConsumerPause pause = new("MemoryEndpoints.CountWorkspaceChunksAsync");

        Task<HttpResponseMessage> loading = client.GetAsync("/api/memory/status");

        try
        {

            await pause.WaitUntilEnteredAsync();

            Assert.Equal(GrimoireScopedConsumerFinalUseKind.ScalarConverted, pause.FinalUse.Kind);

            Assert.Equal(0, pause.FinalUse.Observation);

            Assert.Equal(1, connections.LiveLeaseCountFor(CovenantSqliteConnectionMode.ReadOnly));

        }
        finally
        {

            pause.Release();

            _ = await loading.WaitAsync(TimeSpan.FromSeconds(10));

        }

        HttpResponseMessage response = await loading;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(CovenantSqliteConnectionMode.ReadOnly, connections.Modes[^1]);

        Assert.Equal(0, connections.LiveLeaseCountFor(CovenantSqliteConnectionMode.ReadOnly));

    }

    [SkippableFact]

    public async Task Search_retains_read_only_admission_while_its_result_reader_is_open()
    {

        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        FixtureOrdinaryConnectionFactory connections = new();

        await using ArcanumWebApplicationFactory admitted = new()
        {
            ServiceOverrides = services =>
            {

                // Appended rather than substituted: the last registration is what
                // GetRequiredService returns, so the production descriptor stays composed.
                services.AddSingleton<IGrimoireOrdinaryConnectionFactory>(connections);

            },
        };

        HttpClient client = admitted.CreateAuthenticatedClient();

        using ScopedConsumerPause pause = new("MemoryEndpoints.SearchSessionAsync");

        Task<HttpResponseMessage> searching = client.PostAsJsonAsync(
            "/api/memory/search",
            new MemorySearchRequest("not-present", MemorySearchScope.Session),
            ArcanumJsonContext.Default.MemorySearchRequest);

        try
        {

            await pause.WaitUntilEnteredAsync();

            Assert.Equal(GrimoireScopedConsumerFinalUseKind.ReaderMaterialized, pause.FinalUse.Kind);

            Assert.Equal(0, pause.FinalUse.Observation);

            Assert.Equal(1, connections.LiveLeaseCountFor(CovenantSqliteConnectionMode.ReadOnly));

        }
        finally
        {

            pause.Release();

            _ = await searching.WaitAsync(TimeSpan.FromSeconds(10));

        }

        HttpResponseMessage response = await searching;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(0, connections.LiveLeaseCountFor(CovenantSqliteConnectionMode.ReadOnly));

    }

    [SkippableFact]

    public async Task Status_reports_every_distinct_store_and_retention_without_requiring_features()
    {

        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync("/api/memory/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ApiResponse<MemoryStatusDto>? envelope = await ReadAsync(
            response,
            ArcanumJsonContext.Default.ApiResponseMemoryStatusDto);

        Assert.NotNull(envelope?.Data);

        string[] names = envelope.Data.Stores.Select(static store => store.Name).ToArray();

        Assert.Contains("Session Entries", names);

        Assert.Contains("Pinned Entries", names);

        Assert.Contains("Campaign Summary", names);

        Assert.Contains("Attachments", names);

        Assert.Contains("Indexed Attachment Chunks", names);

        Assert.Contains("Lexicon", names);

        Assert.Contains("Saga", names);

        Assert.Contains("Workspace Index", names);

        Assert.All(envelope.Data.Stores, static store => Assert.False(string.IsNullOrWhiteSpace(store.Retention)));

    }

    /// <summary>
    /// The retention sentence a store reports names every way its rows actually leave, not only the
    /// one that existed when the sentence was first written.
    /// </summary>
    /// <remarks>
    /// <para>Both stores used to say "explicitly deleted" and nothing else, which stopped being true
    /// twice over: an operator can erase a memory or an entry, leaving a fingerprint instead of a
    /// row, and an enabled retention rule prunes unpinned rows without anyone naming them. A reader
    /// who trusts the sentence would not look for the pin that exempts a row from the second or for
    /// the verb that performs the first.</para>
    /// <para>The Covenant has no row in this listing, so its dead constant has no business remaining
    /// in the endpoint class: a sentence nothing reads is one more place for the next change to
    /// leave stale.</para>
    /// </remarks>
    [SkippableFact]
    public async Task Memory_sources_state_each_store_retention_honestly()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using HttpClient client = _factory.CreateAuthenticatedClient();

        using HttpResponseMessage sourced = await client.GetAsync("/api/memory/sources");

        Assert.Equal(HttpStatusCode.OK, sourced.StatusCode);

        ApiResponse<MemorySourcesDto>? sources = await ReadAsync(
            sourced,
            ArcanumJsonContext.Default.ApiResponseMemorySourcesDto);

        Assert.NotNull(sources?.Data);

        string saga = Assert.Single(sources.Data.Sources, static source => source.Name == "Saga").Retention;

        Assert.Contains("erased", saga, StringComparison.Ordinal);

        Assert.Contains("saga-memories", saga, StringComparison.Ordinal);

        Assert.Contains("pinned", saga, StringComparison.Ordinal);

        Assert.Contains("explicitly", saga, StringComparison.Ordinal);

        string lexicon = Assert.Single(sources.Data.Sources, static source => source.Name == "Lexicon").Retention;

        Assert.Contains("erased", lexicon, StringComparison.Ordinal);

        Assert.Contains("lexicon-entries", lexicon, StringComparison.Ordinal);

        Assert.Contains("explicitly", lexicon, StringComparison.Ordinal);

        Assert.Null(
            typeof(MemoryEndpoints).GetField(
                "CovenantRetention",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic));
    }

    /// <summary>
    /// A Session's status counts report what its own stores actually hold, and its explain surface
    /// agrees with the counts it derives from.
    /// </summary>
    /// <remarks>
    /// This endpoint binds one Session identity across six columns, which do not agree on one
    /// spelling: <c>SessionAttachments.SessionId</c>, <c>Entries.SessionId</c> and
    /// <c>Sessions.Id</c> hold the canonical form while <c>session_attachment_chunks.SessionId</c>,
    /// <c>saga_memories.SessionId</c> and <c>tapestry_generations.ScopeId</c> deliberately hold the
    /// minority one. A single parameter served both groups, so a predicate bound to the canonical group
    /// compared a lowercase value against a canonical column and reported zero - a store an operator can
    /// see filling up, reported empty, with no error anywhere. <c>/memory/explain</c> derives its
    /// eligibility from these same counts, so it told the operator a store that was not empty was not
    /// eligible for a turn at all.
    ///
    /// <para>The attachment is written through <c>SessionAttachmentStore.PersistNewAsync</c> rather than
    /// seeded, because a seeded row can only ever agree with whatever the seed chose and that is how
    /// this defect family has stayed alive. The Session and Entry are seeded through EF, which renders
    /// the same canonical form the production write paths do. The assertions are on the numbers and on
    /// explain's eligibility flags, since the labels are present either way.</para>
    /// </remarks>
    [SkippableFact]

    public async Task A_session_status_counts_the_attachment_its_store_actually_wrote()
    {

        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId;

        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {

            ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

            Session session = new()
            {

                Id = Guid.NewGuid(),

                Status = "active",

                CreatedAt = DateTimeOffset.UtcNow,

                UpdatedAt = DateTimeOffset.UtcNow,

                Summary = "campaign summary text",

            };

            db.Sessions.Add(session);

            sessionId = session.Id;

            db.Entries.Add(new Entry
            {

                Id = Guid.NewGuid(),

                SessionId = sessionId,

                Role = MessageRole.User,

                Content = "pinned entry content",

                ModelUsed = "gpt-oracle",

                CreatedAt = DateTimeOffset.UtcNow,

                Sequence = 1,

                IsPinned = true,

            });

            _ = await db.SaveChangesAsync();

            ISessionAttachmentStore attachments =
                scope.ServiceProvider.GetRequiredService<ISessionAttachmentStore>();

            _ = await attachments.PersistNewAsync(
                sessionId,
                null,
                null,
                "counted",
                "counted.txt",
                System.Text.Encoding.UTF8.GetBytes("counted content"),
                "text/plain",
                SessionAttachmentKind.Text);

        }

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync(
            "/api/memory/status/" + sessionId.ToString("D"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ApiResponse<MemoryStatusDto>? envelope = await ReadAsync(
            response,
            ArcanumJsonContext.Default.ApiResponseMemoryStatusDto);

        Assert.NotNull(envelope?.Data);

        MemoryStoreStatusDto entriesStore = Assert.Single(
            envelope.Data.Stores,
            static store => string.Equals(store.Name, "Session Entries", StringComparison.Ordinal));

        Assert.Equal(1, entriesStore.Count);

        MemoryStoreStatusDto pinnedStore = Assert.Single(
            envelope.Data.Stores,
            static store => string.Equals(store.Name, "Pinned Entries", StringComparison.Ordinal));

        Assert.Equal(1, pinnedStore.Count);

        MemoryStoreStatusDto summaryStore = Assert.Single(
            envelope.Data.Stores,
            static store => string.Equals(store.Name, "Campaign Summary", StringComparison.Ordinal));

        Assert.Equal(1, summaryStore.Count);

        MemoryStoreStatusDto attachmentStore = Assert.Single(
            envelope.Data.Stores,
            static store => string.Equals(store.Name, "Attachments", StringComparison.Ordinal));

        Assert.Equal(1, attachmentStore.Count);

        HttpResponseMessage explainResponse = await client.GetAsync(
            "/api/memory/explain/" + sessionId.ToString("D"));

        Assert.Equal(HttpStatusCode.OK, explainResponse.StatusCode);

        ApiResponse<MemoryExplainDto>? explainEnvelope = await ReadAsync(
            explainResponse,
            ArcanumJsonContext.Default.ApiResponseMemoryExplainDto);

        Assert.NotNull(explainEnvelope?.Data);

        Assert.True(EligibleSource(explainEnvelope.Data, "Session Entries"));

        Assert.True(EligibleSource(explainEnvelope.Data, "Pinned Entries"));

        Assert.True(EligibleSource(explainEnvelope.Data, "Campaign Summary"));

    }

    private static bool EligibleSource(MemoryExplainDto explain, string name) =>
        Assert.Single(
            explain.Sources,
            source => string.Equals(source.Name, name, StringComparison.Ordinal)).Eligible;

    /// <summary>
    /// Session-scoped search matches an entry and a summary that belong to the session it was asked
    /// for, not some other one.
    /// </summary>
    /// <remarks>
    /// <c>SearchSessionAsync</c>'s two predicates bind the same canonical parameter the status counts
    /// above do. Every other <c>MemorySearchRequest</c> in this file omits <c>SessionId</c>, so before
    /// this test a regression in either predicate left the whole suite green while a caller-supplied
    /// session scope silently returned nothing.
    /// </remarks>
    [SkippableFact]

    public async Task Search_session_scope_binds_the_canonical_session_id()
    {

        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId;

        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {

            ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

            Session session = new()
            {

                Id = Guid.NewGuid(),

                Status = "active",

                CreatedAt = DateTimeOffset.UtcNow,

                UpdatedAt = DateTimeOffset.UtcNow,

                Summary = "gryphon roost morale notes",

            };

            db.Sessions.Add(session);

            sessionId = session.Id;

            db.Entries.Add(new Entry
            {

                Id = Guid.NewGuid(),

                SessionId = sessionId,

                Role = MessageRole.User,

                Content = "lantern coordinates for the lost expedition",

                ModelUsed = "gpt-oracle",

                CreatedAt = DateTimeOffset.UtcNow,

                Sequence = 1,

            });

            _ = await db.SaveChangesAsync();

        }

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage entryResponse = await client.PostAsJsonAsync(
            "/api/memory/search",
            new MemorySearchRequest("lantern", MemorySearchScope.Session, sessionId),
            ArcanumJsonContext.Default.MemorySearchRequest);

        Assert.Equal(HttpStatusCode.OK, entryResponse.StatusCode);

        ApiResponse<MemorySearchResponse>? entryEnvelope = await ReadAsync(
            entryResponse,
            ArcanumJsonContext.Default.ApiResponseMemorySearchResponse);

        MemorySearchResultDto entryMatch = Assert.Single(entryEnvelope!.Data!.Results);

        Assert.Equal(MemorySearchScope.Session, entryMatch.Scope);

        Assert.Null(entryMatch.Action);

        Assert.Contains("lantern coordinates", entryMatch.Content, StringComparison.Ordinal);

        HttpResponseMessage summaryResponse = await client.PostAsJsonAsync(
            "/api/memory/search",
            new MemorySearchRequest("morale", MemorySearchScope.Session, sessionId),
            ArcanumJsonContext.Default.MemorySearchRequest);

        Assert.Equal(HttpStatusCode.OK, summaryResponse.StatusCode);

        ApiResponse<MemorySearchResponse>? summaryEnvelope = await ReadAsync(
            summaryResponse,
            ArcanumJsonContext.Default.ApiResponseMemorySearchResponse);

        MemorySearchResultDto summaryMatch = Assert.Single(summaryEnvelope!.Data!.Results);

        Assert.Equal("Campaign Summary", summaryMatch.Title);

        Assert.Contains("morale notes", summaryMatch.Content, StringComparison.Ordinal);

    }

    [SkippableFact]

    public async Task Search_requires_query_but_not_an_embedding_feature_gate()
    {

        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage invalid = await client.PostAsJsonAsync(
            "/api/memory/search",
            new MemorySearchRequest("   "),
            ArcanumJsonContext.Default.MemorySearchRequest);

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        HttpResponseMessage invalidScope = await client.PostAsJsonAsync(
            "/api/memory/search",
            new MemorySearchRequest("query", (MemorySearchScope)99),
            ArcanumJsonContext.Default.MemorySearchRequest);

        Assert.Equal(HttpStatusCode.BadRequest, invalidScope.StatusCode);

        HttpResponseMessage valid = await client.PostAsJsonAsync(
            "/api/memory/search",
            new MemorySearchRequest("not-present", MemorySearchScope.All),
            ArcanumJsonContext.Default.MemorySearchRequest);

        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);

        ApiResponse<MemorySearchResponse>? envelope = await ReadAsync(
            valid,
            ArcanumJsonContext.Default.ApiResponseMemorySearchResponse);

        Assert.NotNull(envelope?.Data);

        Assert.Equal(MemorySearchScope.All, envelope.Data.Scope);

    }

    [SkippableFact]

    public async Task Lexicon_endpoints_list_show_search_and_delete_only_the_named_entity()
    {

        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        FakeLexiconService lexicon = new();

        _ = await lexicon.UpsertAsync(
            "Operator",
            "Person",
            ["Prefers dark mode."],
            LexiconScope.Global,
            CancellationToken.None);

        _ = await lexicon.UpsertAsync(
            "Arcanum",
            "Project",
            ["Uses C#."],
            LexiconScope.Global,
            CancellationToken.None);

        await using ArcanumWebApplicationFactory factory = new()
        {
            ServiceOverrides = services =>
            {

                services.RemoveAll<ILexiconService>();

                services.AddSingleton<ILexiconService>(lexicon);

                services.AddSingleton<ILexiconCurationService>(lexicon);

            },
        };

        HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage list = await client.GetAsync("/api/memory/lexicon");

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);

        ApiResponse<LexiconListDto>? listed = await ReadAsync(
            list,
            ArcanumJsonContext.Default.ApiResponseLexiconListDto);

        Assert.Equal(2, listed?.Data?.Entries.Length);

        HttpResponseMessage search = await client.GetAsync("/api/memory/lexicon?q=dark");

        ApiResponse<LexiconListDto>? searched = await ReadAsync(
            search,
            ArcanumJsonContext.Default.ApiResponseLexiconListDto);

        Assert.Single(searched!.Data!.Entries);

        HttpResponseMessage unifiedSearch = await client.PostAsJsonAsync(
            "/api/memory/search",
            new MemorySearchRequest("dark", MemorySearchScope.All),
            ArcanumJsonContext.Default.MemorySearchRequest);

        ApiResponse<MemorySearchResponse>? unified = await ReadAsync(
            unifiedSearch,
            ArcanumJsonContext.Default.ApiResponseMemorySearchResponse);

        MemorySearchResultDto match = Assert.Single(unified!.Data!.Results);

        Assert.Equal(MemorySearchScope.Lexicon, match.Scope);

        Assert.Equal(MemorySearchActionKind.ShowLexiconEntry, match.Action?.Kind);

        Assert.Equal("Operator", match.Action?.Lexicon?.Name);

        Assert.Equal(LexiconScopeKind.Global, match.Action?.Lexicon?.Scope.Kind);

        Assert.Null(match.Action?.Lexicon?.Scope.CampaignId);

        Assert.Contains("Lexicon entity: Operator", match.Provenance, StringComparison.Ordinal);

        Assert.Contains("explicit", match.Retention, StringComparison.OrdinalIgnoreCase);

        HttpResponseMessage show = await client.GetAsync("/api/memory/lexicon/Operator");

        Assert.Equal(HttpStatusCode.OK, show.StatusCode);

        HttpResponseMessage deleted = await client.DeleteAsync("/api/memory/lexicon/Operator");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        Result<LexiconEntryDto?> remainingOperator = await lexicon.GetByNameAsync("Operator", LexiconScope.Global);

        Result<LexiconEntryDto?> remainingArcanum = await lexicon.GetByNameAsync("Arcanum", LexiconScope.Global);

        Assert.Null(remainingOperator.Value);

        Assert.NotNull(remainingArcanum.Value);

    }

    [SkippableTheory]
    [InlineData("show")]
    [InlineData("correct")]
    [InlineData("retire")]
    [InlineData("reinstate")]
    [InlineData("pin")]
    [InlineData("unpin")]
    public async Task Static_curation_names_remain_ordinary_names_for_effective_GET_and_exact_DELETE(string name)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        FakeLexiconService lexicon = new();

        Assert.True((await lexicon.UpsertAsync(name, "Person", ["global"], LexiconScope.Global)).IsSuccess);

        await using ArcanumWebApplicationFactory factory = new()
        {
            ServiceOverrides = services =>
            {
                services.AddSingleton<ILexiconService>(lexicon);

                services.AddSingleton<ILexiconCurationService>(lexicon);
            },
        };

        using HttpClient client = factory.CreateAuthenticatedClient();

        Guid campaign = Guid.NewGuid();

        using HttpResponseMessage effective = await client.GetAsync($"/api/memory/lexicon/{name}?campaignId={campaign:D}");

        ApiResponse<LexiconEntryDto>? shown = await ReadAsync(effective, ArcanumJsonContext.Default.ApiResponseLexiconEntryDto);

        Assert.Equal(HttpStatusCode.OK, effective.StatusCode);

        Assert.Equal(["global"], shown!.Data!.Facts);

        using HttpResponseMessage absentExact = await client.DeleteAsync($"/api/memory/lexicon/{name}?campaignId={campaign:D}");

        Assert.Equal(HttpStatusCode.NotFound, absentExact.StatusCode);

        Assert.NotNull((await lexicon.GetByNameAsync(name, LexiconScope.Global)).Value);

        using HttpResponseMessage deleted = await client.DeleteAsync($"/api/memory/lexicon/{name}");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        Assert.Null((await lexicon.GetByNameAsync(name, LexiconScope.Global)).Value);
    }

    [SkippableFact]

    public async Task Search_bounds_the_saga_page_it_requests_and_caps_the_response()
    {

        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        // A store that answers with whatever it was asked for, so an unbounded request produces an
        // unbounded response — exactly what a broad query against a mature corpus does.
        SaturatingSagaMemoryStore saga = new(available: MemoryEndpoints.SearchResultLimit + 2_000);

        await using ArcanumWebApplicationFactory factory = new()
        {
            ServiceOverrides = services =>
            {

                services.RemoveAll<ISagaMemoryStore>();

                services.AddSingleton<ISagaMemoryStore>(saga);

            },
        };

        HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/memory/search",
            new MemorySearchRequest("e", MemorySearchScope.Saga),
            ArcanumJsonContext.Default.MemorySearchRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ApiResponse<MemorySearchResponse>? envelope = await ReadAsync(
            response,
            ArcanumJsonContext.Default.ApiResponseMemorySearchResponse);

        Assert.NotNull(envelope?.Data);

        // Budget + 1: the probe row is what separates "this scope filled its slice exactly" from
        // "this scope had more", and it is trimmed before the response is built. The bound that
        // matters — what the caller is made to hold in memory — is still exactly the budget.
        Assert.Equal(MemoryEndpoints.SearchResultLimit + 1, saga.RequestedLimit);

        Assert.Equal(MemoryEndpoints.SearchResultLimit, envelope.Data.Results.Length);

        Assert.All(
            envelope.Data.Results,
            match =>
            {
                Assert.Equal(MemorySearchActionKind.ShowSagaMemory, match.Action?.Kind);

                Assert.Equal(match.SourceId, match.Action?.Saga?.MemoryId);
            });

        Assert.True(envelope.Data.HasMore);

    }

    [SkippableFact]

    public async Task Search_shares_one_budget_across_scopes_rather_than_one_per_scope()
    {

        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        SaturatingSagaMemoryStore saga = new(available: MemoryEndpoints.SearchResultLimit + 2_000);

        FakeLexiconService lexicon = new();

        _ = await lexicon.UpsertAsync(
            "Operator",
            "Person",
            ["Prefers dark mode."],
            LexiconScope.Global,
            CancellationToken.None);

        await using ArcanumWebApplicationFactory factory = new()
        {
            ServiceOverrides = services =>
            {

                services.RemoveAll<ISagaMemoryStore>();

                services.AddSingleton<ISagaMemoryStore>(saga);

                services.RemoveAll<ILexiconService>();

                services.AddSingleton<ILexiconService>(lexicon);

                services.AddSingleton<ILexiconCurationService>(lexicon);

            },
        };

        HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/memory/search",
            new MemorySearchRequest("dark", MemorySearchScope.All),
            ArcanumJsonContext.Default.MemorySearchRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ApiResponse<MemorySearchResponse>? envelope = await ReadAsync(
            response,
            ArcanumJsonContext.Default.ApiResponseMemorySearchResponse);

        Assert.NotNull(envelope?.Data);

        Assert.True(
            envelope.Data.Results.Length <= MemoryEndpoints.SearchResultLimit,
            $"scope=all returned {envelope.Data.Results.Length} results, above the {MemoryEndpoints.SearchResultLimit} budget.");

    }

    /// <summary>
    /// The server-side budget alone truncates without any machine-readable signal, and gives a caller
    /// that only wants ten rows no way to say so. <c>limit</c> is the ask, <c>hasMore</c> is the answer.
    /// </summary>
    [SkippableFact]

    public async Task Search_honours_a_caller_supplied_limit_and_reports_which_scopes_had_more()
    {

        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        FakeLexiconService lexicon = new();

        for (int index = 0; index < 5; index++)
        {

            _ = await lexicon.UpsertAsync(
                $"Moonlit-{index}",
                "Person",
                ["Works by moonlight."],
                LexiconScope.Global,
                CancellationToken.None);

        }

        await using ArcanumWebApplicationFactory factory = new()
        {
            ServiceOverrides = services =>
            {

                services.RemoveAll<ILexiconService>();

                services.AddSingleton<ILexiconService>(lexicon);

                services.AddSingleton<ILexiconCurationService>(lexicon);

            },
        };

        HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage capped = await client.PostAsJsonAsync(
            "/api/memory/search",
            new MemorySearchRequest("moonlight", MemorySearchScope.Lexicon, Limit: 2),
            ArcanumJsonContext.Default.MemorySearchRequest);

        Assert.Equal(HttpStatusCode.OK, capped.StatusCode);

        ApiResponse<MemorySearchResponse>? truncated = await ReadAsync(
            capped,
            ArcanumJsonContext.Default.ApiResponseMemorySearchResponse);

        Assert.NotNull(truncated?.Data);

        Assert.Equal(2, truncated.Data.Results.Length);

        Assert.True(truncated.Data.HasMore);

        MemorySearchScopeStatusDto truncatedScope = Assert.Single(truncated.Data.Scopes!);

        Assert.Equal(MemorySearchScope.Lexicon, truncatedScope.Scope);

        Assert.Equal(2, truncatedScope.Count);

        Assert.True(truncatedScope.HasMore);

        HttpResponseMessage roomy = await client.PostAsJsonAsync(
            "/api/memory/search",
            new MemorySearchRequest("moonlight", MemorySearchScope.Lexicon, Limit: 10),
            ArcanumJsonContext.Default.MemorySearchRequest);

        ApiResponse<MemorySearchResponse>? complete = await ReadAsync(
            roomy,
            ArcanumJsonContext.Default.ApiResponseMemorySearchResponse);

        Assert.NotNull(complete?.Data);

        Assert.Equal(5, complete.Data.Results.Length);

        Assert.False(complete.Data.HasMore);

        Assert.False(Assert.Single(complete.Data.Scopes!).HasMore);

    }

    /// <summary>
    /// Refused rather than clamped: a caller that asked for 50,000 and silently received the budget
    /// would read a full <c>hasMore: false</c> page as "that is everything".
    /// </summary>
    [SkippableTheory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(MemoryEndpoints.SearchResultLimit + 1)]

    public async Task Search_refuses_a_limit_outside_the_server_budget(int limit)
    {

        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/memory/search",
            new MemorySearchRequest("anything", MemorySearchScope.All, Limit: limit),
            ArcanumJsonContext.Default.MemorySearchRequest);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        ApiResponse<MemorySearchResponse>? envelope = await ReadAsync(
            response,
            ArcanumJsonContext.Default.ApiResponseMemorySearchResponse);

        Assert.Equal(ErrorCodes.Validation.InvalidBody, envelope?.Error?.Code);

    }

    /// <summary>
    /// The Covenant block reaches the wire carrying what the installation actually holds.
    /// </summary>
    /// <remarks>
    /// Through the real host and over the real encrypted canonical tier, because the two suites either
    /// side of this one both skip it: the port suite drives the service directly, and the CLI suite
    /// drives a recorded response. Between them the projection this route performs was never executed
    /// at all, and reverting its counts and byte totals to constants left every suite green.
    /// </remarks>
    [SkippableFact]

    public async Task Status_carries_the_covenant_census_the_installation_actually_holds()
    {

        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        await using CovenantCanonicalFixture covenant =
            await CovenantCanonicalFixture.CreateAsync(CancellationToken.None);

        _ = await covenant.SeedHeadAsync(
            CovenantScope.Global,
            null,
            "preference.builds",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Run build commands from the repository root.",
            CancellationToken.None);

        await using ArcanumWebApplicationFactory factory = new()
        {
            ServiceOverrides = services =>
            {

                services.RemoveAll<ICovenantManagementService>();

                services.AddSingleton(Management(covenant));

            },
        };

        CovenantStatusDto? status = await CovenantStatusAsync(factory);

        Assert.NotNull(status);

        CovenantScopeCountDto count = Assert.Single(status.Counts);

        Assert.Equal(CovenantScope.Global, count.Scope);

        Assert.Equal(CovenantLane.Confirmed, count.Lane);

        Assert.Equal(1, count.Count);

        // The byte totals are what an operator compares against the ceiling beside them, so a constant
        // here reads exactly like a real measurement of an installation that holds nothing.
        Assert.True(status.GlobalConfirmedRenderedBytes > 0);

        Assert.Equal(CovenantLimits.MaxGlobalConfirmedRenderedBytes, status.RenderedByteCeilingPerSection);

    }

    /// <summary>
    /// A host with no Covenant management arm reports absence, not an installation holding nothing.
    /// </summary>
    /// <remarks>
    /// A zero is a measurement. Composing the block from availability alone when nothing could count
    /// tells an operator their preferences are gone, which is the single most damaging sentence this
    /// surface can say wrongly.
    /// </remarks>
    [SkippableFact]

    public async Task Status_omits_the_covenant_block_entirely_when_nothing_can_answer_for_it()
    {

        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new()
        {
            ServiceOverrides = static services => services.RemoveAll<ICovenantManagementService>(),
        };

        Assert.Null(await CovenantStatusAsync(factory));

    }

    /// <summary>
    /// The Covenant status block says an operator can erase an entry, and says what no local erasure
    /// can reach.
    /// </summary>
    /// <remarks>
    /// The sentence used to end at "retires the entry", which is the tombstone that keeps history
    /// readable, so an operator who read only status would conclude the Covenant has no way to forget
    /// something. The second clause is the other half of the same honesty: erasure removes the local
    /// rows, and a provider that already received the content is outside it.
    /// </remarks>
    [SkippableFact]
    public async Task Covenant_status_states_that_an_operator_can_erase_an_entry()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using CovenantCanonicalFixture covenant =
            await CovenantCanonicalFixture.CreateAsync(CancellationToken.None);

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = static settings => settings with
            {
                Features = settings.Features with { Covenant = true },
            },
            ServiceOverrides = services =>
            {
                services.RemoveAll<ICovenantManagementService>();

                services.AddSingleton(Management(covenant));
            },
        };

        CovenantStatusDto? status = await CovenantStatusAsync(factory);

        Assert.NotNull(status);

        Assert.Contains("until an operator erases the entry", status.Retention, StringComparison.Ordinal);

        Assert.Contains("outside every local erasure path", status.Retention, StringComparison.Ordinal);
    }

    private static async Task<CovenantStatusDto?> CovenantStatusAsync(ArcanumWebApplicationFactory factory)
    {

        HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync("/api/memory/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ApiResponse<MemoryStatusDto>? envelope = await ReadAsync(
            response,
            ArcanumJsonContext.Default.ApiResponseMemoryStatusDto);

        Assert.NotNull(envelope?.Data);

        return envelope.Data.Covenant;

    }

    private static ICovenantManagementService Management(CovenantCanonicalFixture fixture) =>
        new CovenantManagementService(
            fixture.Store,
            new CovenantLinker(),
            CovenantOperationGateFixture.CreateGate(),
            new FakeCovenantAvailability(),
            new UnusedEnvelopeCodec(),
            new UnusedCampaignAvailabilityReader());

    /// <summary>A codec that fails loudly, because a status read issues and accepts no envelope.</summary>
    private sealed class UnusedEnvelopeCodec : ICovenantEnvelopeCodec
    {

        public CovenantEnvelopeKeySnapshot KeySnapshot =>
            throw new NotSupportedException("A status read touches no envelope.");

        public Result<string> Encode(
            CovenantEnvelopePurpose purpose,
            ReadOnlySpan<byte> payload,
            TimeSpan lifetime,
            DateTimeOffset? issuedAtUtc = null) =>
            throw new NotSupportedException("A status read issues no envelope.");

        public Result<CovenantEnvelopeBody> Decode(CovenantEnvelopePurpose expectedPurpose, string? token) =>
            throw new NotSupportedException("A status read accepts no envelope.");

    }

    /// <summary>A reader that fails loudly, because a status read resolves no evaluation Campaign.</summary>
    private sealed class UnusedCampaignAvailabilityReader : ICampaignAvailabilityReader
    {

        public ValueTask<Result<long?>> FindAvailabilityGenerationAsync(
            Guid campaignId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("A status read resolves no Campaign.");

    }

    private static async Task<T?> ReadAsync<T>(
        HttpResponseMessage response,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {

        byte[] json = await response.Content.ReadAsByteArrayAsync();

        return JsonSerializer.Deserialize(json, typeInfo);

    }

    /// <summary>Matches ArcanumSettingClamps.EmbeddingsDimensions' 64-dimension floor.</summary>
    private const int SagaTestDimensions = 64;

    /// <summary>A host with Saga and embeddings on, answering one fixed vector for every text.</summary>
    private static ArcanumWebApplicationFactory CreateSagaEnabledFactory(bool campaignScopedMemory = false) =>
        new()
        {
            SettingsOverride = settings => settings with
            {
                Features = settings.Features with
                {
                    Embeddings = true,
                    Saga = true,
                    CampaignScopedMemory = campaignScopedMemory,
                },
                Integrations = settings.Integrations with
                {
                    Embeddings = settings.Integrations.Embeddings with
                    {
                        Provider = "test",
                        Model = "test-embed",
                        Dimensions = SagaTestDimensions,
                    },
                },
            },
            ServiceOverrides = static services =>
            {

                services.RemoveAll<IWeaveService>();

                services.AddSingleton<IWeaveService>(new FixedVectorWeaveService());

            },
        };

    /// <summary>Writes a memory through the store's own insert, so its scope is the one production derives.</summary>
    private static async Task SeedSagaMemoryAsync(
        ArcanumWebApplicationFactory factory,
        string id,
        string content,
        Guid? sessionId)
    {

        using IServiceScope scope = factory.Services.CreateScope();

        ISagaMemoryStore store = scope.ServiceProvider.GetRequiredService<ISagaMemoryStore>();

        SagaMemoryWriteOutcome outcome = await store.InsertAsync(
            id,
            content,
            DateTimeOffset.UtcNow,
            sessionId,
            tags: null,
            source: "extraction",
            FixedVectorWeaveService.Vector(),
            CancellationToken.None);

        Assert.Equal(SagaMemoryWriteOutcome.Written, outcome);

    }

    /// <summary>Retires a memory through the mapped routes, quoting the digest the detail route publishes.</summary>
    private static async Task RetireSagaMemoryAsync(HttpClient client, string id)
    {

        using HttpResponseMessage shown = await client.GetAsync($"/api/memory/saga/{id}");

        ApiResponse<SagaMemoryDetail>? detail = await ReadAsync(shown, ArcanumJsonContext.Default.ApiResponseSagaMemoryDetail);

        using StringContent request = new(
            JsonSerializer.Serialize(
                new SagaRetireRequest(detail!.Data!.ContentHash),
                ArcanumJsonContext.Default.SagaRetireRequest),
            System.Text.Encoding.UTF8,
            "application/json");

        using HttpResponseMessage retired = await client.PostAsync($"/api/memory/saga/{id}/retire", request);

        Assert.Equal(HttpStatusCode.OK, retired.StatusCode);

    }

    private static async Task<MemorySearchResultDto[]> SearchSagaAsync(HttpClient client, string query)
    {

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/memory/search",
            new MemorySearchRequest(query, MemorySearchScope.Saga),
            ArcanumJsonContext.Default.MemorySearchRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ApiResponse<MemorySearchResponse>? envelope = await ReadAsync(
            response,
            ArcanumJsonContext.Default.ApiResponseMemorySearchResponse);

        return envelope!.Data!.Results;

    }

    /// <summary>
    /// Empties the Saga embedding table the way a restore does when an archive was embedded under
    /// another width, leaving every <c>saga_memories</c> row where it was.
    /// </summary>
    private static async Task DropSagaEmbeddingsTakenUnderAnotherWidthAsync(ArcanumWebApplicationFactory factory)
    {

        using IServiceScope scope = factory.Services.CreateScope();

        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        SqliteConnection connection = (SqliteConnection)db.Database.GetDbConnection();

        if (connection.State != System.Data.ConnectionState.Open)
        {

            await db.Database.OpenConnectionAsync(CancellationToken.None);

        }

        long removed = await RetroDownfall.Arcanum.Infrastructure.Backup.BackupRestoreDatabaseWorker.DropMismatchedEmbeddingsAsync(
            connection,
            SagaTestDimensions + 1,
            CancellationToken.None);

        // The seeded memory's vector and nothing else; an arrangement that removed nothing would leave
        // every assertion after it describing a memory that never lost its embedding.
        Assert.Equal(1L, removed);

    }

    /// <summary>
    /// A Session with no binding row, which the Saga scope classifier reads as "ownership never
    /// resolved", so a memory written under it is <see cref="SagaMemoryScopeKind.LegacyUnresolved"/>.
    /// </summary>
    private static async Task<Guid> SeedUnboundSessionAsync(ArcanumWebApplicationFactory factory)
    {

        Guid sessionId = Guid.NewGuid();

        using IServiceScope scope = factory.Services.CreateScope();

        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        SqliteConnection connection = (SqliteConnection)db.Database.GetDbConnection();

        if (connection.State != System.Data.ConnectionState.Open)
        {

            await db.Database.OpenConnectionAsync(CancellationToken.None);

        }

        await ExecuteAsync(
            connection,
            """
            INSERT INTO "Sessions" ("Id", "CampaignId", "Status", "CreatedAt", "UpdatedAt")
            VALUES ($id, NULL, 'active', $now, $now);
            """,
            ("$id", sessionId.ToString("D").ToUpperInvariant()),
            ("$now", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
                .ToString("o", System.Globalization.CultureInfo.InvariantCulture)));

        return sessionId;

    }

    private static async Task<bool> SagaExplainEligibleAsync(HttpClient client, Guid? sessionId)
    {

        using HttpResponseMessage response = await client.GetAsync(
            sessionId is { } id ? $"/api/memory/explain/{id:D}" : "/api/memory/explain");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ApiResponse<MemoryExplainDto>? explain = await ReadAsync(response, ArcanumJsonContext.Default.ApiResponseMemoryExplainDto);

        return EligibleSource(explain!.Data!, "Saga");

    }

    /// <summary>
    /// A Campaign and a Session bound to it, in the canonical spelling every production writer renders,
    /// with the binding written under the same authorization scope production borrows.
    /// </summary>
    private static async Task<Guid> SeedCampaignSessionAsync(
        ArcanumWebApplicationFactory factory,
        Guid campaignId)
    {

        Guid sessionId = Guid.NewGuid();

        string now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
            .ToString("o", System.Globalization.CultureInfo.InvariantCulture);

        using IServiceScope scope = factory.Services.CreateScope();

        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        SqliteConnection connection = (SqliteConnection)db.Database.GetDbConnection();

        if (connection.State != System.Data.ConnectionState.Open)
        {

            await db.Database.OpenConnectionAsync(CancellationToken.None);

        }

        string canonicalCampaign = campaignId.ToString("D").ToUpperInvariant();

        string canonicalSession = sessionId.ToString("D").ToUpperInvariant();

        await ExecuteAsync(
            connection,
            """
            INSERT OR IGNORE INTO "Campaigns"
                ("Id", "Name", "NameLower", "Path", "Type", "Settings", "CreatedAt", "UpdatedAt")
            VALUES ($id, $name, $name, $path, 0, '{}', $now, $now);
            """,
            ("$id", canonicalCampaign),
            ("$name", campaignId.ToString("N")),
            ("$path", $"/campaigns/{campaignId:N}"),
            ("$now", now));

        await ExecuteAsync(
            connection,
            """
            INSERT INTO "Sessions" ("Id", "CampaignId", "Status", "CreatedAt", "UpdatedAt")
            VALUES ($id, $campaignId, 'active', $now, $now);
            """,
            ("$id", canonicalSession),
            ("$campaignId", canonicalCampaign),
            ("$now", now));

        using CovenantSqliteAuthorizationScope authorization = CovenantSqliteConnectionInitializer.Instance
            .Authorize(connection, CovenantSqliteAuthorizationKind.SessionBindingWrite);

        await ExecuteAsync(
            connection,
            """
            INSERT INTO session_campaign_bindings (SessionId, BindingKindCode, CampaignId, BoundAtUtc)
            VALUES ($id, 2, $campaignId, $now);
            """,
            ("$id", canonicalSession),
            ("$campaignId", canonicalCampaign),
            ("$now", now));

        return sessionId;

    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        foreach ((string name, object value) in parameters)
        {

            _ = command.Parameters.AddWithValue(name, value);

        }

        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);

    }

    /// <summary>Answers one fixed vector for every text, so similarity is not what a case here is about.</summary>
    private sealed class FixedVectorWeaveService : IWeaveService
    {

        public bool IsAvailable => true;

        public static float[] Vector()
        {

            float[] vector = new float[SagaTestDimensions];

            vector[0] = 1f;

            return vector;

        }

        public Task<Result<Embedding<float>>> EmbedAsync(string text, CancellationToken cancellationToken) =>
            Task.FromResult(Result<Embedding<float>>.Success(new Embedding<float>(Vector())));

        public Task<Result<Embedding<float>[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by the memory inspection routes.");

        public Task<Result<(string Chunk, int Offset)[]>> ChunkAsync(string text, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by the memory inspection routes.");

    }

    /// <summary>
    /// Answers <c>ListAsync</c> with whatever page size it is asked for, up to a fixed corpus size,
    /// and records that size. Stands in for a mature Saga corpus so the endpoint's own bound is what
    /// limits the response rather than the amount of test data.
    /// </summary>
    private sealed class SaturatingSagaMemoryStore(int available) : ISagaMemoryStore
    {

        public int RequestedLimit { get; private set; }

        public Task<SagaMemoryDto[]> ListAsync(
            string? query,
            Guid? sessionId, MemoryScope scope,
            int limit,
            int offset,
            CancellationToken cancellationToken)
        {

            RequestedLimit = limit;

            int count = Math.Min(available, limit);

            SagaMemoryDto[] memories = new SagaMemoryDto[count];

            for (int i = 0; i < count; i++)
            {

                memories[i] = new SagaMemoryDto(
                    $"saga-{i}",
                    "e",
                    DateTimeOffset.UnixEpoch,
                    null,
                    null,
                    null);

            }

            return Task.FromResult(memories);

        }

        public async Task<SagaMemoryCurationRow[]> ListCurationRowsAsync(
            string? query,
            Guid? sessionId,
            MemoryScope scope,
            int limit,
            int offset,
            CancellationToken cancellationToken)
        {

            SagaMemoryDto[] memories = await ListAsync(query, sessionId, scope, limit, offset, cancellationToken);

            return
            [
                .. memories.Select(static memory => new SagaMemoryCurationRow(
                    memory,
                    new SagaMemoryLifecycle(memory.RetiredAtUtc, memory.PinnedAtUtc),
                    HasEmbedding: true)),
            ];

        }

        public Task<bool> AnyRetrievableAsync(MemoryScope scope, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SagaMemoryWriteOutcome> InsertAsync(
            string id,
            string content,
            DateTimeOffset createdAt,
            Guid? sessionId,
            string? tags,
            string? source,
            float[] embedding,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int> CountAsync(CancellationToken cancellationToken) => Task.FromResult(available);

        public Task<int> CountBySessionAsync(Guid sessionId, CancellationToken cancellationToken) =>
            Task.FromResult(0);

        public Task<IReadOnlyDictionary<string, SagaMemoryDto>> GetByIdsAsync(
            IReadOnlyList<string> ids,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SagaMemoryCurationRow?> ReadCurationRowAsync(string id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SagaCurationOutcome> RetireAsync(
            string id, byte[] expectedContentDigest, DateTimeOffset retiredAt, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SagaCurationOutcome> ReinstateAsync(
            string id,
            byte[] expectedContentDigest,
            float[] embedding,
            DateTimeOffset reinstatedAt,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SagaCurationOutcome> CorrectAsync(
            string id,
            byte[] expectedContentDigest,
            string content,
            float[] embedding,
            DateTimeOffset correctedAt,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SagaCurationOutcome> SetPinAsync(
            string id, bool pinned, DateTimeOffset changedAt, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteAllAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SagaStats> GetStatsAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DateTimeOffset?> GetWatermarkAsync(Guid sessionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SetWatermarkAsync(
            Guid sessionId,
            DateTimeOffset lastExtractedEntryCreatedAt,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

    }

}
