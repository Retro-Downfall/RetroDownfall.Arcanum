using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension.Pdf;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension.Pdf;

public sealed class PdfTextExtractorTextTests
{
    // Logical order, as a person types them; the fixtures draw them reversed (visual order) or as written (logical order).
    private const string Shalom = "\u05E9\u05DC\u05D5\u05DD";

    private const string Olam = "\u05E2\u05D5\u05DC\u05DD";

    private const string Mechir = "\u05DE\u05D7\u05D9\u05E8";

    private const string Shekel = "\u05E9\u05E7\u05DC";

    private const string Marhaba = "\u0645\u0631\u062D\u0628\u0627";

    private const string Bilalam = "\u0628\u0627\u0644\u0639\u0627\u0644\u0645";

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
        // left of the previous one; gluing them makes one token of two words. The kerned pair and the accent drawn over the
        // letter that follows it (the way TeX writes an umlaut) are the controls that keep a small backward step inside a
        // word: they step back past the previous glyph's end, but not past its start.
        PdfExtractionOutcome outcome = Extract("same-line-order.pdf");

        Assert.Equal(PdfExtractionKind.Extracted, outcome.Kind);

        Assert.Equal(["RIGHTWORD LEFTWORD\n17 Annual Report\nWAVE\nFIRSTRUN SECONDRUN\nM^uller"], outcome.Pages);
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

    [Fact]
    public void Extract_LatinLigatureCodePoints_BecomeTheirLetters()
    {
        // Sensitivity: the ligature expansion. U+FB05 is a long s and a t, as NFKC decomposes it. Nothing else is folded:
        // the plain line is the control.
        PdfExtractionOutcome outcome = Extract("latin-ligatures.pdf");

        Assert.Equal(PdfExtractionKind.Extracted, outcome.Kind);

        Assert.Equal(["effect final flow\noffice baffle\n\u017Ft st\nplain office"], outcome.Pages);
    }

    [Fact]
    public void Extract_RealProducerLigatureGlyphs_ReadAsPlainWords()
    {
        // WebKit writes Helvetica's fi, fl and ffi as ligature glyphs, which PdfPig maps to U+FB01, U+FB02 and U+FB03. The
        // text must be what pdftotext gives and what a person would search for.
        PdfExtractionOutcome outcome = Extract("webkit-ligatures.pdf");

        Assert.Equal(PdfExtractionKind.Extracted, outcome.Kind);

        Assert.Equal(["Helvetica: office, figures, affidavit, flow, fluffy waffles."], outcome.Pages);
    }

    [Fact]
    public void Extract_RightToLeftTextWrittenInVisualOrder_IsReturnedInLogicalOrder()
    {
        // Sensitivity: the reversal of a right-to-left run whose glyphs advance left to right. The sentence keeps its full stop
        // at the end, the English sentence keeps its Hebrew word whole and in place, the number keeps its digit order, the
        // Latin run keeps its letters, and a line with no right-to-left text is untouched.
        PdfExtractionOutcome outcome = Extract("rtl-visual-order.pdf");

        Assert.Equal(PdfExtractionKind.Extracted, outcome.Kind);

        Assert.Equal(
            [
                $"{Shalom} {Olam}.\nThe word {Shalom} means peace.\n{Mechir} 25.5 {Shekel}\n{Olam} abc {Shalom}\n{Marhaba} {Bilalam}\nPlain ASCII line 123",
            ],
            outcome.Pages);
    }

    [Fact]
    public void Extract_RightToLeftTextWrittenInLogicalOrder_IsLeftAlone()
    {
        // Sensitivity: the direction test. Glyphs placed to the left of the one before are already in logical order: they
        // must not be reversed, and the leftward step from one glyph to the next must not read as a gap between words.
        PdfExtractionOutcome outcome = Extract("rtl-logical-order.pdf");

        Assert.Equal(PdfExtractionKind.Extracted, outcome.Kind);

        Assert.Equal([$"{Shalom} {Olam}\n{Marhaba} {Bilalam}"], outcome.Pages);
    }

    [Fact]
    public void Extract_RealProducerHebrew_ReadsInLogicalOrder()
    {
        // Quartz writes the right-to-left paragraph in visual order, as every macOS producer does. The first line starts
        // with a space the producer draws for the paragraph break.
        PdfExtractionOutcome outcome = Extract("quartz-hebrew.pdf");

        Assert.Equal(PdfExtractionKind.Extracted, outcome.Kind);

        string page = Assert.Single(outcome.Pages);

        Assert.Equal([$"HEBREW-MARKER {Shalom} {Olam}", $"The word {Shalom} means peace."], page.Split('\n').Select(line => line.Trim()));
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
