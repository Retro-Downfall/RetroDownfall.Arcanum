using RetroDownfall.Arcanum.Core.Weave;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class SagaMemoryStoreTests
{
    [Theory]
    [InlineData("CAFÉ", "Meet at the Café Étoile")]
    [InlineData("éric", "Éric aime les éclairs")]
    [InlineData("ÉCLAIRS", "Éric aime les éclairs")]
    [InlineData("Σ", "final ς and σ")]
    [InlineData("𐐀", "Deseret 𐐨")]
    [InlineData("Ж", "letter ж")]
    [InlineData("é", "decomposed e\u0301")]
    [InlineData("cafe", "café")]
    [InlineData("ss", "ß")]
    [InlineData("s", "ſ")]
    [InlineData("a%b", "a%b")]
    [InlineData("a%b", "axxb")]
    [InlineData("a_b", "axb")]
    [InlineData("a\\b", "a\\b")]
    [InlineData("OR", "AND OR NEAR(foo)")]
    [InlineData("CAFÉ", "before\0café")]
    public async Task Unicode_literal_query_agrees_across_listing_and_both_curation_pages(string query, string content)
    {
        DateTimeOffset instant = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(SagaMemoryWriteOutcome.Written,
            await _store!.InsertAsync("unicode", content, instant, null, null, "extraction", Vec(1f), CancellationToken.None));

        string[] expected = content.Contains(query, StringComparison.OrdinalIgnoreCase) ? ["unicode"] : [];

        SagaMemoryDto[] listed = await _store.ListAsync(query, null, MemoryScope.Installation, 10, 0, CancellationToken.None);

        SagaMemoryCurationRow[] offset = await _store.ListCurationRowsAsync(query, null, MemoryScope.Installation, 10, 0, CancellationToken.None);

        SagaMemoryCurationRow[] seek = await _store.ListCurationRowsAsync(query, null, MemoryScope.Installation, 10, (SagaMemoryPosition?)null, CancellationToken.None);

        Assert.Equal(expected, listed.Select(row => row.Id));

        if (expected.Length != 0) Assert.Equal(content, Assert.Single(listed).Content);

        Assert.Equal(expected, offset.Select(row => row.Memory.Id));

        Assert.Equal(expected, seek.Select(row => row.Memory.Id));
    }

    [Fact]
    public async Task Unicode_query_preserves_session_filter_offset_and_exact_native_seek_order()
    {
        Guid selected = Guid.NewGuid();

        DateTimeOffset instant = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

        foreach (string id in new[] { "a", "b", "c" })
        {
            Assert.Equal(SagaMemoryWriteOutcome.Written,
                await _store!.InsertAsync(id, $"Éric meets at café {id}", instant, selected, null, "extraction", Vec(1f), CancellationToken.None));
        }

        Assert.Equal(SagaMemoryWriteOutcome.Written,
            await _store!.InsertAsync("z", "Éric meets at café z", instant, Guid.NewGuid(), null, "extraction", Vec(1f), CancellationToken.None));

        SagaMemoryDto[] offset = await _store.ListAsync("CAFÉ", selected, MemoryScope.Installation, 1, 1, CancellationToken.None);

        Assert.Equal("b", Assert.Single(offset).Id);

        SagaMemoryPosition? after = null;

        foreach (string expected in new[] { "c", "b", "a" })
        {
            SagaMemoryCurationRow[] page = await _store.ListCurationRowsAsync("éric", selected, MemoryScope.Installation, 1, after, CancellationToken.None);

            SagaMemoryCurationRow row = Assert.Single(page);

            Assert.Equal(expected, row.Memory.Id);

            after = Assert.IsType<SagaMemoryPosition>(row.SearchPosition);
        }

        Assert.Empty(await _store.ListCurationRowsAsync("éric", selected, MemoryScope.Installation, 1, after, CancellationToken.None));
    }
}
