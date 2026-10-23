using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension.Pdf;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension.Pdf;

public sealed class PdfTextExtractorOutcomeTests
{
    private const string EncryptedCodeword = "ENCRYPTED-CODEWORD-PAPAYA-4410";

    private static PdfExtractionOutcome Extract(string fileName) =>
        new PdfTextExtractor().Extract(PdfFixtures.Read(fileName), PdfExtractionLimits.Default, CancellationToken.None);

    private static void AssertCarriesNoText(PdfExtractionKind kind, PdfExtractionOutcome outcome)
    {
        Assert.Equal(kind, outcome.Kind);

        Assert.Empty(outcome.Pages);

        Assert.False(outcome.Truncated);
    }

    // ---------------------------------------------------------------- no text layer

    [Theory]
    [InlineData("image-only.pdf")]
    [InlineData("whitespace-only.pdf")]
    [InlineData("cyclic-pages.pdf")]
    [InlineData("cyclic-pages-self.pdf")]
    public void Extract_DocumentThatShowsNoText_IsNoTextWithoutPages(string fileName)
    {
        // Sensitivity: the "any non-whitespace" test. Two pages of an image, a run of spaces, and a page tree with no
        // reachable page all produce no entry, so the later OCR stage gets the definition of "nothing to read".
        AssertCarriesNoText(PdfExtractionKind.NoText, Extract(fileName));
    }

    // ---------------------------------------------------------------- encryption

    [Theory]
    [InlineData("encrypted-rc4-empty.pdf")]
    [InlineData("encrypted-aes-empty.pdf")]
    public void Extract_EncryptedWithTheEmptyUserPassword_IsExtracted(string fileName)
    {
        // Permission-only restrictions: the user password is empty, so any reader opens the file without asking.
        PdfExtractionOutcome outcome = Extract(fileName);

        Assert.Equal(PdfExtractionKind.Extracted, outcome.Kind);

        Assert.Equal([EncryptedCodeword], outcome.Pages);
    }

    [Theory]
    [InlineData("encrypted-rc4-password.pdf")]
    [InlineData("encrypted-aes-password.pdf")]
    public void Extract_EncryptedWithARealPassword_IsPasswordRequired(string fileName)
    {
        AssertCarriesNoText(PdfExtractionKind.PasswordRequired, Extract(fileName));
    }

    [Theory]
    [InlineData("encrypted-rc4-owner-empty.pdf")]
    [InlineData("encrypted-aes-owner-empty.pdf")]
    public void Extract_EncryptedWithARealUserPasswordButAnEmptyOwnerPassword_IsOpenedByThePasswordPdfPigTriesItself(string fileName)
    {
        // KNOWN LIMITATION, pinned so a change is noticed: no password is ever supplied, but PdfPig tries the empty
        // string as the owner password too, and an owner session decrypts the whole file. A file whose owner password is
        // the empty string therefore opens although its user password is real. See README.md.
        PdfExtractionOutcome outcome = Extract(fileName);

        Assert.Equal(PdfExtractionKind.Extracted, outcome.Kind);

        Assert.Equal([EncryptedCodeword], outcome.Pages);
    }

    // ---------------------------------------------------------------- bytes that are not a readable PDF

    [Theory]
    [InlineData("empty.pdf")]
    [InlineData("garbage.pdf")]
    [InlineData("header-only.pdf")]
    [InlineData("truncated.pdf")]
    [InlineData("bad-trailer.pdf")]
    [InlineData("missing-root.pdf")]
    public void Extract_BytesThatAreNotAReadablePdf_AreMalformedWithoutPages(string fileName)
    {
        AssertCarriesNoText(PdfExtractionKind.Malformed, Extract(fileName));
    }

    [Theory]
    [InlineData("bad-startxref.pdf")]
    [InlineData("bad-xref-offsets.pdf")]
    public void Extract_CrossReferenceTableThatPdfPigRepairsByScanning_IsStillExtracted(string fileName)
    {
        // PdfPig scans for objects whenever the xref is unusable, even with lenient parsing off. The repaired text is
        // the text the file shows, so there is no reason to refuse it.
        PdfExtractionOutcome outcome = Extract(fileName);

        Assert.Equal(PdfExtractionKind.Extracted, outcome.Kind);

        Assert.Equal(["BROKEN-XREF-TEXT"], outcome.Pages);
    }

    // ---------------------------------------------------------------- hostile structure

    [Theory]
    [InlineData("cyclic-ref-chain.pdf")]
    [InlineData("deep-array-content.pdf")]
    [InlineData("cyclic-xobject.pdf")]
    [InlineData("cyclic-xobject-ab.pdf")]
    public void Extract_StructureTheParserRefuses_IsMalformed(string fileName)
    {
        // The first two end in PdfPig's nesting-depth exception instead of a stack overflow (the reason the nightly is
        // pinned). The two form cycles are refused only with lenient parsing off: lenient parsing extracts them.
        AssertCarriesNoText(PdfExtractionKind.Malformed, Extract(fileName));
    }

    [Theory]
    [InlineData("cyclic-length.pdf", "LENGTH-CYCLE")]
    [InlineData("cyclic-pages-with-page.pdf", "PAGE-BESIDE-CYCLE")]
    public void Extract_CycleThatDoesNotStopTheRealContent_IsExtracted(string fileName, string expected)
    {
        PdfExtractionOutcome outcome = Extract(fileName);

        Assert.Equal(PdfExtractionKind.Extracted, outcome.Kind);

        Assert.Equal([expected], outcome.Pages);
    }

    [Fact]
    public void Extract_RunThatNamesAnUndefinedFont_IsSkippedAndTheRestIsExtracted()
    {
        // Sensitivity: SkipMissingFonts. Without it PdfPig throws for the whole document.
        PdfExtractionOutcome outcome = Extract("missing-font.pdf");

        Assert.Equal(PdfExtractionKind.Extracted, outcome.Kind);

        Assert.Equal(["GOOD-FONT-RUN"], outcome.Pages);
    }

    [Fact]
    public void ExtractionKind_IsAClosedSetOfFiveKinds()
    {
        Assert.Equal(
            ["Extracted", "NoText", "PasswordRequired", "Malformed", "Bounds"],
            Enum.GetNames<PdfExtractionKind>());
    }
}
