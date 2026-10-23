using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension.Pdf;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension.Pdf;

/// <summary>
/// Uses <c>multi-page.pdf</c>: four pages that show 14, 0, 23 and 15 glyphs (52 in all) and whose body is 55 characters
/// once joined by U+000C, and 10 declared objects. Production-sized bounds are exercised at the worker level, not here.
/// </summary>
public sealed class PdfTextExtractorBoundsTests
{
    private static PdfExtractionOutcome Extract(string fileName, PdfExtractionLimits limits) =>
        new PdfTextExtractor().Extract(PdfFixtures.Read(fileName), limits, CancellationToken.None);

    private static PdfExtractionLimits Limits(int pages = 2000, int objects = 100_000, int perPage = 250_000, int perDocument = 2_000_000, int output = 262_144) =>
        new(pages, objects, perPage, perDocument, output);

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

    // ---------------------------------------------------------------- objects

    [Fact]
    public void Extract_MoreDeclaredObjectsThanTheBound_IsBounds()
    {
        // multi-page.pdf declares 10 objects. Sensitivity: the comparison on the declared object count.
        AssertBounds(Extract("multi-page.pdf", Limits(objects: 9)));
    }

    [Fact]
    public void Extract_ExactlyTheBoundedNumberOfDeclaredObjects_IsExtracted()
    {
        PdfExtractionOutcome outcome = Extract("multi-page.pdf", Limits(objects: 10));

        Assert.Equal(PdfExtractionKind.Extracted, outcome.Kind);

        Assert.Equal(4, outcome.Pages.Count);
    }

    [Fact]
    public void Extract_DocumentOverTheObjectBound_IsBoundsWithoutReadingAPage()
    {
        // cyclic-ref-chain.pdf declares 6 objects and its only page cannot be read (the next test shows the failure). Over the
        // bound the page is never touched, so the outcome is Bounds, not Malformed. Sensitivity: the check precedes the first page.
        AssertBounds(Extract("cyclic-ref-chain.pdf", Limits(objects: 5)));
    }

    [Fact]
    public void Extract_TheSameDocumentAtTheObjectBound_ReadsItsPageAndFindsItMalformed()
    {
        Assert.Equal(PdfExtractionKind.Malformed, Extract("cyclic-ref-chain.pdf", Limits(objects: 6)).Kind);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(6)]
    public void Extract_ObjectsStoredInAnObjectStream_CountTowardTheBound(int objects)
    {
        // xref-stream.pdf declares 7 objects: three written at top level (the content, the object stream and the cross-reference
        // stream) and four (catalog, page tree, page, font) that exist only inside the object stream, as type 2 entries. A bound
        // of 3 lets a count of the top-level ones through. Sensitivity: whether compressed entries are counted.
        AssertBounds(Extract("xref-stream.pdf", Limits(objects: objects)));
    }

    [Fact]
    public void Extract_ObjectStreamDocumentAtTheObjectBound_IsExtracted()
    {
        // The trailer says /Size 8 (entry 0 is free). Sensitivity: the count is the entries in use, not /Size.
        PdfExtractionOutcome outcome = Extract("xref-stream.pdf", Limits(objects: 7));

        Assert.Equal(PdfExtractionKind.Extracted, outcome.Kind);

        Assert.Equal(["XREF-STREAM-TEXT"], outcome.Pages);
    }

    [Fact]
    public void Extract_FileThatDeclaresOneObjectMoreThanTheProductionBound_IsBounds()
    {
        // A kilobyte of file whose cross-reference stream lists 100,001 objects: the shape a small file takes to declare a
        // great many. Sensitivity: the production bound is the one the extractor is handed.
        AssertBounds(Extract("xref-stream-over-object-bound.pdf", PdfExtractionLimits.Default));
    }

    [Fact]
    public void Extract_FileThatDeclaresExactlyTheProductionBound_IsExtracted()
    {
        PdfExtractionOutcome outcome = Extract("xref-stream-at-object-bound.pdf", PdfExtractionLimits.Default);

        Assert.Equal(PdfExtractionKind.Extracted, outcome.Kind);

        Assert.Equal(["XREF-STREAM-TEXT"], outcome.Pages);
    }

    [Fact]
    public void Extract_TableThatListsFewerObjectsThanTheFileWrites_CountsOnlyWhatItLists()
    {
        // KNOWN LIMITATION, pinned so a change is noticed: xref-short-table.pdf writes five objects and its table lists two.
        // PdfPig finds the other three by scanning when it looks for them, and the bound does not count them: what a scan can
        // find is limited by the input ceiling (README.md). The trailer says /Size 6, which is not the count either.
        PdfExtractionOutcome outcome = Extract("xref-short-table.pdf", Limits(objects: 2));

        Assert.Equal(PdfExtractionKind.Extracted, outcome.Kind);

        Assert.Equal(["SHORT-TABLE-TEXT"], outcome.Pages);
    }

    [Fact]
    public void Extract_DocumentWithNoCrossReferenceTable_CountsNoObjects()
    {
        // KNOWN LIMITATION, pinned so a change is noticed: PdfPig builds no table from a scan, it holds an empty one and finds
        // all five objects of xref-no-table.pdf by scanning. Objects inside an object stream cannot be found that way.
        PdfExtractionOutcome outcome = Extract("xref-no-table.pdf", Limits(objects: 1));

        Assert.Equal(PdfExtractionKind.Extracted, outcome.Kind);

        Assert.Equal(["SHORT-TABLE-TEXT"], outcome.Pages);
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
