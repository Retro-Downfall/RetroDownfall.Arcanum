using System.IO.Compression;

using System.Text;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Weave;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Weave;

/// <summary>
/// A PDF whose text runs past the output-character ceiling is cut deterministically, and never between the two halves of
/// a UTF-16 surrogate pair. The PDF here is real, parsed by the production extractor, framed on the production wire and
/// projected by the registry's PDF host with its own default ceiling; the character that straddles the ceiling is a
/// supplementary-plane one (U+1F600) at the exact place the ceiling falls.
/// </summary>
public sealed class SessionAttachmentPdfOutputCeilingTests
{
    /// <summary>The registry host's default ceiling, which #162 defines as 262,144 UTF-16 characters.</summary>
    private const int Ceiling = 262_144;

    private const int FirstPageGlyphs = 120_000;

    /// <summary>The projection is <c>Page 1\n</c>, page one's text, <c>\n</c>, then <c>Page 2\n</c>.</summary>
    private const int SecondPageStart = 7 + FirstPageGlyphs + 1 + 7;

    private const string Emoji = "\U0001F600";

    [Fact]
    public async Task A_surrogate_pair_straddling_the_ceiling_is_dropped_whole_and_the_cut_is_deterministic()
    {
        // The high half is the last character the ceiling admits (index 262,143) and the low half the first it refuses.
        byte[] document = Document(lettersBeforeTheEmoji: Ceiling - 1 - SecondPageStart);

        SessionAttachmentExtractionResult first = await ExtractAsync(document);

        Assert.Equal(SessionAttachmentExtractionStatus.Extracted, first.Status);

        Assert.True(first.WasTruncated);

        Assert.Equal(Ceiling - 1, first.Text.Length);

        Assert.Equal('B', first.Text[^1]);

        Assert.DoesNotContain(Emoji[0], first.Text);

        Assert.DoesNotContain('�', first.Text);

        Assert.Equal([new SessionAttachmentPageMark(0, 1), new SessionAttachmentPageMark(7 + FirstPageGlyphs + 1, 2)], first.Pages);

        // Strict UTF-8 refuses a lone surrogate: the text can be written and stored as it is.
        _ = new UTF8Encoding(false, true).GetBytes(first.Text);

        SessionAttachmentExtractionResult second = await ExtractAsync(document);

        Assert.Equal(first.Text, second.Text);

        Assert.Equal(first.Pages, second.Pages);
    }

    [Fact]
    public async Task A_surrogate_pair_that_ends_exactly_at_the_ceiling_is_kept_whole()
    {
        // The control: one letter fewer before the emoji, and both halves fit. The ceiling is the same; only the pair moved.
        byte[] document = Document(lettersBeforeTheEmoji: Ceiling - 2 - SecondPageStart);

        SessionAttachmentExtractionResult result = await ExtractAsync(document);

        Assert.True(result.WasTruncated);

        Assert.Equal(Ceiling, result.Text.Length);

        Assert.EndsWith(Emoji, result.Text, StringComparison.Ordinal);

        Assert.True(char.IsHighSurrogate(result.Text[^2]) && char.IsLowSurrogate(result.Text[^1]));

        _ = new UTF8Encoding(false, true).GetBytes(result.Text);
    }

    private static async Task<SessionAttachmentExtractionResult> ExtractAsync(byte[] document)
    {
        using IDisposable worker = InProcessPdfWorker.Use();

        return await SessionAttachmentTextExtractor.ExtractDocumentAsync(
            new NoOpSessionAttachmentStore(readBytes: (_, _) => Task.FromResult<ReadOnlyMemory<byte>>(document)),
            ComprehensionSeamDocuments.Record(Guid.NewGuid(), SessionAttachmentComprehensionPolicy.PdfMimeType, document.Length),
            new ArcanumSettings { Features = new FeatureSettings { AttachmentRetrieval = true, Comprehension = true } },
            4 * 1024 * 1024,
            CancellationToken.None);
    }

    /// <summary>
    /// Two pages in one Identity-H font whose ToUnicode CMap maps code 1 to <c>A</c>, 2 to <c>B</c> and 3 to U+1F600. Page 1
    /// is <see cref="FirstPageGlyphs"/> letters <c>A</c>. Page 2 is <paramref name="lettersBeforeTheEmoji"/> letters
    /// <c>B</c>, the emoji, then <c>A</c>s that no ceiling reaches. Both streams are Flate-compressed to stay inside the
    /// intake ceiling.
    /// </summary>
    private static byte[] Document(int lettersBeforeTheEmoji)
    {
        string map = string.Join(
            "\n",
            "/CIDInit /ProcSet findresource begin",
            "12 dict begin",
            "begincmap",
            "/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def",
            "/CMapName /Adobe-Identity-UCS def",
            "/CMapType 2 def",
            "1 begincodespacerange",
            "<0000> <FFFF>",
            "endcodespacerange",
            "3 beginbfchar",
            "<0001> <0041>",
            "<0002> <0042>",
            "<0003> <D83DDE00>",
            "endbfchar",
            "endcmap",
            "CMapName currentdict /CMap defineresource pop",
            "end",
            "end\n");

        string firstPage = "BT /F1 6 Tf 72 700 Td <" + Repeat("0001", FirstPageGlyphs) + "> Tj ET\n";

        string secondPage = "BT /F1 6 Tf 72 700 Td <" + Repeat("0002", lettersBeforeTheEmoji) + "0003" + Repeat("0001", 200) + "> Tj ET\n";

        return new PdfDocumentBuilder()
            .Object(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Object(2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>")
            .Object(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 5 0 R /Resources << /Font << /F1 7 0 R >> >> >>")
            .Object(4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R /Resources << /Font << /F1 7 0 R >> >> >>")
            .Stream(5, "/Filter /FlateDecode", Deflate(firstPage))
            .Stream(6, "/Filter /FlateDecode", Deflate(secondPage))
            .Object(7, "<< /Type /Font /Subtype /Type0 /BaseFont /FixtureSans /Encoding /Identity-H /DescendantFonts [8 0 R] /ToUnicode 10 0 R >>")
            .Object(8, "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /FixtureSans /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /DW 1000 /CIDToGIDMap /Identity /FontDescriptor 9 0 R >>")
            .Object(9, "<< /Type /FontDescriptor /FontName /FixtureSans /Flags 4 /FontBBox [0 -200 1000 800] /ItalicAngle 0 /Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 >>")
            .Stream(10, string.Empty, map)
            .Build();
    }

    private static string Repeat(string unit, int count) => new StringBuilder(unit.Length * count).Insert(0, unit, count).ToString();

    private static byte[] Deflate(string text)
    {
        using MemoryStream output = new();

        using (ZLibStream compressor = new(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            compressor.Write(Encoding.ASCII.GetBytes(text));
        }

        return output.ToArray();
    }
}
