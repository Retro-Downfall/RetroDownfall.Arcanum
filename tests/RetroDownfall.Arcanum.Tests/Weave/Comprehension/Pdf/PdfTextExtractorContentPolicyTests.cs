using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension.Pdf;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension.Pdf;

public sealed class PdfTextExtractorContentPolicyTests
{
    private static DocumentExtractionOutcome Extract(string fileName) =>
        new PdfTextExtractor().Extract(PdfFixtures.Read(fileName), PdfExtractionLimits.Default, CancellationToken.None);

    // ---------------------------------------------------------------- what is extracted

    [Fact]
    public void Extract_InvisibleWhiteTinyStrokedAndOffPageRuns_AreAllExtractedInStreamOrder()
    {
        // #163 decides that every text-showing run is extracted, so a reader who cannot see a run still finds it in the
        // data and the label says so. Sensitivity: any filter on render mode, fill colour, point size or page bounds.
        DocumentExtractionOutcome outcome = Extract("hidden.pdf");

        Assert.Equal(DocumentExtractionKind.Extracted, outcome.Kind);

        Assert.Equal(
            [
                "VISIBLE-LINE-ALPHA\n" +
                "INVISIBLE-LINE-BRAVO\n" +
                "WHITE-ON-WHITE-CHARLIE\n" +
                "TINY-TEXT-DELTA\n" +
                "STROKE-ONLY-FOXTROT\n" +
                "OFFPAGE-ECHO\n" +
                "VISIBLE-LINE-GOLF",
            ],
            outcome.Pages);
    }

    [Fact]
    public void Extract_MarkedContentActualText_NeverReplacesTheGlyphsThatAreShown()
    {
        // /ActualText is replacement text no reader sees and the file's author controls. Sensitivity: ParsingOptions.UseActualText.
        DocumentExtractionOutcome outcome = Extract("actual-text.pdf");

        Assert.Equal(DocumentExtractionKind.Extracted, outcome.Kind);

        Assert.Equal(["SHOWN-GLYPHS-ARE-EXTRACTED"], outcome.Pages);
    }

    [Fact]
    public void Extract_ActiveContent_IsNeverRunFollowedOrEmitted()
    {
        // The file carries a JavaScript open action, a launch action, a URI action, an embedded file, a name-tree script
        // and a form field with an appearance stream. Only the page's own text may come out.
        DocumentExtractionOutcome outcome = Extract("active-content.pdf");

        Assert.Equal(DocumentExtractionKind.Extracted, outcome.Kind);

        Assert.Equal(["ACTIVE-CONTENT-VISIBLE-TEXT"], outcome.Pages);
    }

    // ---------------------------------------------------------------- characters the frame and the sink cannot carry

    [Fact]
    public void Extract_NulAndFormFeedMappedFromGlyphs_AreRemoved()
    {
        // Sensitivity: RemoveUnsafeCharacters. U+000C is the page separator on the worker wire and NUL makes the sink throw.
        DocumentExtractionOutcome outcome = Extract("control-characters.pdf");

        Assert.Equal(DocumentExtractionKind.Extracted, outcome.Kind);

        Assert.Equal(["ABC"], outcome.Pages);
    }

    [Fact]
    public void RemoveUnsafeCharacters_DropsNulAndFormFeed()
    {
        Assert.Equal("ab", PdfTextExtractor.RemoveUnsafeCharacters("\0a\fb\0\f"));
    }

    [Fact]
    public void RemoveUnsafeCharacters_ReplacesLoneSurrogatesWithTheReplacementCharacter()
    {
        // A lone surrogate cannot be written as UTF-8, so it must not reach the frame as one.
        Assert.Equal("a�b�c", PdfTextExtractor.RemoveUnsafeCharacters("a\uD83Db\uDE00c"));
    }

    [Fact]
    public void RemoveUnsafeCharacters_ReplacesAHighSurrogateAtTheEndAndALowSurrogateAtTheStart()
    {
        Assert.Equal("�a�", PdfTextExtractor.RemoveUnsafeCharacters("\uDE00a\uD83D"));
    }

    [Fact]
    public void RemoveUnsafeCharacters_KeepsValidPairsAndOrdinaryControlCharacters()
    {
        const string text = "AB\U0001F600\U00020BB7\t\r\n 日本語";

        Assert.Equal(text, PdfTextExtractor.RemoveUnsafeCharacters(text));
    }
}
