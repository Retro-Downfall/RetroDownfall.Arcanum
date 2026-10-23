using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension.Pdf;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension.Pdf;

/// <summary>
/// The ceiling counts UTF-16 characters of the body the worker frame carries: the page texts plus one U+000C between
/// each pair of pages. <c>multi-page.pdf</c> is 14 + 1 + 0 + 1 + 23 + 1 + 15 = 55 characters.
/// </summary>
public sealed class PdfTextExtractorOutputCeilingTests
{
    private static PdfExtractionOutcome Extract(string fileName, int ceiling) =>
        new PdfTextExtractor().Extract(
            PdfFixtures.Read(fileName),
            new PdfExtractionLimits(2000, 100_000, 250_000, 2_000_000, ceiling),
            CancellationToken.None);

    [Fact]
    public void Extract_OutputThatExactlyFillsTheCeiling_IsNotTruncated()
    {
        PdfExtractionOutcome outcome = Extract("multi-page.pdf", 55);

        Assert.Equal(PdfExtractionKind.Extracted, outcome.Kind);

        Assert.False(outcome.Truncated);

        Assert.Equal(["PAGE-ONE-ALPHA", string.Empty, "PAGE-THREE-MARKER-GAMMA", "PAGE-FOUR-DELTA"], outcome.Pages);
    }

    [Fact]
    public void Extract_CeilingOneShortOfTheOutput_CutsTheLastPage()
    {
        PdfExtractionOutcome outcome = Extract("multi-page.pdf", 54);

        Assert.Equal(PdfExtractionKind.Extracted, outcome.Kind);

        Assert.True(outcome.Truncated);

        Assert.Equal(["PAGE-ONE-ALPHA", string.Empty, "PAGE-THREE-MARKER-GAMMA", "PAGE-FOUR-DELT"], outcome.Pages);
    }

    [Fact]
    public void Extract_CeilingInsideAPage_KeepsTheEarlierPagesAndAPrefixOfThatPage()
    {
        // 14 + 1 + 0 + 1 leaves 4 characters for page 3.
        PdfExtractionOutcome outcome = Extract("multi-page.pdf", 20);

        Assert.True(outcome.Truncated);

        Assert.Equal(["PAGE-ONE-ALPHA", string.Empty, "PAGE"], outcome.Pages);
    }

    [Fact]
    public void Extract_CeilingThatEndsExactlyAfterAPage_StopsBeforeTheNextSeparator()
    {
        PdfExtractionOutcome outcome = Extract("multi-page.pdf", 14);

        Assert.True(outcome.Truncated);

        Assert.Equal(["PAGE-ONE-ALPHA"], outcome.Pages);
    }

    [Fact]
    public void Extract_CeilingThatLeavesRoomForAnEmptyPageButNotForText_StopsBeforeThePageThatHasText()
    {
        // 14 + 1 + 0 = 15 reaches the end of the empty page 2. Page 3 would need a separator and a character.
        PdfExtractionOutcome outcome = Extract("multi-page.pdf", 15);

        Assert.True(outcome.Truncated);

        Assert.Equal(["PAGE-ONE-ALPHA", string.Empty], outcome.Pages);
    }

    [Fact]
    public void Extract_CeilingThatLeavesRoomForTheSeparatorButNotForACharacter_DoesNotAddAnEmptyEntryForThatPage()
    {
        // 14 + 1 + 0 + 1 = 16: the separator before page 3 fits, a character of page 3 does not.
        PdfExtractionOutcome outcome = Extract("multi-page.pdf", 16);

        Assert.True(outcome.Truncated);

        Assert.Equal(["PAGE-ONE-ALPHA", string.Empty], outcome.Pages);
    }

    [Theory]
    [InlineData(6, "AB\U0001F600CD", false)]
    [InlineData(5, "AB\U0001F600C", true)]
    [InlineData(4, "AB\U0001F600", true)]
    [InlineData(3, "AB", true)]
    [InlineData(2, "AB", true)]
    [InlineData(1, "A", true)]
    public void Extract_CeilingThroughASurrogatePair_NeverLeavesHalfOfIt(int ceiling, string expected, bool truncated)
    {
        // "AB" + U+1F600 (two UTF-16 units) + "CD". Ceiling 3 falls between the two halves of the pair.
        PdfExtractionOutcome outcome = Extract("astral-plane.pdf", ceiling);

        Assert.Equal(PdfExtractionKind.Extracted, outcome.Kind);

        Assert.Equal(truncated, outcome.Truncated);

        Assert.Equal([expected], outcome.Pages);
    }

    [Fact]
    public void Extract_CeilingThatCutsAPairOnTheFirstPage_StopsThereAndNeverReadsTheNextPage()
    {
        // "AB" + U+1F600 + "CD" (5 glyphs), "EFGHIJKL" (8 glyphs), an empty page. A cut at 3 leaves "AB", so room remains for
        // the next separator; the extractor must still stop at the cut. A bound of 7 glyphs per page would refuse page 2
        // if it were read, so reading it shows up as Bounds.
        PdfExtractionOutcome outcome = new PdfTextExtractor().Extract(
            PdfFixtures.Read("astral-multi-page.pdf"),
            new PdfExtractionLimits(2000, 100_000, 7, 2_000_000, 3),
            CancellationToken.None);

        Assert.Equal(PdfExtractionKind.Extracted, outcome.Kind);

        Assert.True(outcome.Truncated);

        Assert.Equal(["AB"], outcome.Pages);
    }

    [Fact]
    public void Extract_CeilingThatFitsAllThreePagesOfTheAstralDocument_IsNotTruncated()
    {
        // 6 + 1 + 8 + 1 + 0 = 16.
        PdfExtractionOutcome outcome = Extract("astral-multi-page.pdf", 16);

        Assert.False(outcome.Truncated);

        Assert.Equal(["AB\U0001F600CD", "EFGHIJKL", string.Empty], outcome.Pages);
    }

    [Fact]
    public void Extract_CeilingReachedBeforeAnyTextWasProduced_IsBoundsNotNoText()
    {
        // Five spaces under a ceiling of three: the document does have more than it may return, but nothing readable.
        PdfExtractionOutcome outcome = Extract("whitespace-only.pdf", 3);

        Assert.Equal(PdfExtractionKind.Bounds, outcome.Kind);

        Assert.Empty(outcome.Pages);

        Assert.False(outcome.Truncated);
    }

    [Fact]
    public void Extract_WhitespaceThatFitsTheCeiling_IsNoText()
    {
        PdfExtractionOutcome outcome = Extract("whitespace-only.pdf", 5);

        Assert.Equal(PdfExtractionKind.NoText, outcome.Kind);

        Assert.False(outcome.Truncated);
    }
}
