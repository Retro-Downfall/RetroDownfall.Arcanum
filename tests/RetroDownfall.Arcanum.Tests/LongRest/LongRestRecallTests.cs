using System.Data.Common;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Data;
using RetroDownfall.Arcanum.Tests.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed class LongRestRecallTests
{
    private static readonly DateTimeOffset Recorded = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unscoped_recall_filters_exact_suppressed_sources_before_the_result_limit(bool accelerator)
    {
        await using SagaStoreHarness h = await SagaStoreHarness.CreateAsync(annalsEnabled: true);

        await SeedAsync(h);

        h.VectorAccelerator.SetAvailable(accelerator);

        Result<DivinationResult[]> result = await SearchAsync(h);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal("survivor", Assert.Single(result.Value).Id);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Campaign_recall_filters_the_global_arm_before_scoring(bool accelerator)
    {
        await using SagaStoreHarness h = await SagaStoreHarness.CreateAsync(annalsEnabled: true);

        await SeedAsync(h);

        Guid outsideSession = await h.SessionBoundToNewCampaignAsync();

        _ = await h.Store.InsertAsync("outside", "another Campaign", Recorded, outsideSession, null, null, Vector(1, 0), CancellationToken.None);

        h.VectorAccelerator.SetAvailable(accelerator);

        DivinationCampaignScope scope = new("saga_memories", "Id", "ScopeKindCode", "CampaignId", 1, 2,
            Guid.Parse("12345678-1234-1234-1234-123456789012"));

        Result<DivinationResult[]> result = await Service(h).SearchCampaignScopedAsync("saga_memory_embeddings_vec",
            "MemoryId", "Embedding", scope, new Embedding<float>(Vector(1, 0)), 1, 0.5f, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal("survivor", Assert.Single(result.Value).Id);
    }

    [SkippableFact]
    public async Task The_generic_scoped_Saga_path_cannot_return_an_exact_suppressed_source()
    {
        await using SagaStoreHarness h = await SagaStoreHarness.CreateAsync(annalsEnabled: true);

        await SeedAsync(h);

        Result<DivinationResult[]> result = await Service(h).SearchScopedAsync("saga_memory_embeddings_vec", "MemoryId", "Embedding",
            "saga_memories", "Id", "Source", "test-source", new Embedding<float>(Vector(1, 0)), 1, 0.5f, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal("survivor", Assert.Single(result.Value).Id);
    }

    [SkippableFact]
    public async Task Inspection_keeps_each_source_and_reports_the_exact_receipt_output()
    {
        await using SagaStoreHarness h = await SagaStoreHarness.CreateAsync(annalsEnabled: true);

        string survivorVersion = await SeedAsync(h);

        Assert.Equal(["survivor"], (await h.Store.GetByIdsAsync(["source", "survivor"], CancellationToken.None)).Keys);

        SagaMemoryDto[] listing = await h.Store.ListAsync(null, null, MemoryScope.Installation, 10, 0, CancellationToken.None);

        Assert.Equal(2, listing.Length);

        SagaMemoryDto source = Assert.Single(listing, row => row.Id == "source");

        Assert.Equal("survivor", source.ConsolidatedIntoMemoryId);

        Assert.Equal(survivorVersion, source.ConsolidatedIntoVersionId);

        Assert.Equal("test-receipt", source.LongRestReceiptId);

        SagaMemoryCurationRow detail = (await h.Store.ReadCurationRowAsync("source", CancellationToken.None))!;

        Assert.Equal(SagaRetrievalEligibility.Consolidated, SagaRetrievalEligibilityClassifier.Classify(detail, false));

        Assert.True(detail.HasEmbedding);

        SagaMemoryCurationRow[] rows = await h.Store.ListCurationRowsAsync(null, null, MemoryScope.Installation, 10, 0, CancellationToken.None);

        Assert.Equal(survivorVersion, Assert.Single(rows, row => row.Memory.Id == "source").Memory.ConsolidatedIntoVersionId);
    }

    [SkippableFact]
    public async Task A_source_pin_temporarily_releases_its_exact_suppression_without_changing_the_receipt()
    {
        await using SagaStoreHarness h = await SagaStoreHarness.CreateAsync(annalsEnabled: true);

        await SeedAsync(h);

        _ = await h.Store.SetPinAsync("source", true, Recorded.AddDays(1), CancellationToken.None);

        IReadOnlyDictionary<string, SagaMemoryDto> pinned = await h.Store.GetByIdsAsync(["source"], CancellationToken.None);

        Assert.Single(pinned);

        Assert.Null(pinned["source"].LongRestReceiptId);

        Assert.Equal("source", Assert.Single((await SearchAsync(h)).Value).Id);

        _ = await h.Store.SetPinAsync("source", false, Recorded.AddDays(2), CancellationToken.None);

        Assert.Empty(await h.Store.GetByIdsAsync(["source"], CancellationToken.None));

        Assert.Equal("survivor", Assert.Single((await SearchAsync(h)).Value).Id);

        Assert.Equal(1, await h.CountAsync("long_rest_suppressions", "1 = 1"));
    }

    [SkippableFact]
    public async Task A_source_correction_releases_the_new_head_while_preserving_historical_suppression()
    {
        await using SagaStoreHarness h = await SagaStoreHarness.CreateAsync(annalsEnabled: true);

        await SeedAsync(h);

        SagaCurationOutcome changed = await h.Store.CorrectAsync("source", AnnalContentDigest.ForSagaMemory("same observation"),
            "corrected source", Vector(1, 0), Recorded.AddDays(1), CancellationToken.None);

        Assert.Equal(SagaCurationOutcomeKind.Applied, changed.Kind);

        SagaMemoryDto corrected = Assert.Single(await h.Store.GetByIdsAsync(["source"], CancellationToken.None)).Value;

        Assert.Null(corrected.LongRestReceiptId);

        Assert.Equal("source", Assert.Single((await SearchAsync(h)).Value).Id);

        Assert.Equal(1, await h.CountAsync("long_rest_suppressions", "1 = 1"));
    }

    [SkippableFact]
    public async Task Survivor_correction_does_not_revive_an_exact_suppressed_source()
    {
        await using SagaStoreHarness h = await SagaStoreHarness.CreateAsync(annalsEnabled: true);

        string originalOutput = await SeedAsync(h);

        _ = await h.Store.CorrectAsync("survivor", AnnalContentDigest.ForSagaMemory("same observation"),
            "corrected survivor", Vector(0.8f, 0.6f), Recorded.AddDays(1), CancellationToken.None);

        Assert.Empty(await h.Store.GetByIdsAsync(["source"], CancellationToken.None));

        SagaMemoryCurationRow source = (await h.Store.ReadCurationRowAsync("source", CancellationToken.None))!;

        Assert.Equal(originalOutput, source.Memory.ConsolidatedIntoVersionId);

        Assert.Equal(SagaRetrievalEligibility.Consolidated, SagaRetrievalEligibilityClassifier.Classify(source, false));

        Assert.Equal("survivor", Assert.Single((await SearchAsync(h)).Value).Id);
    }

    [SkippableFact]
    public async Task Survivor_retirement_keeps_sources_inspectable_and_makes_recall_empty()
    {
        await using SagaStoreHarness h = await SagaStoreHarness.CreateAsync(annalsEnabled: true);

        await SeedAsync(h);

        _ = await h.Store.RetireAsync("survivor", AnnalContentDigest.ForSagaMemory("same observation"),
            Recorded.AddDays(1), CancellationToken.None);

        Result<DivinationResult[]> result = await SearchAsync(h);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Empty(result.Value);

        Assert.False(await h.Store.AnyRetrievableAsync(MemoryScope.Installation, CancellationToken.None));

        SagaMemoryCurationRow source = (await h.Store.ReadCurationRowAsync("source", CancellationToken.None))!;

        Assert.Equal(SagaRetrievalEligibility.Consolidated, SagaRetrievalEligibilityClassifier.Classify(source, false));

        Assert.Equal(2, (await h.Store.ListAsync(null, null, MemoryScope.Installation, 10, 0, CancellationToken.None)).Length);
    }

    [SkippableFact]
    public async Task Erasing_a_receipt_subject_releases_the_surviving_subjects_own_eligibility()
    {
        await using SagaStoreHarness h = await SagaStoreHarness.CreateAsync(annalsEnabled: true);

        await SeedAsync(h);

        Assert.True(await h.Store.DeleteAsync("survivor", CancellationToken.None));

        SagaMemoryDto source = Assert.Single(await h.Store.GetByIdsAsync(["source"], CancellationToken.None)).Value;

        Assert.Null(source.LongRestReceiptId);

        Assert.Equal("source", Assert.Single((await SearchAsync(h)).Value).Id);

        Assert.Equal(0, await h.CountAsync("long_rest_receipts", "1 = 1"));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Version_sixteen_readers_need_no_Long_Rest_tables_and_keep_each_existing_memory(bool accelerator)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using (var connection = await file.OpenAsync(CancellationToken.None))
        {
            GrimoireSchemaInstallResult installed = await GrimoireSchemaTestInstaller.InstallAsync(connection,
                CoreSchemaVersionSixteenFixture.ChainSet(), 64, CancellationToken.None);

            Assert.Equal(16, installed.Core.SchemaVersion);
        }

        await using ArcanumDbContext db = SagaMemoryMidUpgradeWriteTests.CreateContext(file);

        WeaveIndexAvailability availability = new();

        availability.SetAvailable(accelerator);

        SagaMemoryStore store = new(db, availability,
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings
            {
                Features = new FeatureSettings { Annals = false },

                Integrations = new IntegrationSettings { Embeddings = new EmbeddingIntegrationSettings { Dimensions = 64 } },
            }), MemoryErasureTestKeys.Isolated());

        _ = await store.InsertAsync("legacy", "legacy observation", Recorded, null, null, null, Vector(1, 0), CancellationToken.None);

        SagaMemoryDto listed = Assert.Single(await store.ListAsync(null, null, MemoryScope.Installation, 10, 0, CancellationToken.None));

        Assert.Null(listed.LongRestReceiptId);

        Assert.Null((await store.ReadCurationRowAsync("legacy", CancellationToken.None))!.Memory.ConsolidatedIntoMemoryId);

        Assert.Single(await store.GetByIdsAsync(["legacy"], CancellationToken.None));

        Assert.True(await store.AnyRetrievableAsync(MemoryScope.Installation, CancellationToken.None));

        DivinationService service = new(db, availability, NullLogger<DivinationService>.Instance);

        Result<DivinationResult[]> recall = await service.SearchAsync("saga_memory_embeddings_vec", "MemoryId", "Embedding",
            new Embedding<float>(Vector(1, 0)), 1, 0.5f, CancellationToken.None);

        Assert.True(recall.IsSuccess, recall.Error.Message);

        Assert.Equal("legacy", Assert.Single(recall.Value).Id);
    }

    private static async Task<string> SeedAsync(SagaStoreHarness h)
    {
        _ = await h.Store.InsertAsync("source", "same observation", Recorded, null, null, "test-source", Vector(1, 0), CancellationToken.None);

        _ = await h.Store.InsertAsync("survivor", "same observation", Recorded, null, null, "test-source", Vector(0.8f, 0.6f), CancellationToken.None);

        AnnalClaimHead source = (await h.Annals.GetClaimAsync(AnnalSubjectStore.Saga, "source", CancellationToken.None))!;

        AnnalClaimHead survivor = (await h.Annals.GetClaimAsync(AnnalSubjectStore.Saga, "survivor", CancellationToken.None))!;

        await using DbCommand command = h.Connection.CreateCommand();

        command.CommandText =
            """
            INSERT INTO long_rest_receipts (ReceiptId, PolicyVersion, KindCode, OutcomeCode, ReasonCode,
                InputHash, OutputHash, SurvivorMemoryId, SurvivorVersionId, CreatedAtUtc)
            VALUES ('test-receipt', 1, 1, 1, 0, $inputHash, $outputHash, 'survivor', $survivor, '2026-01-01T00:00:00.0000000Z');
            INSERT INTO long_rest_receipt_inputs (ReceiptId, Ordinal, MemoryId, VersionId, ContentHashFormatCode, ContentHash, SnapshotHash, SnapshotJson)
            VALUES ('test-receipt', 1, 'source', $source, 1, $hash, $inputHash, '{}');
            INSERT INTO long_rest_receipt_inputs (ReceiptId, Ordinal, MemoryId, VersionId, ContentHashFormatCode, ContentHash, SnapshotHash, SnapshotJson)
            VALUES ('test-receipt', 2, 'survivor', $survivor, 1, $hash, $inputHash, '{}');
            INSERT INTO long_rest_suppressions (SourceVersionId, SurvivorVersionId, ReceiptId)
            VALUES ($source, $survivor, 'test-receipt');
            """;

        foreach ((string name, object value) in new (string, object)[]
        {
            ("$source", source.CurrentVersionId),

            ("$survivor", survivor.CurrentVersionId),

            ("$hash", AnnalContentDigest.ForSagaMemory("same observation")),

            ("$inputHash", new string('A', 64)),

            ("$outputHash", new string('B', 64)),
        })
        {
            DbParameter parameter = command.CreateParameter();

            parameter.ParameterName = name;

            parameter.Value = value;

            _ = command.Parameters.Add(parameter);
        }

        _ = await command.ExecuteNonQueryAsync();

        return survivor.CurrentVersionId;
    }

    private static DivinationService Service(SagaStoreHarness h) =>
        new(h.Context, h.VectorAccelerator, NullLogger<DivinationService>.Instance);

    private static Task<Result<DivinationResult[]>> SearchAsync(SagaStoreHarness h) =>
        Service(h).SearchAsync("saga_memory_embeddings_vec", "MemoryId", "Embedding", new Embedding<float>(Vector(1, 0)),
            1, 0.5f, CancellationToken.None);

    private static float[] Vector(float first, float second)
    {
        float[] vector = new float[64];

        vector[0] = first;

        vector[1] = second;

        return vector;
    }
}
