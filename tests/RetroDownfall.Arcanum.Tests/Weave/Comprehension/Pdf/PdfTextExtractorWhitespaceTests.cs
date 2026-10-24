using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension.Pdf;

using UglyToad.PdfPig;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension.Pdf;

/// <summary>
/// The page text is returned without layout: cupsfilter lays its text out on a fixed character grid and draws a space glyph
/// in every cell of it, so the join returned the padding after each line (runs of 70 to 130 spaces in the fixtures) and the
/// blank rows at the foot of a page (one run of 3,897 in <c>cups-text.pdf</c>) as text. Every run of horizontal whitespace
/// becomes one space, each line is trimmed, and a line with nothing on it is dropped, after the unsafe characters are
/// removed and before the output ceiling is counted.
/// </summary>
public sealed class PdfTextExtractorWhitespaceTests
{
    // Logical order, as a person types them.
    private const string Shalom = "\u05E9\u05DC\u05D5\u05DD";

    private const string Olam = "\u05E2\u05D5\u05DC\u05DD";

    private const string Mechir = "\u05DE\u05D7\u05D9\u05E8";

    private const string Marhaba = "\u0645\u0631\u062D\u0628\u0627";

    private static DocumentExtractionOutcome Extract(string fileName, int? ceiling = null) =>
        new PdfTextExtractor().Extract(
            PdfFixtures.Read(fileName),
            ceiling is null ? PdfExtractionLimits.Default : new PdfExtractionLimits(2000, 100_000, 250_000, 2_000_000, ceiling.Value),
            CancellationToken.None);

    /// <summary>The page texts the join produces before the whitespace is normalized: what the extractor returned before this rule.</summary>
    private static string[] UnnormalizedPages(string fileName)
    {
        using PdfDocument document = PdfDocument.Open(
            PdfFixtures.Read(fileName),
            new ParsingOptions { UseLenientParsing = false, SkipMissingFonts = true, UseActualText = false, ClipPaths = false });

        return [.. document.GetPages().Select(page => PdfTextExtractor.RemoveUnsafeCharacters(PdfPageText.Build(page.Letters)))];
    }

    private static void AssertNormalized(string page)
    {
        Assert.All(page, character => Assert.True(!char.IsWhiteSpace(character) || character is ' ' or '\n', $"U+{(int)character:X4} is whitespace that is neither a space nor a line break"));

        if (page.Length == 0)
        {
            return;
        }

        foreach (string line in page.Split('\n'))
        {
            Assert.False(line.Length == 0, "a line is empty");

            Assert.False(string.IsNullOrWhiteSpace(line), "a line holds only whitespace");

            Assert.False(char.IsWhiteSpace(line[0]) || char.IsWhiteSpace(line[^1]), $"a line is not trimmed: '{line}'");

            Assert.DoesNotContain("  ", line, StringComparison.Ordinal);
        }
    }

    // ---------------------------------------------------------------- the normalization on text

    [Theory]
    [InlineData("a b c", "a b c")]
    [InlineData("a  b", "a b")]
    [InlineData("a                                                            b", "a b")]
    [InlineData("a\t\tb", "a b")]
    [InlineData("a\u00A0\u00A0b", "a b")]
    [InlineData("a \t\u00A0\u2003\u3000\u202F b", "a b")]
    [InlineData("  lead and trail  ", "lead and trail")]
    [InlineData("\t\u00A0lead", "lead")]
    [InlineData("one  \n  two", "one\ntwo")]
    [InlineData("a\n\n\nb", "a\nb")]
    [InlineData("a\n   \n\t\n\u00A0\nb", "a\nb")]
    [InlineData("\n\nAB", "AB")]
    [InlineData("A\n\n  \n", "A")]
    [InlineData("a\r\nb\rc\u2028d\u2029e\u0085f\vg\fh", "a\nb\nc\nd\ne\nf\ng\nh")]
    [InlineData("", "")]
    [InlineData("     ", "")]
    [InlineData("\n \n\t\n", "")]
    [InlineData("\u00A0\u2003\n\u3000", "")]
    [InlineData("\U0001F600  \U0001F600\n \n\U0001F600", "\U0001F600 \U0001F600\n\U0001F600")]
    [InlineData("a \u200B  \uFEFF b", "a \u200B \uFEFF b")]
    public void NormalizeWhitespace_PlainText_CollapsesRunsTrimsLinesAndDropsBlankOnes(string text, string expected)
    {
        // The zero-width space and the byte order mark are not whitespace to this rule (nor to the visible-character test
        // that decides NoText), so they stay and keep the spaces beside them apart.
        Assert.Equal(expected, PdfTextExtractor.NormalizeWhitespace(text));
    }

    [Theory]
    [InlineData(Shalom + " \t " + Olam + "   \n  \n" + Mechir + "  25.5 ", Shalom + " " + Olam + "\n" + Mechir + " 25.5")]
    [InlineData(Marhaba + "  \u0661\u0662\u066B\u0665\u00A0\u00A0" + Olam, Marhaba + " \u0661\u0662\u066B\u0665 " + Olam)]
    [InlineData("abc  " + Shalom + "   def\t", "abc " + Shalom + " def")]
    [InlineData("  " + Shalom + "\u00A0\u00A0" + Olam + "\n\t\n  " + Marhaba + "  ", Shalom + " " + Olam + "\n" + Marhaba)]
    public void NormalizeWhitespace_RightToLeftText_KeepsEveryCharacterWhereItWas(string text, string expected)
    {
        // The rule runs on the finished page text, so the order the join gave the characters (the reversal of visual-order
        // runs, the numbers kept whole) is never touched: only whitespace is removed, never moved.
        Assert.Equal(expected, PdfTextExtractor.NormalizeWhitespace(text));
    }

    [Theory]
    [InlineData("a  b\t\tc\n\n  d  \n")]
    [InlineData("\u00A0\n" + Shalom + "   " + Olam + "\n\n\u2003" + Marhaba)]
    [InlineData("\U0001F600  \U0001F600\n\n\t\U0001F600")]
    [InlineData("    \n\t\n")]
    [InlineData("already clean\ntext")]
    public void NormalizeWhitespace_AnyText_KeepsTheVisibleCharactersInOrderAndIsIdempotent(string text)
    {
        string normalized = PdfTextExtractor.NormalizeWhitespace(text);

        Assert.Equal(PdfFixtures.WithoutWhitespace(text), PdfFixtures.WithoutWhitespace(normalized));

        AssertNormalized(normalized);

        Assert.Equal(normalized, PdfTextExtractor.NormalizeWhitespace(normalized));
    }

    // ---------------------------------------------------------------- real producer: cupsfilter's character grid

    [Theory]
    [InlineData("cups-text.pdf")]
    [InlineData("cups-multi.pdf")]
    public void Extract_CupsfilterCharacterGrid_ReturnsNoPaddingNoBlankLinesAndTheSameCharacters(string fileName)
    {
        // Sensitivity: the normalization. cupsfilter draws a space glyph in every cell of its 80-column grid, so before the
        // rule each line ended in a run of spaces and the blank rows at the foot of a page were one run of up to 3,897.
        string[] before = UnnormalizedPages(fileName);

        DocumentExtractionOutcome outcome = Extract(fileName);

        Assert.Equal(DocumentExtractionKind.Extracted, outcome.Kind);

        Assert.False(outcome.Truncated);

        Assert.Equal(before.Length, outcome.Pages.Count);

        Assert.Contains(before, page => page.Contains("                    ", StringComparison.Ordinal));

        foreach (string page in outcome.Pages)
        {
            AssertNormalized(page);
        }

        for (int index = 0; index < before.Length; index++)
        {
            Assert.Equal(PdfFixtures.WithoutWhitespace(before[index]), PdfFixtures.WithoutWhitespace(outcome.Pages[index]));
        }

        Assert.Equal(
            PdfFixtures.WithoutWhitespace(PdfFixtures.ReadText(fileName.Replace(".pdf", ".txt", StringComparison.Ordinal))),
            PdfFixtures.WithoutWhitespace(string.Concat(outcome.Pages)));
    }

    [Fact]
    public void Extract_CupsfilterPage_KeepsTheWordsApartAndTheLinesInOrder()
    {
        // The words are the spaces the producer drew between them; only the padding after the last word goes. The page
        // wraps inside words at 80 columns, which the rule does not repair.
        DocumentExtractionOutcome outcome = Extract("cups-text.pdf");

        string page = Assert.Single(outcome.Pages);

        Assert.StartsWith("Arcanum PdfPig probe document\nThe quick brown fox jumps over the lazy dog. This paragraph exists so the extrac\ntor\nhas ordinary", page, StringComparison.Ordinal);

        Assert.EndsWith("a URL https:\n//example.com/path?q=1.", page, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- the ceiling counts real text

    [Fact]
    public void Extract_PaddedDocumentThatExceededTheCeilingByItsPadding_NowFits()
    {
        // Sensitivity: the order of the steps. The ten pages are 48,142 characters with their padding and 26,392 without it.
        // Counted before the padding went, they were cut 50 characters into the seventh page, short of the codeword on the tenth.
        DocumentExtractionOutcome outcome = Extract("cups-multi.pdf", 30_000);

        Assert.Equal(DocumentExtractionKind.Extracted, outcome.Kind);

        Assert.False(outcome.Truncated);

        Assert.Equal(10, outcome.Pages.Count);

        Assert.Contains("LASTLINE-OBSIDIAN-9915", outcome.Pages[9], StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_CeilingInsideAPaddedPage_HoldsOnlyRealText()
    {
        // 2,000 characters of the first page are 57 percent whitespace without the rule (padding) and about 20 percent with it
        // (the spaces between words and the line breaks).
        DocumentExtractionOutcome outcome = Extract("cups-multi.pdf", 2_000);

        Assert.True(outcome.Truncated);

        string page = Assert.Single(outcome.Pages);

        Assert.True(page.Length <= 2_000);

        Assert.True(PdfFixtures.WithoutWhitespace(page).Length >= 1_500, $"{PdfFixtures.WithoutWhitespace(page).Length} visible characters");
    }

    [Fact]
    public void Extract_PaddedPageUnderTheCeiling_IsNotTruncated()
    {
        // The page is 4,803 characters with its padding and 452 without.
        DocumentExtractionOutcome outcome = Extract("cups-text.pdf", 500);

        Assert.False(outcome.Truncated);

        Assert.EndsWith("//example.com/path?q=1.", Assert.Single(outcome.Pages), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(8, "Arcanum")]
    [InlineData(29, "Arcanum PdfPig probe document")]
    [InlineData(30, "Arcanum PdfPig probe document")]
    [InlineData(31, "Arcanum PdfPig probe document\nT")]
    public void Extract_CeilingThatEndsOnWhitespace_DoesNotLeaveItAtTheEndOfThePage(int ceiling, string expected)
    {
        // Sensitivity: the trim after the cut. Ceiling 8 ends after the space that follows "Arcanum", and ceiling 30 after the
        // line break that follows the first line; a page returned trimmed on every line is trimmed at its end too.
        DocumentExtractionOutcome outcome = Extract("cups-text.pdf", ceiling);

        Assert.True(outcome.Truncated);

        Assert.Equal([expected], outcome.Pages);
    }

    // ---------------------------------------------------------------- a page with nothing but whitespace

    [Fact]
    public void Extract_BlankPageThenAPageWithText_KeepsTheBlankPageAsAnEmptyEntry()
    {
        // The blank page is an empty entry, so "page 2" stays true for the text after it. Its separator is still counted.
        DocumentExtractionOutcome outcome = Extract("blank-page-then-text.pdf");

        Assert.Equal(DocumentExtractionKind.Extracted, outcome.Kind);

        Assert.False(outcome.Truncated);

        Assert.Equal([string.Empty, "TEXT-AFTER-BLANK-PAGE"], outcome.Pages);
    }
}
