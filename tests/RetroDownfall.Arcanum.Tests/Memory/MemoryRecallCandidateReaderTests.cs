using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Lexicon;
using RetroDownfall.Arcanum.Infrastructure.Memory;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Memory;

[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class MemoryRecallCandidateReaderTests(GrimoireFixture fixture)
{
    private static readonly Guid CampaignA = new("A0000000-0000-4000-8000-0000000000AA");

    private static readonly Guid CampaignB = new("B0000000-0000-4000-8000-0000000000BB");

    [Fact]
    public async Task Clean_lexicon_recall_honors_campaign_shadowing_and_retirement()
    {
        await using ArcanumDbContext db = fixture.CreateContext(fixture.CopyDatabase());

        TestOptionsMonitor<ArcanumSettings> options = Options();

        LexiconService lexicon = new(db, NullLogger<LexiconService>.Instance, options, MemoryErasureTestKeys.Isolated());

        _ = await lexicon.UpsertAsync("config", "General", ["global answer"], LexiconScope.Global);

        _ = await lexicon.UpsertAsync("config", "General", ["campaign answer"], LexiconScope.ForCampaign(CampaignA));

        _ = await lexicon.UpsertAsync("config", "General", ["other campaign secret"], LexiconScope.ForCampaign(CampaignB));

        MemoryRecallCandidateReader reader = Reader(db, options, lexicon);

        Result<MemoryRecallCandidates> recalled = await reader.ReadAsync(new("config", MemoryRecallScope.Lexicon),
            Canonical(CampaignA), MemoryScope.Resolve(true, CampaignA), CancellationToken.None);

        Assert.True(recalled.IsSuccess, recalled.Error.Message);

        MemoryRecallFragment fragment = Assert.Single(recalled.Value.Items);

        Assert.Contains("campaign answer", fragment.Content);

        Assert.Equal(CampaignA, fragment.CampaignId);

        Assert.DoesNotContain("global answer", fragment.Content);

        Assert.NotEmpty(fragment.Provenance);

        await db.Database.OpenConnectionAsync();

        await using var command = db.Database.GetDbConnection().CreateCommand();

        command.CommandText = "UPDATE lexicon_entries SET RetiredAtUtc = '2026-09-01T00:00:00.0000000Z' WHERE ScopeCampaignId <> '';";

        _ = await command.ExecuteNonQueryAsync();

        Result<MemoryRecallCandidates> afterRetirement = await reader.ReadAsync(new("config", MemoryRecallScope.Lexicon),
            Canonical(CampaignA), MemoryScope.Resolve(true, CampaignA), CancellationToken.None);

        Assert.True(afterRetirement.IsSuccess);

        Assert.Contains("global answer", Assert.Single(afterRetirement.Value.Items).Content);
    }

    [Fact]
    public async Task Clean_saga_local_recall_excludes_other_campaign_and_retired_memories()
    {
        await using ArcanumDbContext db = fixture.CreateContext(fixture.CopyDatabase());

        TestOptionsMonitor<ArcanumSettings> options = Options();

        WeaveIndexAvailability availability = new();

        SagaMemoryStore saga = new(db, availability, options, MemoryErasureTestKeys.Isolated());

        float[] embedding = new float[64];

        embedding[0] = 1;

        DateTimeOffset created = DateTimeOffset.Parse("2026-10-20T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

        _ = await saga.InsertAsync("a", "semantic answer", created, null, null, "test", embedding, CancellationToken.None);

        _ = await saga.InsertAsync("b", "other campaign secret", created, null, null, "test", embedding, CancellationToken.None);

        _ = await saga.InsertAsync("c", "retired secret", created, null, null, "test", embedding, CancellationToken.None);

        await db.Database.OpenConnectionAsync();

        await using var command = db.Database.GetDbConnection().CreateCommand();

        command.CommandText = "UPDATE saga_memories SET ScopeKindCode = 2, CampaignId = $campaign WHERE Id = 'b'; UPDATE saga_memories SET RetiredAtUtc = '2026-10-20T12:00:00Z' WHERE Id = 'c';";

        var parameter = command.CreateParameter();

        parameter.ParameterName = "$campaign";

        parameter.Value = CampaignB.ToString("D").ToUpperInvariant();

        command.Parameters.Add(parameter);

        _ = await command.ExecuteNonQueryAsync();

        LexiconService lexicon = new(db, NullLogger<LexiconService>.Instance, options, MemoryErasureTestKeys.Isolated());

        ConstantWeave provider = new(embedding);

        MemoryRecallCandidateReader reader = Reader(db, options, lexicon, provider);

        Result<MemoryRecallCandidates> recalled = await reader.ReadAsync(new("answer", MemoryRecallScope.Saga),
            Canonical(CampaignA), MemoryScope.Resolve(true, CampaignA), CancellationToken.None);

        Assert.True(recalled.IsSuccess, recalled.Error.Message);

        MemoryRecallFragment fragment = Assert.Single(recalled.Value.Items);

        Assert.Equal("a", fragment.Id);

        Assert.Equal("semantic answer", fragment.Content);

        Assert.Equal(0, provider.EmbedCalls);

        Assert.Null(fragment.Similarity);

        Assert.Contains("sha256", fragment.Provenance);
    }

    [Fact]
    public async Task Canonical_campaign_mismatch_is_refused_before_any_content_is_read()
    {
        await using ArcanumDbContext db = fixture.CreateContext(fixture.CopyDatabase());

        TestOptionsMonitor<ArcanumSettings> options = Options();

        LexiconService lexicon = new(db, NullLogger<LexiconService>.Instance, options, MemoryErasureTestKeys.Isolated());

        MemoryRecallCandidateReader reader = Reader(db, options, lexicon);

        Result<MemoryRecallCandidates> result = await reader.ReadAsync(new("config"), Canonical(CampaignA),
            MemoryScope.Resolve(true, CampaignB), CancellationToken.None);

        Assert.True(result.IsFailure);
    }

    [Fact]
    public async Task Protected_aliases_and_deleted_owners_are_excluded_before_content_hydration()
    {
        await using ArcanumDbContext db = fixture.CreateContext(fixture.CopyDatabase());

        TestOptionsMonitor<ArcanumSettings> options = Options();

        WeaveIndexAvailability availability = new();

        SagaMemoryStore saga = new(db, availability, options, MemoryErasureTestKeys.Isolated());

        float[] vector = new float[64];

        vector[0] = 1;

        foreach (string id in new[] { "clean", "protected", "deleted" })
        {
            _ = await saga.InsertAsync(id, "answer " + id, DateTimeOffset.UtcNow, null, null, "test", vector, CancellationToken.None);
        }

        await db.Database.OpenConnectionAsync();

        await using var command = db.Database.GetDbConnection().CreateCommand();

        command.CommandText = """
            INSERT INTO artifact_sensitivity (
                LabelId, ArtifactKindCode, ArtifactId, SensitivityCode, ProvenanceModeCode,
                ExactGenerationIds, GenerationBloom, SessionId, CampaignId, TurnId,
                ArtifactRevision, ArtifactContentDigest, SensitivityDigest, ProducingPlanDigest,
                ProducingAdmissionDigest, ProducingMaintenanceReceiptDigest, ArtifactLabelDigest, CreatedAtUtc)
            VALUES ('label', 6, 'PRO-TECTED', 1, 1, randomblob(16), NULL, NULL, NULL, NULL,
                1, zeroblob(32), zeroblob(32), NULL, NULL, NULL, zeroblob(32), '2026-10-20T12:00:00Z');
            INSERT INTO "Sessions" ("Id", "CreatedAt", "UpdatedAt")
            VALUES ('D0000000-0000-4000-8000-0000000000DD', '2026-10-20T12:00:00Z', '2026-10-20T12:00:00Z');
            UPDATE saga_memories SET SessionId = 'D0000000-0000-4000-8000-0000000000DD' WHERE Id = 'deleted';
            UPDATE saga_memories SET CreatedAt = 'invalid protected timestamp' WHERE Id IN ('protected', 'deleted');
            INSERT INTO owner_deletion_events (OwnerKindCode, OwnerId, DeletedAtUtc)
            VALUES (2, 'D0000000-0000-4000-8000-0000000000DD', '2026-10-20T12:00:00Z');
            """;

        _ = await command.ExecuteNonQueryAsync();

        LexiconService lexicon = new(db, NullLogger<LexiconService>.Instance, options, MemoryErasureTestKeys.Isolated());

        MemoryRecallCandidateReader reader = Reader(db, options, lexicon, new ConstantWeave(vector));

        Result<MemoryRecallCandidates> recalled = await reader.ReadAsync(new("answer", MemoryRecallScope.Saga),
            Canonical(CampaignA), MemoryScope.Resolve(true, CampaignA), CancellationToken.None);

        Assert.True(recalled.IsSuccess, recalled.Error.Message);

        Assert.Equal("clean", Assert.Single(recalled.Value.Items).Id);

        SagaMemoryCurationRow[] narrow = await saga.ListCleanRecallRowsAsync("answer",
            MemoryScope.Resolve(true, CampaignA), 1, CancellationToken.None);

        Assert.Equal("clean", Assert.Single(narrow).Memory.Id);
    }

    [Fact]
    public async Task Live_session_memories_survive_the_stores_lowercase_session_spelling()
    {
        await using ArcanumDbContext db = fixture.CreateContext(fixture.CopyDatabase());

        TestOptionsMonitor<ArcanumSettings> options = Options();

        Guid sessionId = new("E0000000-0000-4000-8000-0000000000EE");

        await db.Database.OpenConnectionAsync();

        await using var command = db.Database.GetDbConnection().CreateCommand();

        command.CommandText = """
            INSERT INTO "Sessions" ("Id", "CreatedAt", "UpdatedAt")
            VALUES ('E0000000-0000-4000-8000-0000000000EE', '2026-10-20T12:00:00Z', '2026-10-20T12:00:00Z');
            INSERT INTO session_campaign_bindings (SessionId, BindingKindCode, CampaignId, BoundAtUtc)
            VALUES ('E0000000-0000-4000-8000-0000000000EE', 1, NULL, '2026-10-20T12:00:00Z');
            """;

        using (CovenantSqliteConnectionInitializer.Instance.Authorize((SqliteConnection)command.Connection!,
            CovenantSqliteAuthorizationKind.SessionBindingWrite))
        {
            _ = await command.ExecuteNonQueryAsync();
        }

        WeaveIndexAvailability availability = new();

        SagaMemoryStore saga = new(db, availability, options, MemoryErasureTestKeys.Isolated());

        float[] vector = new float[64];

        vector[0] = 1;

        _ = await saga.InsertAsync("live-session", "answer from live session", DateTimeOffset.UtcNow,
            sessionId, null, "test", vector, CancellationToken.None);

        SagaMemoryCurationRow[] recalled = await saga.ListCleanRecallRowsAsync("answer",
            MemoryScope.Resolve(true, CampaignA), 10, CancellationToken.None);

        Assert.Equal("live-session", Assert.Single(recalled).Memory.Id);
    }

    private static TestOptionsMonitor<ArcanumSettings> Options() => new(new ArcanumSettings
    {
        Features = new FeatureSettings { Saga = true, Embeddings = true, Lexicon = true, CampaignScopedMemory = true },
        Integrations = new IntegrationSettings
        {
            Embeddings = new EmbeddingIntegrationSettings { Dimensions = 64, Provider = "test", Model = "test" },
        },
    });

    private static CanonicalCampaignContext Canonical(Guid campaignId) => CanonicalCampaignContext.Create(
        SessionCampaignBinding.ForCampaign(campaignId), 1, 1, null, null);

    private static MemoryRecallCandidateReader Reader(ArcanumDbContext db,
        TestOptionsMonitor<ArcanumSettings> options, LexiconService lexicon, IWeaveService? weave = null)
    {
        WeaveIndexAvailability availability = new();

        ServiceCollection services = new();

        services.AddSingleton<IWeaveService>(weave ?? new ConstantWeave(new float[64]));

        services.AddSingleton<IDivinationService>(new DivinationService(db, availability, NullLogger<DivinationService>.Instance, options));

        services.AddSingleton<ISagaMemoryStore>(new SagaMemoryStore(db, availability, options, MemoryErasureTestKeys.Isolated()));

        services.AddSingleton<ILexiconService>(lexicon);

        services.AddSingleton<IOptionsMonitor<ArcanumSettings>>(options);

        services.AddTransient<MemoryRecallCandidateReader>();

        return services.BuildServiceProvider().GetRequiredService<MemoryRecallCandidateReader>();
    }

    private sealed class ConstantWeave(float[] embedding) : IWeaveService
    {
        public int EmbedCalls { get; private set; }

        public bool IsAvailable => true;

        public Task<Result<Embedding<float>>> EmbedAsync(string text, CancellationToken cancellationToken)
        {
            EmbedCalls++;

            return Task.FromResult<Result<Embedding<float>>>(new Embedding<float>(embedding));
        }

        public Task<Result<Embedding<float>[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<(string Chunk, int Offset)[]>> ChunkAsync(string text, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
