using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Annals;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildSagaTranscriptAccessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Saga_grants_filter_before_top_k_with_campaign_gate_off(bool compact)
    {
        await using GuildSignetAuthorityTests.Fixture f = await GuildSignetAuthorityTests.Fixture.CreateAsync();

        await using SqliteConnection connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        await SeedSagaAsync(connection, "allowed", 1, null, 0.8f);

        await SeedSagaAsync(connection, "unclassified", 0, null, 1f);

        await SeedSagaAsync(connection, "unresolved", 3, null, 1f);

        await SeedSagaAsync(connection, "foreign", 2, Guid.NewGuid(), 1f);

        await SeedSagaAsync(connection, "malformed-global", 1, Guid.NewGuid(), 1f);

        await SeedSagaAsync(connection, "campaign-without-owner", 2, null, 1f);

        if (compact)
        {
            for (int i = 0; i < 1024; i++)
            {
                await SeedSagaAsync(connection, "denied-" + i, 0, null, 1f);
            }
        }

        GuildGrant grant = f.Grant with { Memory = [new(GuildMemoryStore.Saga, GuildMemoryOperation.Read, new(GuildMemoryScopeKind.Global))] };

        f.Policy.HostGrant = grant;

        GuildExecutionHandle execution = await f.RootAsync(grant);

        using IDisposable invocation = GuildMemoryInvocationAmbient.Begin(new(f.Authority, execution));

        DivinationService service = new(db, NullLogger<DivinationService>.Instance, new TestOptionsMonitor<ArcanumSettings>(new()));

        var result = await service.SearchAsync(SagaStorageKeys.EmbeddingTable, SagaStorageKeys.EmbeddingKeyColumn,
            SagaStorageKeys.EmbeddingColumn, new Embedding<float>(Vector(1f)), 1, 0f, default);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal("allowed", Assert.Single(result.Value).Id);
    }

    [Fact]
    public async Task Saga_hydration_rechecks_explicit_classification_and_live_grant()
    {
        await using GuildSignetAuthorityTests.Fixture f = await GuildSignetAuthorityTests.Fixture.CreateAsync();

        await using SqliteConnection connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        await SeedSagaAsync(connection, "allowed", 1, null, 1f);

        await SeedSagaAsync(connection, "null-is-not-global", 0, null, 1f);

        await SeedSagaAsync(connection, "malformed-global", 1, Guid.NewGuid(), 1f);

        await SeedSagaAsync(connection, "campaign-without-owner", 2, null, 1f);

        await SeedSagaAsync(connection, "foreign", 2, Guid.NewGuid(), 1f);

        GuildGrant grant = f.Grant with { Memory = [new(GuildMemoryStore.Saga, GuildMemoryOperation.Read, new(GuildMemoryScopeKind.Global))] };

        f.Policy.HostGrant = grant;

        GuildExecutionHandle execution = await f.RootAsync(grant);

        SagaMemoryStore store = Store(db);

        using IDisposable invocation = GuildMemoryInvocationAmbient.Begin(new(f.Authority, execution));

        var rows = await store.GetByIdsAsync(["allowed", "null-is-not-global", "foreign", "malformed-global", "campaign-without-owner"], default);

        Assert.Equal("allowed", Assert.Single(rows).Key);

        f.Policy.HostGrant = f.Grant;

        await Assert.ThrowsAsync<GuildMemoryAccessException>(() => store.GetByIdsAsync(["allowed"], default));
    }

    [Fact]
    public async Task Transcript_session_grant_limits_archive_rank_and_direct_readers()
    {
        await using GuildSignetAuthorityTests.Fixture f = await GuildSignetAuthorityTests.Fixture.CreateAsync();

        Guid campaign = Guid.NewGuid();

        Guid allowed = Guid.NewGuid();

        Guid denied = Guid.NewGuid();

        await f.SeedOwnersAsync(campaign, allowed, denied);

        f.Policy.NextOwners = new(allowed, campaign, null);

        GuildGrant grant = f.Grant with { Memory = [new(GuildMemoryStore.Transcript, GuildMemoryOperation.Read, new(GuildMemoryScopeKind.Session, allowed))] };

        f.Policy.HostGrant = grant;

        GuildExecutionHandle execution = await f.RootAsync(grant);

        await using SqliteConnection connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        await SeedBindingsAsync(connection, campaign);

        Guid allowedEntry = await SeedEntryAsync(connection, allowed, "needle permitted");

        Guid deniedEntry = await SeedEntryAsync(connection, denied, "needle denied needle needle needle");

        GrimoireRepository repository = new(db, new NoOpSessionAttachmentStore(), NullLogger<GrimoireRepository>.Instance,
            new TestOptionsSnapshot<ArcanumSettings>(new()), null, null, FixtureOrdinaryConnectionFactory.For(db),
            FixtureLabeledArtifactGuard.For(db), null);

        using IDisposable invocation = GuildMemoryInvocationAmbient.Begin(new(f.Authority, execution));

        var archive = await repository.SearchArchivesAsync("needle", 1);

        Assert.True(archive.IsSuccess, archive.Error.Message);

        Assert.Contains("permitted", archive.Value);

        Assert.DoesNotContain("denied", archive.Value);

        Assert.NotNull(await repository.GetEntryByIdAsync(allowed, allowedEntry));

        Assert.Null(await repository.GetEntryByIdAsync(denied, deniedEntry));

        Assert.Null(await repository.GetSessionAsync(denied));

        Assert.Null(await repository.GetSessionEntriesAsync(denied));

        Assert.Null(await repository.GetRecentSessionEntriesAsync(denied, 1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Saga_campaign_grant_never_includes_global_content(bool campaignGate)
    {
        await using GuildSignetAuthorityTests.Fixture f = await GuildSignetAuthorityTests.Fixture.CreateAsync();

        Guid campaign = Guid.NewGuid();

        Guid session = Guid.NewGuid();

        await f.SeedOwnersAsync(campaign, session, Guid.NewGuid());

        f.Policy.NextOwners = new(session, campaign, null);

        await using SqliteConnection connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        await SeedSagaAsync(connection, "global", 1, null, 1f);

        await SeedSagaAsync(connection, "campaign", 2, campaign, 0.8f);

        await SeedSagaAsync(connection, "other", 2, Guid.NewGuid(), 1f);

        GuildGrant grant = f.Grant with { Memory = [new(GuildMemoryStore.Saga, GuildMemoryOperation.Read, new(GuildMemoryScopeKind.Campaign, campaign))] };

        f.Policy.HostGrant = grant;

        GuildExecutionHandle execution = await f.RootAsync(grant);

        using IDisposable invocation = GuildMemoryInvocationAmbient.Begin(new(f.Authority, execution));

        DivinationService service = new(db, NullLogger<DivinationService>.Instance, new TestOptionsMonitor<ArcanumSettings>(new()));

        var result = campaignGate
            ? await service.SearchCampaignScopedAsync(SagaStorageKeys.EmbeddingTable, SagaStorageKeys.EmbeddingKeyColumn,
                SagaStorageKeys.EmbeddingColumn, SagaStorageKeys.CampaignScope(campaign), new Embedding<float>(Vector(1f)), 1, 0f, default)
            : await service.SearchAsync(SagaStorageKeys.EmbeddingTable, SagaStorageKeys.EmbeddingKeyColumn,
                SagaStorageKeys.EmbeddingColumn, new Embedding<float>(Vector(1f)), 1, 0f, default);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal("campaign", Assert.Single(result.Value).Id);

        Assert.Equal("campaign", Assert.Single(await Store(db).GetByIdsAsync(["global", "campaign", "other"], default)).Key);
    }

    [Fact]
    public async Task Guild_Saga_append_keeps_a_real_immutable_version_with_Annals_disabled()
    {
        await using GuildSignetAuthorityTests.Fixture f = await GuildSignetAuthorityTests.Fixture.CreateAsync();

        await using SqliteConnection connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        GuildGrant grant = f.Grant with { Memory = [new(GuildMemoryStore.Saga, GuildMemoryOperation.Append, new(GuildMemoryScopeKind.Global))] };

        f.Policy.HostGrant = grant;

        GuildExecutionHandle execution = await f.RootAsync(grant);

        SagaMemoryStore store = Store(db);

        using IDisposable invocation = GuildMemoryInvocationAmbient.Begin(new(f.Authority, execution));

        Assert.Equal(SagaMemoryWriteOutcome.Written, await store.InsertAsync(Guid.NewGuid().ToString(),
            "Guild conclusion", f.Time.GetUtcNow(), null, null, "extraction", Vector(1f), default));

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM annal_versions v JOIN annal_claims c ON c.ClaimId = v.ClaimId WHERE c.SubjectStoreCode = 1"));

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM annal_heads WHERE SubjectStoreCode = 1"));

        f.Policy.HostGrant = f.Grant;

        await Assert.ThrowsAsync<GuildMemoryAccessException>(() => store.InsertAsync(Guid.NewGuid().ToString(),
            "must not borrow operator authority", f.Time.GetUtcNow(), null, null, "extraction", Vector(1f), default));

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM saga_memories"));
    }

    [Fact]
    public async Task Saga_operator_curation_and_unscoped_preview_are_unavailable_to_Guild()
    {
        await using GuildSignetAuthorityTests.Fixture f = await GuildSignetAuthorityTests.Fixture.CreateAsync();

        await using SqliteConnection connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        await SeedSagaAsync(connection, "memory", 1, null, 1f);

        GuildExecutionHandle execution = await f.RootAsync();

        SagaMemoryStore store = Store(db);

        using IDisposable invocation = GuildMemoryInvocationAmbient.Begin(new(f.Authority, execution));

        await Assert.ThrowsAsync<GuildMemoryAccessException>(() => store.CountAsync(default));

        await Assert.ThrowsAsync<GuildMemoryAccessException>(() => store.ReadCurationRowAsync("memory", default));

        await Assert.ThrowsAsync<GuildMemoryAccessException>(() => store.DeleteAsync("memory", default));
    }

    [Theory]
    [InlineData("input")]
    [InlineData("replay")]
    [InlineData("semantic")]
    public async Task Operator_transcript_claim_and_semantic_preview_paths_refuse_Guild(string path)
    {
        await using GuildSignetAuthorityTests.Fixture f = await GuildSignetAuthorityTests.Fixture.CreateAsync();

        await using SqliteConnection connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        GuildExecutionHandle execution = await f.RootAsync();

        GrimoireRepository repository = new(db, new NoOpSessionAttachmentStore(), NullLogger<GrimoireRepository>.Instance,
            new TestOptionsSnapshot<ArcanumSettings>(new()), null, null, FixtureOrdinaryConnectionFactory.For(db),
            FixtureLabeledArtifactGuard.For(db), null);

        SessionDivinationService preview = new(db, FixtureOrdinaryConnectionFactory.For(db), NullLogger<SessionDivinationService>.Instance);

        using IDisposable invocation = GuildMemoryInvocationAmbient.Begin(new(f.Authority, execution));

        await Assert.ThrowsAsync<GuildMemoryAccessException>(async () =>
        {
            if (path == "input")
            {
                _ = await repository.ReadClaimInputAsync(Guid.NewGuid(), default);
            }
            else if (path == "replay")
            {
                _ = await repository.ReadCleanCommittedReplyAsync(null!, default);
            }
            else
            {
                _ = await preview.SearchAsync(new Embedding<float>(Vector(1f)), null, null, 1, 0f, default);
            }
        });
    }

    [Fact]
    public async Task Disposed_invocation_cannot_start_a_deferred_Saga_read()
    {
        await using GuildSignetAuthorityTests.Fixture f = await GuildSignetAuthorityTests.Fixture.CreateAsync();

        await using SqliteConnection connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        await SeedSagaAsync(connection, "allowed", 1, null, 1f);

        GuildGrant grant = f.Grant with { Memory = [new(GuildMemoryStore.Saga, GuildMemoryOperation.Read, new(GuildMemoryScopeKind.Global))] };

        f.Policy.HostGrant = grant;

        GuildExecutionHandle execution = await f.RootAsync(grant);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task escaped;

        using (GuildMemoryInvocationAmbient.Begin(new(f.Authority, execution)))
        {
            escaped = Task.Run(async () =>
            {
                await release.Task;

                _ = await Store(db).GetByIdsAsync(["allowed"], default);
            });
        }

        release.SetResult();

        await Assert.ThrowsAsync<GuildMemoryAccessException>(() => escaped);
    }

    [Fact]
    public async Task Campaign_Transcript_grant_excludes_tombstoned_sibling_before_cleanup()
    {
        await using GuildSignetAuthorityTests.Fixture f = await GuildSignetAuthorityTests.Fixture.CreateAsync();

        Guid campaign = Guid.NewGuid();

        Guid owner = Guid.NewGuid();

        Guid sibling = Guid.NewGuid();

        await f.SeedOwnersAsync(campaign, owner, sibling);

        f.Policy.NextOwners = new(owner, campaign, null);

        await using SqliteConnection connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        await SeedBindingsAsync(connection, campaign);

        await SeedEntryAsync(connection, owner, "needle healthy");

        Guid tombstonedEntry = await SeedEntryAsync(connection, sibling, "needle deleted needle needle");

        GuildGrant grant = f.Grant with { Memory = [new(GuildMemoryStore.Transcript, GuildMemoryOperation.Read, new(GuildMemoryScopeKind.Campaign, campaign))] };

        f.Policy.HostGrant = grant;

        GuildExecutionHandle execution = await f.RootAsync(grant);

        await f.ExecuteAsync($"INSERT INTO owner_deletion_events(OwnerKindCode,OwnerId,DeletedAtUtc) VALUES(2,'{sibling.ToString("D").ToUpperInvariant()}','2026-11-05T00:00:00.0000000Z')");

        GrimoireRepository repository = new(db, new NoOpSessionAttachmentStore(), NullLogger<GrimoireRepository>.Instance,
            new TestOptionsSnapshot<ArcanumSettings>(new()), null, null, FixtureOrdinaryConnectionFactory.For(db),
            FixtureLabeledArtifactGuard.For(db), null);

        using IDisposable invocation = GuildMemoryInvocationAmbient.Begin(new(f.Authority, execution));

        var result = await repository.SearchArchivesAsync("needle", 1);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Contains("healthy", result.Value);

        Assert.DoesNotContain("deleted", result.Value);

        Assert.Null(await repository.GetEntryByIdAsync(sibling, tombstonedEntry));

        Assert.Null(await repository.GetSessionAsync(sibling));
    }

    [Theory]
    [InlineData("unmaterialized")]
    [InlineData("omitted")]
    [InlineData("changed-version")]
    [InlineData("ended-turn")]
    public async Task Guild_Saga_append_requires_exact_live_source_materialization(string proof)
    {
        await using GuildSignetAuthorityTests.Fixture f = await GuildSignetAuthorityTests.Fixture.CreateAsync();

        Guid campaign = Guid.NewGuid();

        Guid session = Guid.NewGuid();

        await f.SeedOwnersAsync(campaign, session, Guid.NewGuid());

        f.Policy.NextOwners = new(session, campaign, null);

        await using SqliteConnection connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        await SeedBindingsAsync(connection, campaign);

        AttachmentMemoryProvenance provenance = new(session, Guid.NewGuid(), "notes.txt", 1,
            new string('A', 64), f.Time.GetUtcNow(), "SnapshotOnly", AttachmentSourceAvailability.Available);

        await SeedAttachmentAsync(connection, provenance);

        GuildGrant grant = f.Grant with
        {
            Memory = [new(GuildMemoryStore.Saga, GuildMemoryOperation.Append, new(GuildMemoryScopeKind.Campaign, campaign)),
                new(GuildMemoryStore.Attachment, GuildMemoryOperation.Read, new(GuildMemoryScopeKind.Session, session))],
        };

        f.Policy.HostGrant = grant;

        GuildExecutionHandle execution = await f.RootAsync(grant);

        using IDisposable invocation = GuildMemoryInvocationAmbient.Begin(new(f.Authority, execution));

        using AttachmentMemoryGateAmbient.TurnScope turn = AttachmentMemoryGateAmbient.BeginTurn(session);

        if (proof != "unmaterialized")
        {
            turn.RegisterMaterialized(provenance);
        }

        if (proof == "ended-turn")
        {
            turn.Dispose();
        }

        SagaMemoryStore store = Store(db);

        await Assert.ThrowsAsync<GuildMemoryAccessException>(() => proof == "omitted"
            ? store.InsertAsync(Guid.NewGuid().ToString(), "must not promote", f.Time.GetUtcNow(), session, null, "extraction", Vector(1f), default)
            : store.InsertAsync(Guid.NewGuid().ToString(), "must not promote", f.Time.GetUtcNow(), session, null, "attachment-extraction", Vector(1f),
                proof == "changed-version" ? provenance with { Version = 2 } : provenance, default));

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM saga_memories"));

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM annal_versions"));

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_memory_attribution"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Guild_Saga_attachment_append_requires_source_read_grant_and_keeps_provenance(bool sourceGranted)
    {
        await using GuildSignetAuthorityTests.Fixture f = await GuildSignetAuthorityTests.Fixture.CreateAsync();

        Guid campaign = Guid.NewGuid();

        Guid session = Guid.NewGuid();

        await f.SeedOwnersAsync(campaign, session, Guid.NewGuid());

        f.Policy.NextOwners = new(session, campaign, null);

        await using SqliteConnection connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        await SeedBindingsAsync(connection, campaign);

        AttachmentMemoryProvenance provenance = new(session, Guid.NewGuid(), "notes.txt", 1,
            new string('A', 64), f.Time.GetUtcNow(), "SnapshotOnly", AttachmentSourceAvailability.Available);

        await SeedAttachmentAsync(connection, provenance);

        GuildGrant grant = f.Grant with
        {
            Memory = sourceGranted
                ? [new(GuildMemoryStore.Saga, GuildMemoryOperation.Append, new(GuildMemoryScopeKind.Campaign, campaign)),
                    new(GuildMemoryStore.Attachment, GuildMemoryOperation.Read, new(GuildMemoryScopeKind.Session, session))]
                : [new(GuildMemoryStore.Saga, GuildMemoryOperation.Append, new(GuildMemoryScopeKind.Campaign, campaign))],
        };

        f.Policy.HostGrant = grant;

        GuildExecutionHandle execution = await f.RootAsync(grant);

        using IDisposable invocation = GuildMemoryInvocationAmbient.Begin(new(f.Authority, execution));

        using AttachmentMemoryGateAmbient.TurnScope turn = AttachmentMemoryGateAmbient.BeginTurn(session);

        turn.RegisterMaterialized(provenance);

        SagaMemoryStore store = Store(db);

        Task<SagaMemoryWriteOutcome> Append() => store.InsertAsync(Guid.NewGuid().ToString(), "consulted conclusion",
            f.Time.GetUtcNow(), session, null, "attachment-extraction", Vector(1f), provenance, default);

        if (sourceGranted)
        {
            Assert.Equal(SagaMemoryWriteOutcome.Written, await Append());

            Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM saga_memory_attachment_provenance"));

            Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_memory_attribution"));
        }
        else
        {
            await Assert.ThrowsAsync<GuildMemoryAccessException>(Append);

            Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM saga_memories"));
        }
    }

    [Theory]
    [InlineData("artifact")]
    [InlineData("annal")]
    public async Task Ordinary_Saga_grant_excludes_protected_sources_before_rank_and_hydration(string labelKind)
    {
        await using GuildSignetAuthorityTests.Fixture f = await GuildSignetAuthorityTests.Fixture.CreateAsync();

        await using SqliteConnection connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        Guid protectedId = Guid.NewGuid();

        string id = protectedId.ToString("D").ToUpperInvariant();

        await SeedSagaAsync(connection, "allowed", 1, null, 0.8f);

        await SeedSagaAsync(connection, id, 1, null, 1f);

        if (labelKind == "artifact")
        {
            using CovenantConnectionSource connections = new(db, FixtureOrdinaryConnectionFactory.For(db));

            ArtifactSensitivityLedger ledger = new(connections);

            var labeled = await ledger.LabelAsync(new DerivedArtifactWrite(SensitiveArtifactKind.Saga, protectedId,
                null, null, null, 1, new CovenantDigest(AnnalContentDigest.ForSagaMemory(id)), ContentSensitivity.CovenantDerived,
                GenerationProvenance.CreateExact([Guid.NewGuid()])), default);

            Assert.True(labeled.IsSuccess, labeled.Error.Message);
        }
        else
        {
            await using SqliteTransaction transaction = connection.BeginTransaction();

            Assert.True(await AnnalsClaimWriter.AppendAssertAsync(connection, transaction, AnnalSubjectStore.Saga,
                id, AnnalOrigin.AgentExtracted, SagaMemoryScopeKind.Global, null, ContentSensitivity.CovenantDerived,
                AnnalContentDigest.ForSagaMemory(id), f.Time.GetUtcNow(), f.Time.GetUtcNow(), null, default));

            await transaction.CommitAsync();
        }

        GuildGrant grant = f.Grant with { Memory = [new(GuildMemoryStore.Saga, GuildMemoryOperation.Read, new(GuildMemoryScopeKind.Global))] };

        f.Policy.HostGrant = grant;

        GuildExecutionHandle execution = await f.RootAsync(grant);

        using IDisposable invocation = GuildMemoryInvocationAmbient.Begin(new(f.Authority, execution));

        DivinationService service = new(db, NullLogger<DivinationService>.Instance, new TestOptionsMonitor<ArcanumSettings>(new()));

        var result = await service.SearchAsync(SagaStorageKeys.EmbeddingTable, SagaStorageKeys.EmbeddingKeyColumn,
            SagaStorageKeys.EmbeddingColumn, new Embedding<float>(Vector(1f)), 1, 0f, default);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal("allowed", Assert.Single(result.Value).Id);

        Assert.Equal("allowed", Assert.Single(await Store(db).GetByIdsAsync(["allowed", id], default)).Key);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("recent")]
    [InlineData("entries")]
    [InlineData("session")]
    [InlineData("archives")]
    public async Task Transcript_read_excludes_protected_entries_before_rank_and_window(string path)
    {
        await using GuildSignetAuthorityTests.Fixture f = await GuildSignetAuthorityTests.Fixture.CreateAsync();

        Guid campaign = Guid.NewGuid();

        Guid session = Guid.NewGuid();

        await f.SeedOwnersAsync(campaign, session, Guid.NewGuid());

        f.Policy.NextOwners = new(session, campaign, null);

        await using SqliteConnection connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        await SeedBindingsAsync(connection, campaign);

        Guid clean = await SeedEntryAsync(connection, session, "needle clean");

        Guid protectedEntry = await SeedEntryAsync(connection, session, "needle protected needle needle needle", sequence: 2);

        using CovenantConnectionSource connections = new(db, FixtureOrdinaryConnectionFactory.For(db));

        ArtifactSensitivityLedger ledger = new(connections);

        var labeled = await ledger.LabelAsync(new DerivedArtifactWrite(SensitiveArtifactKind.AssistantEntry, protectedEntry,
            session, campaign, null, 1, new CovenantDigest(AnnalContentDigest.ForSagaMemory("needle protected needle needle needle")),
            ContentSensitivity.CovenantDerived, GenerationProvenance.CreateExact([Guid.NewGuid()])), default);

        Assert.True(labeled.IsSuccess, labeled.Error.Message);

        GuildGrant grant = f.Grant with { Memory = [new(GuildMemoryStore.Transcript, GuildMemoryOperation.Read, new(GuildMemoryScopeKind.Session, session))] };

        f.Policy.HostGrant = grant;

        GuildExecutionHandle execution = await f.RootAsync(grant);

        GrimoireRepository repository = new(db, new NoOpSessionAttachmentStore(), NullLogger<GrimoireRepository>.Instance,
            new TestOptionsSnapshot<ArcanumSettings>(new()), null, null, FixtureOrdinaryConnectionFactory.For(db),
            FixtureLabeledArtifactGuard.For(db), null);

        using (GuildMemoryInvocationAmbient.Begin(new(f.Authority, execution)))
        {
            if (path == "id")
            {
                Assert.Null(await repository.GetEntryByIdAsync(session, protectedEntry));

                Assert.NotNull(await repository.GetEntryByIdAsync(session, clean));
            }
            else if (path == "recent")
            {
                Assert.Equal(clean, Assert.Single((await repository.GetRecentSessionEntriesAsync(session, 1))!).Id);
            }
            else if (path == "entries")
            {
                Assert.Equal(clean, Assert.Single((await repository.GetSessionEntriesAsync(session))!).Id);
            }
            else if (path == "session")
            {
                Assert.Equal(clean, Assert.Single((await repository.GetSessionAsync(session))!.Entries).Id);
            }
            else
            {
                var result = await repository.SearchArchivesAsync("needle", 1);

                Assert.True(result.IsSuccess, result.Error.Message);

                Assert.Contains("clean", result.Value);

                Assert.DoesNotContain("protected", result.Value);
            }
        }

        Assert.NotNull(await repository.GetEntryByIdAsync(session, protectedEntry));
    }

    [Theory]
    [InlineData("title", false, true)]
    [InlineData("title", true, true)]
    [InlineData("summary", false, true)]
    [InlineData("summary", true, true)]
    [InlineData("title", false, false)]
    [InlineData("title", true, false)]
    [InlineData("summary", false, false)]
    [InlineData("summary", true, false)]
    public async Task Transcript_header_refuses_protected_derived_artifact_identity(string field, bool headerOnly, bool protectedContent)
    {
        await using GuildSignetAuthorityTests.Fixture f = await GuildSignetAuthorityTests.Fixture.CreateAsync();

        Guid campaign = Guid.NewGuid();

        Guid session = Guid.NewGuid();

        await f.SeedOwnersAsync(campaign, session, Guid.NewGuid());

        f.Policy.NextOwners = new(session, campaign, null);

        await using SqliteConnection connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        await SeedBindingsAsync(connection, campaign);

        using CovenantConnectionSource connections = new(db, FixtureOrdinaryConnectionFactory.For(db));

        SessionDerivedArtifactStore derived = new(connections, CovenantSqliteConnectionInitializer.Instance);

        ContentSensitivity sensitivity = protectedContent ? ContentSensitivity.CovenantDerived : ContentSensitivity.None;

        GenerationProvenance provenance = GenerationProvenance.CreateExact(protectedContent ? [Guid.NewGuid()] : []);

        var written = field == "title"
            ? await derived.ReplaceAsync(new SessionTitleArtifactWrite(session, "derived title", sensitivity, provenance, campaign), default)
            : await derived.ReplaceAsync(new SessionSummaryArtifactWrite(session, "derived summary", f.Time.GetUtcNow(), sensitivity, provenance, campaign), default);

        Assert.True(written.IsSuccess, written.Error.Message);

        Assert.NotEqual(session, written.Value.ArtifactId);

        GuildGrant grant = f.Grant with { Memory = [new(GuildMemoryStore.Transcript, GuildMemoryOperation.Read, new(GuildMemoryScopeKind.Session, session))] };

        f.Policy.HostGrant = grant;

        GuildExecutionHandle execution = await f.RootAsync(grant);

        GrimoireRepository repository = new(db, new NoOpSessionAttachmentStore(), NullLogger<GrimoireRepository>.Instance,
            new TestOptionsSnapshot<ArcanumSettings>(new()), null, null, FixtureOrdinaryConnectionFactory.For(db),
            FixtureLabeledArtifactGuard.For(db), null);

        using (GuildMemoryInvocationAmbient.Begin(new(f.Authority, execution)))
        {
            var result = headerOnly ? await repository.GetSessionHeaderAsync(session) : await repository.GetSessionAsync(session);

            if (protectedContent)
            {
                Assert.Null(result);
            }
            else
            {
                Assert.NotNull(result);

                Assert.Equal(field == "title" ? "derived title" : "derived summary", field == "title" ? result.Title : result.Summary);
            }
        }

        Assert.NotNull(await repository.GetSessionHeaderAsync(session));
    }

    [Theory]
    [InlineData("title")]
    [InlineData("summary")]
    public async Task Transcript_header_rejects_bytes_that_no_longer_match_its_clean_immutable_artifact(string field)
    {
        await using GuildSignetAuthorityTests.Fixture f = await GuildSignetAuthorityTests.Fixture.CreateAsync();

        Guid campaign = Guid.NewGuid();

        Guid session = Guid.NewGuid();

        await f.SeedOwnersAsync(campaign, session, Guid.NewGuid());

        f.Policy.NextOwners = new(session, campaign, null);

        await using SqliteConnection connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        await SeedBindingsAsync(connection, campaign);

        using CovenantConnectionSource connections = new(db, FixtureOrdinaryConnectionFactory.For(db));

        SessionDerivedArtifactStore derived = new(connections, CovenantSqliteConnectionInitializer.Instance);

        var written = field == "title"
            ? await derived.ReplaceAsync(new SessionTitleArtifactWrite(session, "clean title", ContentSensitivity.None,
                GenerationProvenance.CreateExact([]), campaign), default)
            : await derived.ReplaceAsync(new SessionSummaryArtifactWrite(session, "clean summary", f.Time.GetUtcNow(),
                ContentSensitivity.None, GenerationProvenance.CreateExact([]), campaign), default);

        Assert.True(written.IsSuccess, written.Error.Message);

        GrimoireRepository repository = new(db, new NoOpSessionAttachmentStore(), NullLogger<GrimoireRepository>.Instance,
            new TestOptionsSnapshot<ArcanumSettings>(new()), null, null, FixtureOrdinaryConnectionFactory.For(db),
            FixtureLabeledArtifactGuard.For(db), null);

        if (field == "summary")
        {
            await repository.UpdateSessionCampaignRollupAsync(session, "bytes outside the clean artifact", f.Time.GetUtcNow().UtcDateTime);
        }
        else
        {
            await using SqliteCommand corrupt = connection.CreateCommand();

            corrupt.CommandText = "UPDATE Sessions SET Title=$content WHERE Id=$session";

            corrupt.Parameters.AddWithValue("$content", "bytes outside the clean artifact");

            corrupt.Parameters.AddWithValue("$session", session.ToString("D").ToUpperInvariant());

            await corrupt.ExecuteNonQueryAsync();
        }

        GuildGrant grant = f.Grant with { Memory = [new(GuildMemoryStore.Transcript, GuildMemoryOperation.Read, new(GuildMemoryScopeKind.Session, session))] };

        f.Policy.HostGrant = grant;

        GuildExecutionHandle execution = await f.RootAsync(grant);

        using (GuildMemoryInvocationAmbient.Begin(new(f.Authority, execution)))
        {
            Assert.Null(await repository.GetSessionHeaderAsync(session));
        }

        Assert.NotNull(await repository.GetSessionHeaderAsync(session));
    }

    private static SagaMemoryStore Store(ArcanumDbContext db) => new(db,
        new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings
        {
            Integrations = new() { Embeddings = new() { Dimensions = 768 } },
        }), MemoryErasureTestKeys.Isolated());

    private static float[] Vector(float first)
    {
        float[] vector = new float[768];

        vector[0] = first;

        vector[1] = MathF.Sqrt(1f - first * first);

        return vector;
    }

    private static async Task SeedSagaAsync(SqliteConnection connection, string id, int classification, Guid? campaign, float similarity)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO saga_memories(Id,Content,CreatedAt,ScopeKindCode,CampaignId)
            VALUES($id,$id,'2026-11-01T00:00:00.0000000+00:00',$kind,$campaign);
            INSERT INTO saga_memory_embeddings(MemoryId,Embedding,Dim) VALUES($id,$vector,768);
            """;

        command.Parameters.AddWithValue("$id", id);

        command.Parameters.AddWithValue("$kind", classification);

        command.Parameters.AddWithValue("$campaign", campaign is { } owner ? owner.ToString("D").ToUpperInvariant() : DBNull.Value);

        float[] vector = Vector(similarity);

        command.Parameters.AddWithValue("$vector", EmbeddingBlobCodec.Encode(vector));

        await command.ExecuteNonQueryAsync();

        await QuantizedEmbeddingStore.UpsertAsync(connection, null, "saga_memory_embeddings", id, vector, default);
    }

    private static async Task SeedAttachmentAsync(SqliteConnection connection, AttachmentMemoryProvenance provenance)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO SessionAttachments(Id,SessionId,State,LogicalKey,OriginalFileName,Version,RelativePath,
                ContentSha256,MimeType,ByteLength,Kind,CreatedAt)
            VALUES($id,$session,'Bound','notes.txt','notes.txt',1,'notes.txt',$hash,'text/plain',12,'text',$now);
            """;

        command.Parameters.AddWithValue("$id", provenance.AttachmentId.ToString("D").ToUpperInvariant());

        command.Parameters.AddWithValue("$session", provenance.SessionId.ToString("D").ToUpperInvariant());

        command.Parameters.AddWithValue("$hash", provenance.ContentHash);

        command.Parameters.AddWithValue("$now", UtcInstantText.Format(provenance.MaterializedAt));

        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedBindingsAsync(SqliteConnection connection, Guid campaign)
    {
        using CovenantSqliteAuthorizationScope scope = CovenantSqliteConnectionInitializer.Instance
            .Authorize(connection, CovenantSqliteAuthorizationKind.SessionBindingWrite);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO session_campaign_bindings(SessionId,BindingKindCode,CampaignId,BoundAtUtc)
            SELECT Id,2,CampaignId,'2026-11-01T00:00:00.0000000+00:00' FROM Sessions WHERE CampaignId=$campaign;
            """;

        command.Parameters.AddWithValue("$campaign", campaign.ToString("D").ToUpperInvariant());

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<Guid> SeedEntryAsync(SqliteConnection connection, Guid session, string content, long sequence = 1)
    {
        Guid id = Guid.NewGuid();

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO Entries(Id,SessionId,Role,Content,ModelUsed,CreatedAt,Sequence)
            VALUES($id,$session,2,$content,'test','2026-11-01T00:00:00.0000000+00:00',$sequence);
            """;

        command.Parameters.AddWithValue("$id", id.ToString("D").ToUpperInvariant());

        command.Parameters.AddWithValue("$session", session.ToString("D").ToUpperInvariant());

        command.Parameters.AddWithValue("$content", content);

        command.Parameters.AddWithValue("$sequence", sequence);

        await command.ExecuteNonQueryAsync();

        return id;
    }
}
