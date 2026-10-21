using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Weave;

namespace RetroDownfall.Arcanum.Tests.Memory;

public sealed class MemoryRecallPagerTests
{
    private static readonly MemoryRecallFragment[] Candidates =
    [
        new(MemoryRecallScope.Saga, "b", "semantic answer", 0.8f, Provenance: "source B"),
        new(MemoryRecallScope.Saga, "a", "first answer", 0.9f, Provenance: "source A"),
        new(MemoryRecallScope.Lexicon, "c", "entity answer", Provenance: "source C"),
    ];

    [Fact]
    public void Page_filters_sources_orders_deterministically_and_continues_without_duplicates()
    {
        MemoryRecallRequest request = new("query", MemoryRecallScope.Saga, 1);

        Result<MemoryRecallPage> first = Page(request);

        Assert.True(first.IsSuccess);

        Assert.Equal("a", Assert.Single(first.Value.Items).Id);

        Assert.NotNull(first.Value.NextCursor);

        Result<MemoryRecallPage> next = Page(request with { Cursor = first.Value.NextCursor });

        Assert.True(next.IsSuccess);

        Assert.Equal("b", Assert.Single(next.Value.Items).Id);

        Assert.Null(next.Value.NextCursor);
    }

    [Fact]
    public void Cursor_refuses_changed_query_scope_content_and_authentication()
    {
        MemoryRecallRequest request = new("query", Limit: 1);

        MemoryRecallPage first = Page(request).Value;

        Assert.NotNull(first.NextCursor);

        Assert.True(Page(request with { Query = "other", Cursor = first.NextCursor }).IsFailure);

        Assert.True(Page(request with { Scope = MemoryRecallScope.Saga, Cursor = first.NextCursor }).IsFailure);

        Assert.True(Page(request with { Cursor = first.NextCursor }, [Candidates[0] with { Content = "changed" }, Candidates[1], Candidates[2]]).IsFailure);

        Assert.True(Page(request with { Cursor = first.NextCursor + "x" }).IsFailure);
    }

    [Theory]
    [InlineData("", 10, null)]
    [InlineData("query", 0, null)]
    [InlineData("query", 21, null)]
    [InlineData("query", 10, "invalid")]
    public void Invalid_inputs_fail_without_echoing_content(string query, int limit, string? cursor)
    {
        Result<MemoryRecallPage> result = Page(new(query, Limit: limit, Cursor: cursor));

        Assert.True(result.IsFailure);

        Assert.DoesNotContain("query", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Byte_budget_returns_smaller_page_and_does_not_skip_oversized_candidate()
    {
        MemoryRecallFragment[] rows =
        [
            new(MemoryRecallScope.Saga, "a", new string('"', 200)),
            new(MemoryRecallScope.Saga, "b", new string('"', 200)),
        ];

        Result<MemoryRecallPage> bounded = Page(new("query"), rows, 1600);

        Assert.True(bounded.IsSuccess);

        Assert.Equal("a", Assert.Single(bounded.Value.Items).Id);

        Assert.NotNull(bounded.Value.NextCursor);

        Result<MemoryRecallPage> tooSmall = Page(new("query"), rows, 100);

        Assert.True(tooSmall.IsFailure);
    }

    [Fact]
    public void Candidate_bound_is_reported_and_long_inputs_are_refused()
    {
        Result<MemoryRecallPage> page = MemoryRecallPager.CreatePage(new("query"), CanonicalCampaignContext.GlobalOnly,
            MemoryScope.Installation, [], 4096, candidateLimitReached: true);

        Assert.True(page.Value.CandidateLimitReached);

        Assert.True(Page(new(new string('q', 513))).IsFailure);

        Assert.True(Page(new("query", Cursor: new string('c', 2049))).IsFailure);
    }

    private static Result<MemoryRecallPage> Page(MemoryRecallRequest request,
        IReadOnlyList<MemoryRecallFragment>? candidates = null, int cap = 4096) =>
        MemoryRecallPager.CreatePage(request, CanonicalCampaignContext.GlobalOnly, MemoryScope.Installation,
            candidates ?? Candidates, cap);
}
