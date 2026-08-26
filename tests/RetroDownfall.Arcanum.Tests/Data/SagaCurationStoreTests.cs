using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed class SagaCurationStoreTests
{

    [SkippableFact]
    public async Task A_freshly_written_memory_reads_back_active_unpinned_and_embedded()
    {

        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

        await harness.Store.InsertAsync(
            "m-1",
            "the operator prefers tabs",
            DateTimeOffset.UtcNow,
            sessionId: null,
            tags: null,
            source: null,
            harness.Embedding(),
            CancellationToken.None).ConfigureAwait(false);

        SagaMemoryCurationRow? row = await harness.Store
            .ReadCurationRowAsync("m-1", CancellationToken.None)
            .ConfigureAwait(false);

        Assert.NotNull(row);

        Assert.Null(row.Lifecycle.RetiredAtUtc);

        Assert.Null(row.Lifecycle.PinnedAtUtc);

        Assert.True(row.HasEmbedding);

    }

    [SkippableFact]
    public async Task An_unknown_identity_reads_back_as_nothing_rather_than_as_an_error()
    {

        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

        Assert.Null(await harness.Store
            .ReadCurationRowAsync("m-absent", CancellationToken.None)
            .ConfigureAwait(false));

    }

}
