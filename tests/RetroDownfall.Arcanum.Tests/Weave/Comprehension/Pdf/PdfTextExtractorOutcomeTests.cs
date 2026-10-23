using System.Text;

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

    [Fact]
    public void Extract_CertificateEncryptedDocument_IsPasswordRequiredNotMalformed()
    {
        // Public-key security has no password to ask for, and PdfPig has no handler for it: it reads the dictionary as a
        // Standard one and fails with an unrelated exception. The file is well formed and protected, so calling it damaged
        // names the wrong condition.
        AssertCarriesNoText(PdfExtractionKind.PasswordRequired, Extract("encrypted-pubsec.pdf"));
    }

    [Fact]
    public void Extract_CertificateEncryptedDocumentWithACrossReferenceStream_IsPasswordRequired()
    {
        // A PDF 1.5 file keeps its trailer entries in the cross-reference stream's dictionary, so that is where /Encrypt is.
        AssertCarriesNoText(PdfExtractionKind.PasswordRequired, Extract("encrypted-pubsec-xref-stream.pdf"));
    }

    [Fact]
    public void Extract_FileThatOpensButWhosePageIsRefused_IsMalformedEvenWhenItsTextMentionsEncrypt()
    {
        // Sensitivity: the check runs only when opening fails. This file opens, so a name in its text proves nothing.
        AssertCarriesNoText(PdfExtractionKind.Malformed, Extract("page-failure-mentions-encrypt.pdf"));
    }

    [Fact]
    public void Extract_DamagedFileWhosePageTextMentionsEncrypt_IsMalformed()
    {
        // Sensitivity: only a trailer declares encryption. The file has no trailer and cannot be opened, and the name is in
        // its page text, so it is damaged, not protected.
        AssertCarriesNoText(PdfExtractionKind.Malformed, Extract("truncated-mentions-encrypt.pdf"));
    }

    [Theory]
    [InlineData("1 0 obj\n<< /EncryptMetadata false >>\nendobj\n")]
    [InlineData("1 0 obj\n(a (b) \\) trailer << /Encrypt 2 0 R >>)\nendobj\n")]
    [InlineData("% trailer << /Encrypt 2 0 R >>\n")]
    [InlineData("1 0 obj\n<< /Length 30 >>\nstream\ntrailer << /Encrypt 2 0 R >>\nendstream\nendobj\n")]
    [InlineData("1 0 obj\n<< /Type /Annot /Encrypt 2 0 R >>\nendobj\n")]
    [InlineData("1 0 obj\n<< /Type /Annot /Kids [ /Encrypt ] >>\nendobj\n")]
    [InlineData("trailer\n<< /Info << /Encrypt 2 0 R >> >>\n")]
    [InlineData("1 0 obj\n<< /Type /XRef /Info << /Encrypt 2 0 R >> >>\nendobj\n")]
    [InlineData("1 0 obj\n<< /Kind /XRef /Encrypt 2 0 R >>\nendobj\n")]
    public void Extract_UnreadableBytesThatNameEncryptOutsideATrailer_AreMalformed(string body)
    {
        // Sensitivity: each part of the trailer test. /EncryptMetadata is another key; a string, a comment and a stream's data
        // are not dictionaries; an ordinary object's dictionary, an array inside it, a dictionary nested in a trailer or in a
        // cross-reference stream's dictionary, and a dictionary where /XRef is not the /Type are not the trailer.
        byte[] bytes = Encoding.ASCII.GetBytes("%PDF-1.4\n" + body);

        PdfExtractionOutcome outcome = new PdfTextExtractor().Extract(bytes, PdfExtractionLimits.Default, CancellationToken.None);

        AssertCarriesNoText(PdfExtractionKind.Malformed, outcome);
    }

    [Theory]
    [InlineData("trailer\n<< /Size 3 /Root 1 0 R /Encrypt 2 0 R >>\n")]
    [InlineData("trailer % the trailer follows\n<</Encrypt<</Filter/Adobe.PubSec>>>>\n")]
    [InlineData("1 0 obj\n<< /Type/XRef /Size 3 /Encrypt 2 0 R >>\nstream\n\nendstream\nendobj\n")]
    public void Extract_UnreadableBytesWhoseTrailerDeclaresEncryption_ArePasswordRequired(string body)
    {
        // The controls for the tests above: a trailer, written loosely or not, and a cross-reference stream's dictionary.
        byte[] bytes = Encoding.ASCII.GetBytes("%PDF-1.4\n" + body);

        PdfExtractionOutcome outcome = new PdfTextExtractor().Extract(bytes, PdfExtractionLimits.Default, CancellationToken.None);

        AssertCarriesNoText(PdfExtractionKind.PasswordRequired, outcome);
    }

    // ---------------------------------------------------------------- bytes that are not a readable PDF

    [Theory]
    [InlineData("empty.pdf")]
    [InlineData("garbage.pdf")]
    [InlineData("header-only.pdf")]
    [InlineData("truncated.pdf")]
    [InlineData("bad-trailer.pdf")]
    [InlineData("missing-root.pdf")]
    [InlineData("xref-stream-garbled.pdf")]
    public void Extract_BytesThatAreNotAReadablePdf_AreMalformedWithoutPages(string fileName)
    {
        AssertCarriesNoText(PdfExtractionKind.Malformed, Extract(fileName));
    }

    [Fact]
    public void Extract_CrossReferenceStreamWhoseEntriesAreGarbled_IsMalformedWhileTheIntactFileIsExtracted()
    {
        // The malformed-cross-reference case a repair scan cannot rescue: every object lives inside an object stream and
        // is reachable only through the cross-reference stream. The control is the same file; only the stream's 32 bytes
        // of entries differ, so the entries are the whole reason the garbled file is refused.
        byte[] intact = PdfFixtures.Read("xref-stream.pdf");

        byte[] garbled = PdfFixtures.Read("xref-stream-garbled.pdf");

        Assert.Equal(intact.Length, garbled.Length);

        int[] differing = [.. Enumerable.Range(0, intact.Length).Where(index => intact[index] != garbled[index])];

        Assert.Equal(32, differing.Length);

        Assert.Equal(31, differing[^1] - differing[0]);

        PdfExtractionOutcome control = Extract("xref-stream.pdf");

        Assert.Equal(PdfExtractionKind.Extracted, control.Kind);

        Assert.Equal(["XREF-STREAM-TEXT"], control.Pages);

        AssertCarriesNoText(PdfExtractionKind.Malformed, Extract("xref-stream-garbled.pdf"));
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

    // ---------------------------------------------------------------- references to objects the file does not define

    [Fact]
    public void Extract_PageWhoseContentsIsAnUndefinedObject_IsBlankAndTheOtherPagesAreExtracted()
    {
        // ISO 32000-1 7.3.10: a reference to an undefined object is the null object, so the page is blank. Refusing the
        // document would lose page 1, which is good, on every path that reads it.
        PdfExtractionOutcome outcome = Extract("contents-undefined-page-two.pdf");

        Assert.Equal(PdfExtractionKind.Extracted, outcome.Kind);

        Assert.Equal(["GOOD-PAGE-ONE", string.Empty], outcome.Pages);
    }

    [Fact]
    public void Extract_ContentsArrayWithOneUndefinedElement_ExtractsTheOtherElements()
    {
        PdfExtractionOutcome outcome = Extract("contents-undefined-element.pdf");

        Assert.Equal(PdfExtractionKind.Extracted, outcome.Kind);

        Assert.Equal(["ARRAY-ELEMENT-TEXT"], outcome.Pages);
    }

    [Fact]
    public void Extract_OnlyPageHasAnUndefinedContents_ShowsNoText()
    {
        AssertCarriesNoText(PdfExtractionKind.NoText, Extract("contents-undefined-only-page.pdf"));
    }

    [Fact]
    public void Extract_UndefinedContentsInAPageTreeThatListsItself_TerminatesAndShowsNoText()
    {
        // Sensitivity: the visited set of the repair's walk of the page tree. Without it this file never returns.
        AssertCarriesNoText(PdfExtractionKind.NoText, Extract("contents-undefined-in-cyclic-page-tree.pdf"));
    }

    [Theory]
    [InlineData("contents-undefined-beside-refused-page.pdf")]
    [InlineData("contents-not-a-stream.pdf")]
    public void Extract_ReferenceTheRepairMustNotForgive_IsStillMalformed(string fileName)
    {
        // Sensitivity: how narrow the repair is. A second page the parser refuses for its structure, and a /Contents
        // element that exists but is not a stream (the wrong type, not null), still make the document malformed.
        AssertCarriesNoText(PdfExtractionKind.Malformed, Extract(fileName));
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
