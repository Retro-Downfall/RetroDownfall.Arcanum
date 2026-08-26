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
