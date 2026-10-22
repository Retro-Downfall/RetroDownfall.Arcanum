using System.Text;

using RetroDownfall.Arcanum.Infrastructure.Weave;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension;

/// <summary>
/// Hand-built PresentationML packages for the PowerPoint extractor. Every fixture is a valid ZIP written by
/// <see cref="ComprehensionPackageBuilder"/>, so each test states exactly the part, attribute or relationship it is about.
/// </summary>
internal static class PresentationDeck
{
    public const string P = "http://schemas.openxmlformats.org/presentationml/2006/main";

    public const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";

    public const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public const string StrictP = "http://purl.oclc.org/ooxml/presentationml/main";

    public const string StrictA = "http://purl.oclc.org/ooxml/drawingml/main";

    public const string StrictR = "http://purl.oclc.org/ooxml/officeDocument/relationships";

    public const string Mc = "http://schemas.openxmlformats.org/markup-compatibility/2006";

    public const string PackageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";

    public const string Declaration = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>";

    public static string Type(string name) => $"{R}/{name}";

    public static string StrictType(string name) => $"{StrictR}/{name}";

    public static string Rel(string id, string type, string target, bool external = false) =>
        $"<Relationship Id=\"{id}\" Type=\"{type}\" Target=\"{target}\"{(external ? " TargetMode=\"External\"" : string.Empty)}/>";

    public static string Rels(params string[] relationships) =>
        $"{Declaration}<Relationships xmlns=\"{PackageRelationships}\">{string.Concat(relationships)}</Relationships>";

    /// <summary>A presentation part whose slide list names the given relationship ids, in order.</summary>
    public static string Presentation(string pns, string rns, params string[] slideRelationshipIds) =>
        $"{Declaration}<p:presentation xmlns:r=\"{rns}\" xmlns:p=\"{pns}\">"
        + "<p:sldMasterIdLst><p:sldMasterId id=\"2147483648\" r:id=\"rId100\"/></p:sldMasterIdLst>"
        + $"<p:sldIdLst>{string.Concat(slideRelationshipIds.Select((id, index) => $"<p:sldId id=\"{256 + index}\" r:id=\"{id}\"/>"))}</p:sldIdLst>"
        + "<p:sldSz cx=\"9144000\" cy=\"6858000\"/><p:notesSz cx=\"6858000\" cy=\"9144000\"/></p:presentation>";

    /// <summary>A slide part. <paramref name="shapes"/> sit inside the shape tree after the root group's own properties.</summary>
    public static string Slide(string shapes, string rootAttributes = "") =>
        $"{Declaration}<p:sld xmlns:a=\"{A}\" xmlns:r=\"{R}\" xmlns:p=\"{P}\"{rootAttributes}><p:cSld><p:spTree>"
        + "<p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>"
        + $"{shapes}</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>";

    public static string Paragraph(string text) => $"<a:p><a:r><a:rPr lang=\"en-US\"/><a:t>{text}</a:t></a:r></a:p>";

    /// <summary>A text shape. <paramref name="properties"/> are extra attributes for its <c>p:cNvPr</c>.</summary>
    public static string Shape(int id, string paragraphs, string properties = "") =>
        $"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"Shape {id}\"{properties}/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>"
        + $"<p:spPr/><p:txBody><a:bodyPr/><a:lstStyle/>{paragraphs}</p:txBody></p:sp>";

    /// <summary>The entries of a valid transitional deck: one slide part per slide, listed in order.</summary>
    public static List<(string Name, string Content)> Entries(params string[] slides)
    {
        string[] ids = [.. slides.Select((_, index) => $"rId{index + 1}")];

        List<(string Name, string Content)> entries =
        [
            ("[Content_Types].xml", $"{Declaration}<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>"),
            ("_rels/.rels", Rels(Rel("rId1", Type("officeDocument"), "ppt/presentation.xml"))),
            ("ppt/presentation.xml", Presentation(P, R, ids)),
            (
                "ppt/_rels/presentation.xml.rels",
                Rels([.. ids.Select((id, index) => Rel(id, Type("slide"), $"slides/slide{index + 1}.xml"))])),
        ];

        for (int index = 0; index < slides.Length; index++)
        {
            entries.Add(($"ppt/slides/slide{index + 1}.xml", slides[index]));
        }

        return entries;
    }

    public static byte[] Build(List<(string Name, string Content)> entries) => ComprehensionPackageBuilder.Build([.. entries]);

    public static byte[] Deck(params string[] slides) => Build(Entries(slides));

    public static void Replace(List<(string Name, string Content)> entries, string name, string content)
    {
        int index = entries.FindIndex(entry => entry.Name == name);

        Assert.True(index >= 0, $"{name} is not in the fixture.");

        entries[index] = (name, content);
    }

    public static async Task<SessionAttachmentExtractionResult> ExtractAsync(
        byte[] bytes,
        PresentationDocumentExtractor? extractor = null,
        int maximumCharacters = 262_144)
    {
        BoundedDocumentTextSink sink = new(maximumCharacters, CancellationToken.None);

        PresentationDocumentExtractor target = extractor ?? new PresentationDocumentExtractor();

        // Run on a pool thread with a deadline, so a regression that loops forever fails this test instead of hanging the run.
        await Task.Run(() => target.ExtractAsync(bytes, sink, CancellationToken.None).AsTask()).WaitAsync(TimeSpan.FromSeconds(20));

        return sink.Complete();
    }

    public static async Task<string> TextOfAsync(byte[] bytes)
    {
        SessionAttachmentExtractionResult result = await ExtractAsync(bytes);

        Assert.Equal(SessionAttachmentExtractionStatus.Extracted, result.Status);

        return result.Text;
    }

    public static async Task AssertRejectedAsync(
        DocumentPackageRejection expected,
        byte[] bytes,
        PresentationDocumentExtractor? extractor = null)
    {
        DocumentPackageRejectedException exception = await Assert.ThrowsAsync<DocumentPackageRejectedException>(
            () => ExtractAsync(bytes, extractor));

        Assert.Equal(expected, exception.Rejection);
    }
}

public sealed class PresentationDocumentExtractorTests
{
    private static string Paragraph(string text) => PresentationDeck.Paragraph(text);

    private static string Shape(int id, string paragraphs, string properties = "") => PresentationDeck.Shape(id, paragraphs, properties);

    private static string Slide(string shapes, string rootAttributes = "") => PresentationDeck.Slide(shapes, rootAttributes);

    // ---------------------------------------------------------------- projection

    [Fact]
    public async Task Extract_TwoSlides_ProjectsNumberedSlidesWithTheirParagraphs()
    {
        byte[] deck = PresentationDeck.Deck(
            Slide(Shape(2, Paragraph("Quarterly review") + Paragraph("Revenue grew"))),
            Slide(Shape(2, Paragraph("Next steps"))));

        SessionAttachmentExtractionResult result = await PresentationDeck.ExtractAsync(deck);

        Assert.Equal(SessionAttachmentExtractionStatus.Extracted, result.Status);

        Assert.Equal("Slide 1\nQuarterly review\nRevenue grew\nSlide 2\nNext steps\n", result.Text);

        Assert.False(result.WasTruncated);
    }

    [Fact]
    public async Task Extract_SlideOrderIsTheSlideListOrderNotTheFileOrder()
    {
        // Sensitivity: iterating the relationships or the slide file names instead of p:sldIdLst.
        List<(string Name, string Content)> entries = PresentationDeck.Entries(
            Slide(Shape(2, Paragraph("File one"))),
            Slide(Shape(2, Paragraph("File two"))),
            Slide(Shape(2, Paragraph("File three"))));

        PresentationDeck.Replace(
            entries,
            "ppt/presentation.xml",
            PresentationDeck.Presentation(PresentationDeck.P, PresentationDeck.R, "rId3", "rId1", "rId2"));

        Assert.Equal(
            "Slide 1\nFile three\nSlide 2\nFile one\nSlide 3\nFile two\n",
            await PresentationDeck.TextOfAsync(PresentationDeck.Build(entries)));
    }

    [Fact]
    public async Task Extract_RunsLineBreaksAndFields_JoinIntoParagraphs()
    {
        string paragraph =
            "<a:p><a:r><a:t>Hello</a:t></a:r><a:r><a:t> </a:t></a:r><a:r><a:t>World</a:t></a:r><a:br><a:rPr lang=\"en-US\"/></a:br>"
            + "<a:r><a:t>Next</a:t></a:r><a:fld id=\"{B6F15528-21DE-4FAA-801E-634DDDAF4B2B}\" type=\"slidenum\"><a:rPr lang=\"en-US\"/><a:t>7</a:t></a:fld></a:p>";

        Assert.Equal("Slide 1\nHello World\nNext7\n", await PresentationDeck.TextOfAsync(PresentationDeck.Deck(Slide(Shape(2, paragraph)))));
    }

    [Fact]
    public async Task Extract_EmptyParagraphs_StillEndWithANewline()
    {
        string paragraphs = "<a:p/>" + "<a:p><a:endParaRPr lang=\"en-US\"/></a:p>" + Paragraph("After");

        Assert.Equal("Slide 1\n\n\nAfter\n", await PresentationDeck.TextOfAsync(PresentationDeck.Deck(Slide(Shape(2, paragraphs)))));
    }

    [Fact]
    public async Task Extract_EntitiesCdataAndUnicode_AreDecodedIntoPlainText()
    {
        string paragraphs =
            Paragraph("A &amp; B &lt;C&gt; &#169; Ünïcödé \U0001F600")
            + "<a:p><a:r><a:t><![CDATA[x < y && <b>raw</b>]]></a:t></a:r></a:p>";

        Assert.Equal(
            "Slide 1\nA & B <C> © Ünïcödé \U0001F600\nx < y && <b>raw</b>\n",
            await PresentationDeck.TextOfAsync(PresentationDeck.Deck(Slide(Shape(2, paragraphs)))));
    }

    [Fact]
    public async Task Extract_WhitespaceOnlyRunsAndPreservedSpaces_KeepTheirSpaces()
    {
        string paragraph =
            "<a:p><a:r><a:t>one</a:t></a:r><a:r><a:t xml:space=\"preserve\">  </a:t></a:r><a:r><a:t>two</a:t></a:r></a:p>";

        Assert.Equal("Slide 1\none  two\n", await PresentationDeck.TextOfAsync(PresentationDeck.Deck(Slide(Shape(2, paragraph)))));
    }

    [Fact]
    public async Task Extract_AltTextNamesAndDescriptions_AreNeverEmitted()
    {
        string shape =
            "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"NAMELEAK\" descr=\"ALTLEAK\" title=\"TITLELEAK\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>"
            + $"<p:spPr/><p:txBody><a:bodyPr/><a:lstStyle/>{Paragraph("Visible")}</p:txBody></p:sp>";

        string text = await PresentationDeck.TextOfAsync(PresentationDeck.Deck(Slide(shape)));

        Assert.Equal("Slide 1\nVisible\n", text);
    }

    [Fact]
    public async Task Extract_StrictNamespaces_ProjectTheSameText()
    {
        string slide =
            $"{PresentationDeck.Declaration}<p:sld xmlns:a=\"{PresentationDeck.StrictA}\" xmlns:r=\"{PresentationDeck.StrictR}\" xmlns:p=\"{PresentationDeck.StrictP}\">"
            + "<p:cSld><p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>"
            + $"{Shape(2, Paragraph("Strict text"))}</p:spTree></p:cSld></p:sld>";

        List<(string Name, string Content)> entries =
        [
            ("_rels/.rels", PresentationDeck.Rels(PresentationDeck.Rel("rId1", PresentationDeck.StrictType("officeDocument"), "ppt/presentation.xml"))),
            ("ppt/presentation.xml", PresentationDeck.Presentation(PresentationDeck.StrictP, PresentationDeck.StrictR, "rId1")),
            (
                "ppt/_rels/presentation.xml.rels",
                PresentationDeck.Rels(PresentationDeck.Rel("rId1", PresentationDeck.StrictType("slide"), "slides/slide1.xml"))),
            ("ppt/slides/slide1.xml", slide),
        ];

        Assert.Equal("Slide 1\nStrict text\n", await PresentationDeck.TextOfAsync(PresentationDeck.Build(entries)));
    }

    // ---------------------------------------------------------------- main part and format

    [Theory]
    [InlineData("ppt/presentation.xml")]
    [InlineData("/ppt/presentation.xml")]
    [InlineData("./ppt/presentation.xml")]
    [InlineData("PPT/Presentation.XML")]
    [InlineData("ppt/pres%65ntation.xml")]
    public async Task Extract_PresentationTargetSpellings_ResolveToTheSamePart(string target)
    {
        List<(string Name, string Content)> entries = PresentationDeck.Entries(Slide(Shape(2, Paragraph("Resolved"))));

        PresentationDeck.Replace(
            entries,
            "_rels/.rels",
            PresentationDeck.Rels(PresentationDeck.Rel("rId1", PresentationDeck.Type("officeDocument"), target)));

        Assert.Equal("Slide 1\nResolved\n", await PresentationDeck.TextOfAsync(PresentationDeck.Build(entries)));
    }

    [Fact]
    public async Task Extract_NoOfficeDocumentRelationship_IsMissingPart()
    {
        List<(string Name, string Content)> entries = PresentationDeck.Entries(Slide(Shape(2, Paragraph("x"))));

        PresentationDeck.Replace(
            entries,
            "_rels/.rels",
            PresentationDeck.Rels(PresentationDeck.Rel("rId1", PresentationDeck.Type("extended-properties"), "ppt/presentation.xml")));

        await PresentationDeck.AssertRejectedAsync(DocumentPackageRejection.MissingPart, PresentationDeck.Build(entries));
    }

    [Fact]
    public async Task Extract_OfficeDocumentTargetAbsentFromThePackage_IsMissingPart()
    {
        List<(string Name, string Content)> entries = PresentationDeck.Entries(Slide(Shape(2, Paragraph("x"))));

        PresentationDeck.Replace(
            entries,
            "_rels/.rels",
            PresentationDeck.Rels(PresentationDeck.Rel("rId1", PresentationDeck.Type("officeDocument"), "ppt/elsewhere.xml")));

        await PresentationDeck.AssertRejectedAsync(DocumentPackageRejection.MissingPart, PresentationDeck.Build(entries));
    }

    [Fact]
    public async Task Extract_ExternalOfficeDocumentRelationship_IsNeverFollowed()
    {
        List<(string Name, string Content)> entries = PresentationDeck.Entries(Slide(Shape(2, Paragraph("x"))));

        PresentationDeck.Replace(
            entries,
            "_rels/.rels",
            PresentationDeck.Rels(PresentationDeck.Rel("rId1", PresentationDeck.Type("officeDocument"), "https://evil.example/presentation.xml", external: true)));

        await PresentationDeck.AssertRejectedAsync(DocumentPackageRejection.MissingPart, PresentationDeck.Build(entries));
    }

    [Fact]
    public async Task Extract_TwoOfficeDocumentRelationships_AreAmbiguousAndRefused()
    {
        // A second main part would let a consumer that picks the other one show a different document.
        List<(string Name, string Content)> entries = PresentationDeck.Entries(Slide(Shape(2, Paragraph("Shown"))));

        entries.Add(("ppt/other.xml", entries.Single(entry => entry.Name == "ppt/presentation.xml").Content));

        PresentationDeck.Replace(
            entries,
            "_rels/.rels",
            PresentationDeck.Rels(
                PresentationDeck.Rel("rId1", PresentationDeck.Type("officeDocument"), "ppt/presentation.xml"),
                PresentationDeck.Rel("rId2", PresentationDeck.Type("officeDocument"), "ppt/other.xml")));

        await PresentationDeck.AssertRejectedAsync(DocumentPackageRejection.FormatMismatch, PresentationDeck.Build(entries));
    }

    [Fact]
    public async Task Extract_WordprocessingPackage_IsFormatMismatch()
    {
        byte[] docx = ComprehensionPackageBuilder.Build(
            ("_rels/.rels", PresentationDeck.Rels(PresentationDeck.Rel("rId1", PresentationDeck.Type("officeDocument"), "word/document.xml"))),
            (
                "word/document.xml",
                $"{PresentationDeck.Declaration}<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>Word text</w:t></w:r></w:p></w:body></w:document>"));

        await PresentationDeck.AssertRejectedAsync(DocumentPackageRejection.FormatMismatch, docx);
    }

    [Fact]
    public async Task Extract_WordprocessingPackageWithAnUnusableRelationship_IsStillFormatMismatch()
    {
        // Sensitivity: reading the main part's relationships before checking its root element. This package's
        // document relationships escape the package root, which would otherwise surface as an unsafe entry name.
        byte[] docx = ComprehensionPackageBuilder.Build(
            ("_rels/.rels", PresentationDeck.Rels(PresentationDeck.Rel("rId1", PresentationDeck.Type("officeDocument"), "word/document.xml"))),
            (
                "word/_rels/document.xml.rels",
                PresentationDeck.Rels(PresentationDeck.Rel("rId1", PresentationDeck.Type("customXml"), "../../escape.xml"))),
            (
                "word/document.xml",
                $"{PresentationDeck.Declaration}<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"/>"));

        await PresentationDeck.AssertRejectedAsync(DocumentPackageRejection.FormatMismatch, docx);
    }

    [Fact]
    public async Task Extract_RealTextutilDocx_IsFormatMismatch()
    {
        byte[] docx = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "Comprehension", "textutil-basic.docx"));

        await PresentationDeck.AssertRejectedAsync(DocumentPackageRejection.FormatMismatch, docx);
    }

    [Theory]
    [InlineData("http://schemas.openxmlformats.org/wordprocessingml/2006/main", "presentation")]
    [InlineData("http://schemas.openxmlformats.org/presentationml/2006/main", "sld")]
    [InlineData("http://schemas.openxmlformats.org/presentationml/2006/main", "Presentation")]
    [InlineData("", "presentation")]
    public async Task Extract_PresentationPartWithAnotherRoot_IsFormatMismatch(string rootNamespace, string rootName)
    {
        List<(string Name, string Content)> entries = PresentationDeck.Entries(Slide(Shape(2, Paragraph("x"))));

        string declaration = rootNamespace.Length == 0 ? string.Empty : $" xmlns=\"{rootNamespace}\"";

        PresentationDeck.Replace(entries, "ppt/presentation.xml", $"{PresentationDeck.Declaration}<{rootName}{declaration}/>");

        await PresentationDeck.AssertRejectedAsync(DocumentPackageRejection.FormatMismatch, PresentationDeck.Build(entries));
    }

    [Fact]
    public async Task Extract_OleCompoundFile_IsEncrypted()
    {
        byte[] compound = new byte[2048];

        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(compound, 0);

        await PresentationDeck.AssertRejectedAsync(DocumentPackageRejection.Encrypted, compound);
    }

    [Fact]
    public async Task Extract_BytesThatAreNotAZip_AreNotZip()
    {
        await PresentationDeck.AssertRejectedAsync(DocumentPackageRejection.NotZip, Encoding.ASCII.GetBytes("this is plainly not a zip container at all"));
    }
}
