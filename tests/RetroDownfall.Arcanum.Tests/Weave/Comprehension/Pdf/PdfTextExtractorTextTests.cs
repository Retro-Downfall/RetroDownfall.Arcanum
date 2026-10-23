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

    // Numbers inside right-to-left lines, each line in logical order, as a person types it.
    private static readonly string RightToLeftNumbers = string.Join(
        '\n',
        "\u0627\u0644\u0633\u0639\u0631 \u0661\u0662\u066B\u0665 \u062F\u064A\u0646\u0627\u0631", // Arabic: price 12.5 (Arabic-Indic, U+066B) dinar
        "\u0642\u06CC\u0645\u062A \u06F1\u06F2\u066B\u06F5 \u062A\u0648\u0645\u0627\u0646", // Persian: price 12.5 (Extended Arabic-Indic) toman
        "\u0627\u0644\u0639\u062F\u062F \u0661\u066C\u0662\u0665\u0660 \u0643\u062A\u0627\u0628", // Arabic: count 1,250 (U+066C) books
        "\u0646\u0633\u0628\u0629 50% \u0641\u0642\u0637", // Arabic: rate 50% only
        "\u0646\u0633\u0628\u0629 \u0665\u0660% \u0641\u0642\u0637", // Arabic: rate 50% (Arabic-Indic) only
        "\u0646\u0633\u0628\u0629 \u0665\u0660\u066A \u0641\u0642\u0637", // Arabic: rate 50 and the Arabic percent sign only
        "\u0627\u0644\u0633\u0639\u0631 $25 \u0641\u0642\u0637", // Arabic: price $25 only
        "50% \u0641\u0642\u0637", // Arabic, the number first: 50% only
        "\u05D4\u05D8\u05DE\u05E4\u05E8\u05D8\u05D5\u05E8\u05D4 25\u00B0 \u05D4\u05D9\u05D5\u05DD", // Hebrew: temperature 25 degrees today
        "\u05D4\u05E0\u05D7\u05D4 50% \u05D4\u05D9\u05D5\u05DD", // Hebrew: discount 50% today
        "\u05DE\u05D7\u05D9\u05E8 $25 \u05D4\u05D9\u05D5\u05DD", // Hebrew: price $25 today
        "\u05EA\u05D0\u05E8\u05D9\u05DA 2026-10-01 \u05D4\u05D9\u05D5\u05DD"); // Hebrew: date 2026-10-01 today

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
    public void Extract_TextWhoseGlyphsDoNotAdvanceRightwards_StaysWords()
    {
        // Sensitivity: word breaks measured along each glyph's own advance direction. Upside-down, mirrored and 150-degree
        // text starts each glyph a glyph's width left of the one before; measured in page x, that step reads as a word break
        // and every letter comes out spaced. The kerned upside-down line has no space glyphs, so its words are apart only
        // along their own direction. The last two lines are the controls for a change of direction: an upside-down run with
        // an upright one to its left, and a run that starts off the baseline of the one before, are still two words.
        PdfExtractionOutcome outcome = Extract("rotated-text.pdf");

        Assert.Equal(PdfExtractionKind.Extracted, outcome.Kind);

        Assert.Equal(
            ["Ordinary line of text\nInvoice total due\nMirrored words here\nRotated one fifty\nSpaced by kerning\nFlipped Upright\nUp the side Turned"],
            outcome.Pages);
    }

    [Fact]
    public void Extract_LineFeedGlyphAboveALineOfLetters_DoesNotStartTheLineWithASpace()
    {
        // Sensitivity: only a visible letter has a baseline the next letter can start off. Some producers draw a
        // paragraph's line feed as a glyph (macOS license documents do, at the top of a page); the line below it starts no
        // new word.
        PdfExtractionOutcome outcome = Extract("line-feed-glyph.pdf");

        Assert.Equal(PdfExtractionKind.Extracted, outcome.Kind);

        Assert.Equal(["\nAB"], outcome.Pages);
    }

    [Fact]
    public void Extract_RealProducerRotatedAndMirroredText_StaysWords()
    {
        // WebKit draws CSS rotate(180deg), rotate(150deg) and scaleX(-1) text with a turned text matrix.
        PdfExtractionOutcome outcome = Extract("webkit-rotated.pdf");

        Assert.Equal(PdfExtractionKind.Extracted, outcome.Kind);

        Assert.Equal(["Normal line here\nUpside down label text\nRotated one fifty\nMirrored words here"], outcome.Pages);
    }

    [Fact]
    public void Extract_LatinLigatureCodePoints_BecomeTheirLetters()
    {
        // Sensitivity: the ligature expansion. U+FB05 becomes "st", which is what NFKC gives: its compatibility decomposition
        // is a long s and a t, and the long s decomposes again to s. Nothing else is folded: the plain line is the control.
        PdfExtractionOutcome outcome = Extract("latin-ligatures.pdf");

        Assert.Equal(PdfExtractionKind.Extracted, outcome.Kind);

        Assert.Equal(["effect final flow\noffice baffle\nst st\nplain office"], outcome.Pages);
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

    [Theory]
    [InlineData("rtl-numbers.pdf")]
    [InlineData("webkit-rtl-numbers.pdf")]
    public void Extract_NumbersInRightToLeftLines_KeepTheirDigitsSeparatorsAndSignsInLogicalOrder(string fileName)
    {
        // Sensitivity: what a reversed run keeps together as one number, which the Unicode bidirectional algorithm decides
        // (UAX #9 W2, W4, W5). The Arabic decimal and thousands separators sit inside the number; after a Hebrew letter, or
        // at the start of the line, a sign next to European digits is drawn inside the number too; after an Arabic letter
        // the digits are Arabic numbers, so a percent or dollar sign is drawn outside them and must come back where it was
        // typed. The first file is hand-written in that visual order, the second is WebKit's.
        PdfExtractionOutcome outcome = Extract(fileName);

        Assert.Equal(PdfExtractionKind.Extracted, outcome.Kind);

        Assert.Equal([RightToLeftNumbers], outcome.Pages);
    }

    [Fact]
    public void Extract_RightToLeftTextDrawnUpsideDown_IsJudgedAlongItsOwnDirection()
    {
        // Sensitivity: the direction test and the facing-edge gap measured along the glyphs' own direction. The first line is
        // in visual order (each glyph a glyph's width further along its leftward direction), the second in logical order
        // (each glyph behind the one before); both read the same, and neither is letter-spaced.
        PdfExtractionOutcome outcome = Extract("rtl-upside-down.pdf");

        Assert.Equal(PdfExtractionKind.Extracted, outcome.Kind);

        Assert.Equal([$"{Shalom} {Olam}\n{Shalom} {Olam}"], outcome.Pages);
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
