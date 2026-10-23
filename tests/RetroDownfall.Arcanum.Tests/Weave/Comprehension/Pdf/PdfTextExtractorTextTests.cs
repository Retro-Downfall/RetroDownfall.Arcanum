using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension.Pdf;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension.Pdf;

public sealed class PdfTextExtractorTextTests
{
    private static PdfExtractionOutcome Extract(string fileName, PdfExtractionLimits? limits = null) =>
        new PdfTextExtractor().Extract(PdfFixtures.Read(fileName), limits ?? PdfExtractionLimits.Default, CancellationToken.None);

    [Fact]
    public void Extract_RealProducerDocument_KeepsEveryCharacterInOrder()
    {
        // cupsfilter pads its lines and hard-wraps them inside words, so layout differs and whitespace cannot be compared;
        // the characters and their order must not. Word boundaries are pinned by the same-line tests below.
        PdfExtractionOutcome outcome = Extract("cups-text.pdf");

        Assert.Equal(PdfExtractionKind.Extracted, outcome.Kind);

        Assert.False(outcome.Truncated);

        string page = Assert.Single(outcome.Pages);

        Assert.Equal(PdfFixtures.WithoutWhitespace(PdfFixtures.ReadText("cups-text.txt")), PdfFixtures.WithoutWhitespace(page));
    }

    [Fact]
    public void Extract_RealProducerDocumentOfTenPages_KeepsPagesApartAndInOrder()
    {
        PdfExtractionOutcome outcome = Extract("cups-multi.pdf");

        Assert.Equal(PdfExtractionKind.Extracted, outcome.Kind);

        Assert.Equal(10, outcome.Pages.Count);

        Assert.Equal(
            PdfFixtures.WithoutWhitespace(PdfFixtures.ReadText("cups-multi.txt")),
            PdfFixtures.WithoutWhitespace(string.Concat(outcome.Pages)));

        Assert.Contains("MARKER-001", PdfFixtures.WithoutWhitespace(outcome.Pages[0]));

        Assert.DoesNotContain("LASTLINE-OBSIDIAN-9915", PdfFixtures.WithoutWhitespace(string.Concat(outcome.Pages.Take(9))));

        Assert.Contains("LASTLINE-OBSIDIAN-9915", outcome.Pages[9]);
    }

    [Fact]
    public void Extract_MultiPageDocument_KeepsOneEntryPerPageIncludingEmptyOnes()
    {
        // Page 2 has no content at all. Its empty entry is what keeps "page 3" true for the marker after it.
        PdfExtractionOutcome outcome = Extract("multi-page.pdf");

        Assert.Equal(PdfExtractionKind.Extracted, outcome.Kind);

        Assert.False(outcome.Truncated);

        Assert.Equal(["PAGE-ONE-ALPHA", string.Empty, "PAGE-THREE-MARKER-GAMMA", "PAGE-FOUR-DELTA"], outcome.Pages);
    }

    [Fact]
    public void Extract_TextShownOutOfVisualOrder_IsReturnedInContentStreamOrder()
    {
        // Sensitivity: ContentOrderTextExtractor. A position-sorted or neighbour-grouped extractor would put the top run first.
        PdfExtractionOutcome outcome = Extract("content-order.pdf");

        Assert.Equal(PdfExtractionKind.Extracted, outcome.Kind);

        Assert.Equal(["FIRST-IN-STREAM-BOTTOM\nSECOND-IN-STREAM-TOP\nTHIRD-IN-STREAM-MIDDLE"], outcome.Pages);
    }

    [Fact]
    public void Extract_RunsOnOneLineDrawnRightToLeftOrOnTopOfEachOther_StayApartAsWords()
    {
        // Sensitivity: the separator rule for a backward jump. A right run drawn before the left run it sits beside, a page
        // number drawn before its title and a run drawn on top of another are all on one baseline with the next run starting
        // left of the previous one's end; gluing them makes one token of two words. The kerned pair is the control that
        // keeps a small backward step inside a word.
        PdfExtractionOutcome outcome = Extract("same-line-order.pdf");

        Assert.Equal(PdfExtractionKind.Extracted, outcome.Kind);

        Assert.Equal(["RIGHTWORD LEFTWORD\n17 Annual Report\nWAVE\nFIRSTRUN SECONDRUN"], outcome.Pages);
    }

    [Fact]
    public void Extract_RealProducerFloatAndRightToLeftRows_KeepTheirRunsApart()
    {
        // WebKit paints a float before the inline text it sits beside, a right-to-left table row from the right cell to the
        // left one and a row-reverse flex row from its last item to its first. Glued, the price and the quantity after it
        // read as one number.
        PdfExtractionOutcome outcome = Extract("webkit-float.pdf");

        Assert.Equal(PdfExtractionKind.Extracted, outcome.Kind);

        Assert.Equal(["$1,250.00 2 hours consulting\nRIGHTCELL LEFTCELL\nFLEXFIRST FLEXSECOND"], outcome.Pages);
    }

    [Theory]
    [InlineData("astral-plane.pdf", "AB\U0001F600CD")]
    [InlineData("cjk-tounicode.pdf", "日本語\n漢字\n\U00020BB7野家")]
    [InlineData("cjk-predefined-cmap.pdf", "あいう")]
    public void Extract_NonAsciiText_IsExact(string fileName, string expected)
    {
        PdfExtractionOutcome outcome = Extract(fileName);

        Assert.Equal(PdfExtractionKind.Extracted, outcome.Kind);

        Assert.Equal([expected], outcome.Pages);
    }

    [Fact]
    public void Extract_FontThatIsNotEmbeddedAndInstalledNowhere_IsStillExtracted()
    {
        // The text comes from the file, not from a font found on the host, so the result cannot depend on which fonts
        // the worker's sandbox can read.
        PdfExtractionOutcome outcome = Extract("non-embedded-font.pdf");

        Assert.Equal(PdfExtractionKind.Extracted, outcome.Kind);

        Assert.Equal(["NON-EMBEDDED-FONT-TEXT"], outcome.Pages);
    }

    [Fact]
    public void Extract_PageTreeThatClaimsTwoBillionPages_ReadsOnlyThePagesThatExist()
    {
        PdfExtractionOutcome outcome = Extract("huge-count.pdf");

        Assert.Equal(PdfExtractionKind.Extracted, outcome.Kind);

        Assert.Equal(["REAL-PAGE-ONE"], outcome.Pages);
    }

    [Fact]
    public void Extract_PageBuriedInAThousandLevelPageTree_IsFound()
    {
        PdfExtractionOutcome outcome = Extract("deep-pages-1k.pdf");

        Assert.Equal(PdfExtractionKind.Extracted, outcome.Kind);

        Assert.Equal(["DEEP-PAGE-LEAF"], outcome.Pages);
    }
}
