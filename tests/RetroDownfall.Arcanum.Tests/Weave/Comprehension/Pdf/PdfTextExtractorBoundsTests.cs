using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension.Pdf;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension.Pdf;

/// <summary>
/// Uses <c>multi-page.pdf</c>: four pages that show 14, 0, 23 and 15 glyphs (52 in all) and whose body is 55 characters
/// once joined by U+000C. Production-sized bounds are exercised at the worker level, not here.
/// </summary>
public sealed class PdfTextExtractorBoundsTests
{
    private static PdfExtractionOutcome Extract(string fileName, PdfExtractionLimits limits) =>
        new PdfTextExtractor().Extract(PdfFixtures.Read(fileName), limits, CancellationToken.None);

    private static PdfExtractionLimits Limits(int pages = 2000, int perPage = 250_000, int perDocument = 2_000_000, int output = 262_144) =>
        new(pages, perPage, perDocument, output);

    private static void AssertBounds(PdfExtractionOutcome outcome)
    {
        Assert.Equal(PdfExtractionKind.Bounds, outcome.Kind);

        Assert.Empty(outcome.Pages);

        Assert.False(outcome.Truncated);
    }

    // ---------------------------------------------------------------- pages

    [Fact]
    public void Extract_MorePagesThanTheBound_IsBounds()
    {
        AssertBounds(Extract("multi-page.pdf", Limits(pages: 3)));
    }

    [Fact]
    public void Extract_ExactlyTheBoundedNumberOfPages_IsExtracted()
    {
        PdfExtractionOutcome outcome = Extract("multi-page.pdf", Limits(pages: 4));

        Assert.Equal(PdfExtractionKind.Extracted, outcome.Kind);

        Assert.Equal(4, outcome.Pages.Count);
    }

    // ---------------------------------------------------------------- letters

    [Fact]
    public void Extract_PageThatShowsMoreGlyphsThanTheBound_IsBounds()
    {
        // Page 3 shows 23 glyphs. Sensitivity: the comparison on one page's letter count.
        AssertBounds(Extract("multi-page.pdf", Limits(perPage: 22)));
    }

    [Fact]
    public void Extract_PageThatShowsExactlyTheBoundedGlyphs_IsExtracted()
    {
        PdfExtractionOutcome outcome = Extract("multi-page.pdf", Limits(perPage: 23));

        Assert.Equal(PdfExtractionKind.Extracted, outcome.Kind);
    }

    [Fact]
    public void Extract_PagesThatTogetherShowMoreGlyphsThanTheBound_IsBounds()
    {
        // No page is over its own bound; the 52 glyphs together are over 51. Sensitivity: the running total.
        AssertBounds(Extract("multi-page.pdf", Limits(perDocument: 51)));
    }

    [Fact]
    public void Extract_PagesThatTogetherShowExactlyTheBoundedGlyphs_IsExtracted()
    {
        PdfExtractionOutcome outcome = Extract("multi-page.pdf", Limits(perDocument: 52));

        Assert.Equal(PdfExtractionKind.Extracted, outcome.Kind);

        Assert.Equal(4, outcome.Pages.Count);
    }

    [Fact]
    public void Extract_GlyphsThatAreOnlySpaces_CountTowardTheBound()
    {
        // The bound protects the parser's work, and a space is a glyph it had to build.
        AssertBounds(Extract("whitespace-only.pdf", Limits(perPage: 4)));
    }
}
