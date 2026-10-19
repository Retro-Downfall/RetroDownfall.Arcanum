using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Support;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed class SagaRankingRecallTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dormancy_is_applied_before_top_k_and_is_shared_with_hydration_and_inspection(bool accelerator)
    {
        await using SagaStoreHarness h = await SagaStoreHarness.CreateAsync(annalsEnabled: true);
        h.Options.CurrentValue.Features.LongRest = true;
        h.VectorAccelerator.SetAvailable(accelerator);
        await Insert(h, "old", Now.AddDays(-500), 1f, 0f);
        await Insert(h, "fresh", Now.AddDays(-1), 0.8f, 0.6f);
        var result = await Service(h).SearchAsync("saga_memory_embeddings_vec", "MemoryId", "Embedding", new(Vector(1, 0)), 1, 0.5f, CancellationToken.None);
        Assert.True(result.IsSuccess, result.Error.Message);
        Assert.Equal("fresh", Assert.Single(result.Value).Id);
        Assert.Equal(0.8f, result.Value[0].Similarity, 5);
        Assert.Empty(await h.Store.GetByIdsAsync(["old"], CancellationToken.None));
        var row = (await h.Store.ReadCurationRowAsync("old", CancellationToken.None))!;
        Assert.Equal("Dormant", SagaRetrievalEligibilityClassifier.Classify(row, false).ToString());
        Assert.True(row.HasEmbedding);
        Assert.Equal(2, await h.Store.CountAsync(CancellationToken.None));
    }

    [SkippableFact]
    public async Task The_feature_gate_preserves_raw_cosine_order_and_pinning_releases_dormancy()
    {
        await using SagaStoreHarness h = await SagaStoreHarness.CreateAsync(annalsEnabled: true);
        await Insert(h, "old", Now.AddDays(-500), 1, 0);
        await Insert(h, "fresh", Now.AddDays(-1), 0.8f, 0.6f);
        var before = await Search(h);
        Assert.Equal("old", Assert.Single(before).Id);
        h.Options.CurrentValue.Features.LongRest = true;
        Assert.Equal("fresh", Assert.Single(await Search(h)).Id);
        await h.Store.SetPinAsync("old", true, Now, CancellationToken.None);
        Assert.Equal("old", Assert.Single(await Search(h)).Id);
        Assert.Single(await h.Store.GetByIdsAsync(["old"], CancellationToken.None));
        h.Options.CurrentValue.Features.LongRest = false;
        var after = await Search(h);
        Assert.Equal(before.Select(x => (x.Id,x.Similarity)), after.Select(x => (x.Id,x.Similarity)));
    }

    [SkippableFact]
    public async Task An_operator_version_is_decay_exempt_and_has_priority_at_equal_similarity()
    {
        await using SagaStoreHarness h = await SagaStoreHarness.CreateAsync(annalsEnabled: true);
        h.Options.CurrentValue.Features.LongRest = true;
        await Insert(h, "a-inferred", Now.AddDays(-1), 1, 0);
        await Insert(h, "z-operator", Now.AddDays(-500), 1, 0);
        await h.Store.CorrectAsync("z-operator", AnnalContentDigest.ForSagaMemory("z-operator"), "operator correction", Vector(1,0), Now.AddDays(-499), CancellationToken.None);
        Assert.Equal("z-operator", Assert.Single(await Search(h)).Id);
    }

    [SkippableFact]
    public async Task A_retired_row_cannot_reenter_recall_through_a_leftover_embedding()
    {
        await using SagaStoreHarness h = await SagaStoreHarness.CreateAsync(annalsEnabled: true);
        h.Options.CurrentValue.Features.LongRest = true;
        await Insert(h, "retired", Now.AddDays(-1), 1, 0);
        await h.Store.RetireAsync("retired", AnnalContentDigest.ForSagaMemory("retired"), Now, CancellationToken.None);
        await using var command = h.Connection.CreateCommand();
        command.CommandText = "INSERT INTO saga_memory_embeddings(MemoryId,Embedding,Dim) VALUES('retired',@embedding,64)";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@embedding";
        parameter.Value = EmbeddingBlobCodec.Encode(Vector(1,0));
        command.Parameters.Add(parameter);
        await command.ExecuteNonQueryAsync();
        Assert.Empty(await Search(h));
    }

    [SkippableFact]
    public async Task Explanation_omits_protected_identity_in_every_normalized_spelling()
    {
        await using SagaStoreHarness h = await SagaStoreHarness.CreateAsync(annalsEnabled: true);

        h.Options.CurrentValue.Features.LongRest = true;

        Guid identity = Guid.NewGuid();

        string memoryId = identity.ToString("N");

        await h.Store.InsertAsync(memoryId, "PROTECTED-SOURCE-CANARY", Now.AddDays(-1), null, null, "extraction", Vector(1, 0), CancellationToken.None);

        using var connections = new CovenantConnectionSource(h.Context, FixtureOrdinaryConnectionFactory.For(h.Context));

        var ledger = new ArtifactSensitivityLedger(connections);

        Assert.True((await ledger.LabelAsync(new DerivedArtifactWrite(SensitiveArtifactKind.Saga, identity,
            null, null, null, 1, DerivedArtifactContentDigest.ForText("PROTECTED-SOURCE-CANARY"), ContentSensitivity.CovenantDerived,
            GenerationProvenance.CreateExact([Guid.NewGuid()])), CancellationToken.None)).IsSuccess);

        var all = (await h.Store.ExplainRankingAsync(MemoryScope.Installation, null, 0.8f, CancellationToken.None))!;

        var exact = (await h.Store.ExplainRankingAsync(MemoryScope.Installation, memoryId, 0.8f, CancellationToken.None))!;

        Assert.Empty(all.Memories);

        Assert.Empty(exact.Memories);
    }

    [SkippableFact]
    public async Task Explanation_is_bounded_and_reports_more_without_hiding_retained_rows()
    {
        await using SagaStoreHarness h = await SagaStoreHarness.CreateAsync(annalsEnabled: true);

        h.Options.CurrentValue.Features.LongRest = true;

        for (int index = 0; index < 52; index++)
        {
            await Insert(h, "memory-" + index.ToString("D2", System.Globalization.CultureInfo.InvariantCulture), Now.AddDays(-1), 1, 0);
        }

        var explanation = (await h.Store.ExplainRankingAsync(MemoryScope.Installation, null, 0.8f, CancellationToken.None))!;

        Assert.Equal(50, explanation.Memories.Length);

        Assert.True(explanation.HasMore);

        Assert.Equal("memory-00", explanation.Memories[0].MemoryId);

        Assert.Equal("memory-49", explanation.Memories[49].MemoryId);

        Assert.Equal(52, await h.Store.CountAsync(CancellationToken.None));
    }

    [SkippableFact]
    public async Task Default_off_hydration_preserves_the_legacy_identity_surface()
    {
        await using SagaStoreHarness h = await SagaStoreHarness.CreateAsync(annalsEnabled: true);

        await Insert(h, "legacy-surface", Now.AddDays(-1), 1, 0);

        SagaMemoryDto disabled = Assert.Single(await h.Store.GetByIdsAsync(["legacy-surface"], CancellationToken.None)).Value;

        Assert.Null(disabled.UseCoordinate);

        Assert.Null(disabled.Ranking);

        h.Options.CurrentValue.Features.LongRest = true;

        SagaMemoryDto enabled = Assert.Single(await h.Store.GetByIdsAsync(["legacy-surface"], CancellationToken.None)).Value;

        Assert.NotNull(enabled.UseCoordinate);

        Assert.NotNull(enabled.Ranking);
    }

    private static Task<SagaMemoryWriteOutcome> Insert(SagaStoreHarness h,string id,DateTimeOffset at,float x,float y) =>
        h.Store.InsertAsync(id,id,at,null,null,"extraction",Vector(x,y),CancellationToken.None);

    private static DivinationService Service(SagaStoreHarness h) =>
        new(h.Context,h.VectorAccelerator,NullLogger<DivinationService>.Instance,h.Options,new FixedTime());

    private static async Task<DivinationResult[]> Search(SagaStoreHarness h) =>
        (await Service(h).SearchAsync("saga_memory_embeddings_vec","MemoryId","Embedding",new(Vector(1,0)),1,0.5f,CancellationToken.None)).Value;

    private static float[] Vector(float x,float y)
    {
        float[] vector = new float[64];
        vector[0] = x;
        vector[1] = y;
        return vector;
    }

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
