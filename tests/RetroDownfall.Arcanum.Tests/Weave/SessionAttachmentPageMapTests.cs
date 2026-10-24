using RetroDownfall.Arcanum.Infrastructure.Weave;

namespace RetroDownfall.Arcanum.Tests.Weave;

public sealed class SessionAttachmentPageMapTests
{
    private static readonly SessionAttachmentPageMark[] Marks =
    [
        new(0, 4),
        new(10, 9),
        new(25, 12),
        new(26, 13),
        new(100, 40),
    ];

    public static TheoryData<int, int, int, int> ChunkEdges() => new()
    {
        // The window sits inside one page.
        { 0, 10, 4, 4 },
        { 2, 9, 4, 4 },
        { 10, 25, 9, 9 },
        { 30, 100, 13, 13 },
        { 100, 120, 40, 40 },

        // The window starts or ends on a page's first or last character.
        { 9, 11, 4, 9 },
        { 24, 26, 9, 12 },
        { 25, 27, 12, 13 },
        { 25, 26, 12, 12 },
        { 26, 27, 13, 13 },

        // The window crosses several pages, or the whole document.
        { 5, 101, 4, 40 },
        { 0, 101, 4, 40 },
    };

    [Fact]
    public void TryCreate_RefusesMissingMarks()
    {
        Assert.False(SessionAttachmentPageMap.TryCreate(null, out SessionAttachmentPageMap? map));

        Assert.Null(map);
    }

    [Fact]
    public void TryCreate_RefusesAnEmptyList()
    {
        Assert.False(SessionAttachmentPageMap.TryCreate([], out SessionAttachmentPageMap? map));

        Assert.Null(map);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void TryCreate_RefusesMarksThatDoNotOpenTheText(int firstOffset)
    {
        Assert.False(SessionAttachmentPageMap.TryCreate([new SessionAttachmentPageMark(firstOffset, 1)], out SessionAttachmentPageMap? map));

        Assert.Null(map);
    }

    [Fact]
    public void TryCreate_AcceptsMarksThatOpenTheText()
    {
        Assert.True(SessionAttachmentPageMap.TryCreate(Marks, out SessionAttachmentPageMap? map));

        Assert.NotNull(map);
    }

    [Theory]
    [MemberData(nameof(ChunkEdges))]
    public void WithPages_GivesTheLastMarkAtOrBeforeEachEdgeAndKeepsEverythingElse(
        int start,
        int end,
        int expectedStartPage,
        int expectedEndPage)
    {
        Assert.True(SessionAttachmentPageMap.TryCreate(Marks, out SessionAttachmentPageMap? map));

        SessionAttachmentTextChunk chunk = new(7, start, end, 80, 90, "chunk text");

        SessionAttachmentTextChunk paged = map.WithPages(chunk);

        Assert.Equal(chunk with { StartLine = expectedStartPage, EndLine = expectedEndPage }, paged);
    }

    [Fact]
    public void WithPages_OnASingleMarkGivesItsPageEverywhere()
    {
        Assert.True(SessionAttachmentPageMap.TryCreate([new SessionAttachmentPageMark(0, 6)], out SessionAttachmentPageMap? map));

        SessionAttachmentTextChunk paged = map.WithPages(new SessionAttachmentTextChunk(0, 0, 5000, 1, 99, "text"));

        Assert.Equal((6, 6), (paged.StartLine, paged.EndLine));
    }

    [Fact]
    public void WithPages_FindsEveryOffsetOfALongRunOfPages()
    {
        // Seven hundred pages of three characters each: every offset resolves to exactly its own page.
        SessionAttachmentPageMark[] marks = [.. Enumerable.Range(0, 700).Select(static index => new SessionAttachmentPageMark(index * 3, index + 1))];

        Assert.True(SessionAttachmentPageMap.TryCreate(marks, out SessionAttachmentPageMap? map));

        for (int offset = 0; offset < 2100; offset++)
        {
            SessionAttachmentTextChunk paged = map.WithPages(new SessionAttachmentTextChunk(0, offset, offset + 1, 0, 0, "x"));

            Assert.Equal(offset / 3 + 1, paged.StartLine);

            Assert.Equal(offset / 3 + 1, paged.EndLine);
        }
    }
}
