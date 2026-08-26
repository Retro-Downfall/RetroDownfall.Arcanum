using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed class SagaSuppressionDigestTests
{

    private static readonly byte[] Key = Enumerable.Range(0, 32).Select(static i => (byte)i).ToArray();

    [Fact]
    public void The_same_content_in_the_same_scope_produces_the_same_digest()
    {

        Assert.Equal(
            SagaSuppressionDigest.Compute(Key, SagaMemoryScopeKind.Global, null, "the operator prefers tabs"),
            SagaSuppressionDigest.Compute(Key, SagaMemoryScopeKind.Global, null, "the operator prefers tabs"));

    }

    [Fact]
    public void The_same_content_in_a_different_Campaign_produces_a_different_digest()
    {

        // A rejection made inside one piece of work does not govern another the operator never had an
        // opinion about.
        string first = "11111111-1111-1111-1111-111111111111";

        string second = "22222222-2222-2222-2222-222222222222";

        Assert.NotEqual(
            SagaSuppressionDigest.Compute(Key, SagaMemoryScopeKind.Campaign, first, "the operator prefers tabs"),
            SagaSuppressionDigest.Compute(Key, SagaMemoryScopeKind.Campaign, second, "the operator prefers tabs"));

    }

    [Fact]
    public void The_digest_is_not_the_content_hash_the_Annals_already_stores()
    {

        // Domain separation is the whole reason this is keyed: an unkeyed digest would be the identical
        // value annal_versions.ContentHash holds, and the two tables would join into one oracle.
        Assert.NotEqual(
            AnnalContentDigest.ForSagaMemory("the operator prefers tabs"),
            SagaSuppressionDigest.Compute(Key, SagaMemoryScopeKind.Global, null, "the operator prefers tabs"));

    }

    [Fact]
    public void A_field_boundary_cannot_be_forged_by_content_that_looks_like_the_next_field()
    {

        // Without a separator the pair ("ab", "c") and ("a", "bc") would hash identically.
        Assert.NotEqual(
            SagaSuppressionDigest.Compute(Key, SagaMemoryScopeKind.Campaign, "11111111-1111-1111-1111-111111111111", "x"),
            SagaSuppressionDigest.Compute(Key, SagaMemoryScopeKind.Campaign, "11111111-1111-1111-1111-1111111111", "11x"));

    }

    [Fact]
    public void A_different_key_produces_a_different_digest()
    {

        byte[] other = new byte[32];

        other[0] = 0xFF;

        Assert.NotEqual(
            SagaSuppressionDigest.Compute(Key, SagaMemoryScopeKind.Global, null, "the operator prefers tabs"),
            SagaSuppressionDigest.Compute(other, SagaMemoryScopeKind.Global, null, "the operator prefers tabs"));

    }

}

public sealed class SagaSuppressionKeyStoreTests
{

    [SkippableFact]
    public async Task The_key_is_created_once_and_read_back_unchanged()
    {

        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

        Assert.Null(await SagaSuppressionKeyStore
            .ReadAsync(harness.Connection, null, CancellationToken.None)
            .ConfigureAwait(false));

        byte[] first = await SagaSuppressionKeyStore
            .ReadOrCreateAsync(harness.Connection, null, DateTimeOffset.UtcNow, CancellationToken.None)
            .ConfigureAwait(false);

        byte[] second = await SagaSuppressionKeyStore
            .ReadOrCreateAsync(harness.Connection, null, DateTimeOffset.UtcNow, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.Equal(32, first.Length);

        Assert.Equal(first, second);

    }

}

/// <summary>
/// The insert chokepoint: a retirement's suppression is checked inside <c>InsertCoreAsync</c>, after
/// scope is derived and before the row lands, so extraction cannot re-add exactly what an operator
/// just retired.
/// </summary>
public sealed class SagaSuppressionTests
{

    [SkippableFact]
    public async Task A_retired_memory_is_not_written_again_by_the_path_that_wrote_it()
    {

        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

        _ = await harness.Store.InsertAsync(
            "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
            null, null, null, harness.Embedding(), CancellationToken.None).ConfigureAwait(false);

        _ = await harness.Store.RetireAsync(
            "m-1",
            AnnalContentDigest.ForSagaMemory("the operator prefers tabs"),
            DateTimeOffset.UtcNow,
            CancellationToken.None).ConfigureAwait(false);

        SagaMemoryWriteOutcome outcome = await harness.Store.InsertAsync(
            "m-2", "the operator prefers tabs", DateTimeOffset.UtcNow,
            null, null, null, harness.Embedding(), CancellationToken.None).ConfigureAwait(false);

        Assert.Equal(SagaMemoryWriteOutcome.Suppressed, outcome);

        Assert.Equal(0, await harness.CountAsync("saga_memories", "Id = 'm-2'").ConfigureAwait(false));

    }

    [SkippableFact]
    public async Task A_suppression_made_in_one_Campaign_does_not_govern_another()
    {

        // Written through two Sessions the classifier resolves to different Campaigns, so the scope on each
        // row is derived exactly as production derives it rather than declared by the test.
        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

        Guid first = await harness.SessionBoundToNewCampaignAsync().ConfigureAwait(false);

        Guid second = await harness.SessionBoundToNewCampaignAsync().ConfigureAwait(false);

        _ = await harness.Store.InsertAsync(
            "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
            first, null, null, harness.Embedding(), CancellationToken.None).ConfigureAwait(false);

        _ = await harness.Store.RetireAsync(
            "m-1",
            AnnalContentDigest.ForSagaMemory("the operator prefers tabs"),
            DateTimeOffset.UtcNow,
            CancellationToken.None).ConfigureAwait(false);

        SagaMemoryWriteOutcome outcome = await harness.Store.InsertAsync(
            "m-2", "the operator prefers tabs", DateTimeOffset.UtcNow,
            second, null, null, harness.Embedding(), CancellationToken.None).ConfigureAwait(false);

        Assert.Equal(SagaMemoryWriteOutcome.Written, outcome);

    }

    [SkippableFact]
    public async Task Reinstating_the_memory_lets_the_same_conclusion_be_written_again()
    {

        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

        _ = await harness.Store.InsertAsync(
            "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
            null, null, null, harness.Embedding(), CancellationToken.None).ConfigureAwait(false);

        byte[] digest = AnnalContentDigest.ForSagaMemory("the operator prefers tabs");

        _ = await harness.Store.RetireAsync("m-1", digest, DateTimeOffset.UtcNow, CancellationToken.None)
            .ConfigureAwait(false);

        _ = await harness.Store.ReinstateAsync(
            "m-1", digest, harness.Embedding(), DateTimeOffset.UtcNow, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.Equal(
            SagaMemoryWriteOutcome.Written,
            await harness.Store.InsertAsync(
                "m-2", "the operator prefers tabs", DateTimeOffset.UtcNow,
                null, null, null, harness.Embedding(), CancellationToken.None).ConfigureAwait(false));

    }

}
