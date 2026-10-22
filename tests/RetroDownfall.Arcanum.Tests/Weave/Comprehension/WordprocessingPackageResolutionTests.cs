using System.Text;

using RetroDownfall.Arcanum.Infrastructure.Weave;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension;

public sealed class WordprocessingPackageResolutionTests
{
    // ---------------------------------------------------------------- finding the main part

    [Fact]
    public void Extract_PackageWithoutRootRelationships_IsMissingPart()
    {
        byte[] package = ComprehensionPackageBuilder.Build(("word/document.xml", WordprocessingFixtures.Document(WordprocessingFixtures.P("x"))));

        Assert.Equal(DocumentPackageRejection.MissingPart, WordprocessingFixtures.Rejection(package));
    }

    [Fact]
    public void Extract_RootRelationshipsWithoutAnOfficeDocumentRelationship_IsMissingPart()
    {
        byte[] package = ComprehensionPackageBuilder.Build(
            ("_rels/.rels", WordprocessingFixtures.Relationships(WordprocessingFixtures.Relationship("rId1", WordprocessingFixtures.StylesType, "word/document.xml"))),
            ("word/document.xml", WordprocessingFixtures.Document(WordprocessingFixtures.P("x"))));

        Assert.Equal(DocumentPackageRejection.MissingPart, WordprocessingFixtures.Rejection(package));
    }

    [Fact]
    public void Extract_OfficeDocumentRelationshipToAnAbsentPart_IsMissingPart()
    {
        byte[] package = ComprehensionPackageBuilder.Build(
            ("_rels/.rels", WordprocessingFixtures.Relationships(WordprocessingFixtures.Relationship("rId1", WordprocessingFixtures.OfficeDocumentType, "word/missing.xml"))),
            ("word/document.xml", WordprocessingFixtures.Document(WordprocessingFixtures.P("x"))));

        Assert.Equal(DocumentPackageRejection.MissingPart, WordprocessingFixtures.Rejection(package));
    }

    [Fact]
    public void Extract_ExternalOfficeDocumentRelationship_IsNeverFollowed()
    {
        byte[] package = ComprehensionPackageBuilder.Build(
            ("_rels/.rels", WordprocessingFixtures.Relationships(WordprocessingFixtures.Relationship("rId1", WordprocessingFixtures.OfficeDocumentType, "http://evil.example/document.xml", external: true))),
            ("word/document.xml", WordprocessingFixtures.Document(WordprocessingFixtures.P("decoy"))));

        Assert.Equal(DocumentPackageRejection.MissingPart, WordprocessingFixtures.Rejection(package));
    }

    [Fact]
    public void Extract_MainPartIsFoundByRelationshipNotByTheConventionalName()
    {
        byte[] package = ComprehensionPackageBuilder.Build(
            ("_rels/.rels", WordprocessingFixtures.Relationships(WordprocessingFixtures.Relationship("rId1", WordprocessingFixtures.OfficeDocumentType, "/custom/body.xml"))),
            ("word/document.xml", WordprocessingFixtures.Document(WordprocessingFixtures.P("DECOY"))),
            ("custom/body.xml", WordprocessingFixtures.Document(WordprocessingFixtures.P("chosen"))));

        Assert.Equal("chosen\n", WordprocessingFixtures.Extract(package));
    }

    [Fact]
    public void Extract_MainPartTargetIsMatchedCaseInsensitivelyAndAfterOnePercentUnescape()
    {
        byte[] caseDifference = ComprehensionPackageBuilder.Build(
            ("_rels/.rels", WordprocessingFixtures.Relationships(WordprocessingFixtures.Relationship("rId1", WordprocessingFixtures.OfficeDocumentType, "WORD/Document.XML"))),
            ("word/document.xml", WordprocessingFixtures.Document(WordprocessingFixtures.P("case"))));

        byte[] escaped = ComprehensionPackageBuilder.Build(
            ("_rels/.rels", WordprocessingFixtures.Relationships(WordprocessingFixtures.Relationship("rId1", WordprocessingFixtures.OfficeDocumentType, "word/my%20doc.xml"))),
            ("word/my doc.xml", WordprocessingFixtures.Document(WordprocessingFixtures.P("escaped"))));

        Assert.Equal("case\n", WordprocessingFixtures.Extract(caseDifference));

        Assert.Equal("escaped\n", WordprocessingFixtures.Extract(escaped));
    }

    [Fact]
    public void Extract_ContentTypesPart_IsNotConsulted()
    {
        // A package that declares nothing (or lies) in [Content_Types].xml is judged by its main part's root element.
        byte[] package = ComprehensionPackageBuilder.Build(
            ("[Content_Types].xml", "this is not xml at all <<<"),
            ("_rels/.rels", WordprocessingFixtures.Relationships(WordprocessingFixtures.Relationship("rId1", WordprocessingFixtures.OfficeDocumentType, "word/document.xml"))),
            ("word/document.xml", WordprocessingFixtures.Document(WordprocessingFixtures.P("fine"))));

        Assert.Equal("fine\n", WordprocessingFixtures.Extract(package));
    }

    // ---------------------------------------------------------------- the declared format

    [Theory]
    [InlineData("<w:body xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:p/></w:body>")]
    [InlineData("<document xmlns=\"urn:not:wordprocessing\"><body/></document>")]
    [InlineData("<document><body/></document>")]
    [InlineData("<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheets/></workbook>")]
    [InlineData("<p:presentation xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"/>")]
    [InlineData("<w:styles xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"/>")]
    public void Extract_MainPartWithAnotherRootElement_IsFormatMismatch(string mainPart)
    {
        Assert.Equal(DocumentPackageRejection.FormatMismatch, WordprocessingFixtures.Rejection(WordprocessingFixtures.Package(mainPart)));
    }

    [Fact]
    public void Extract_SpreadsheetPackage_IsFormatMismatch()
    {
        byte[] workbook = ComprehensionPackageBuilder.Build(
            ("_rels/.rels", WordprocessingFixtures.Relationships(WordprocessingFixtures.Relationship("rId1", WordprocessingFixtures.OfficeDocumentType, "xl/workbook.xml"))),
            ("xl/workbook.xml", "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheets><sheet name=\"S\" sheetId=\"1\" r:id=\"rId1\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"/></sheets></workbook>"));

        Assert.Equal(DocumentPackageRejection.FormatMismatch, WordprocessingFixtures.Rejection(workbook));
    }

    [Fact]
    public void Extract_PresentationPackage_IsFormatMismatch()
    {
        byte[] presentation = ComprehensionPackageBuilder.Build(
            ("_rels/.rels", WordprocessingFixtures.Relationships(WordprocessingFixtures.Relationship("rId1", WordprocessingFixtures.OfficeDocumentType, "ppt/presentation.xml"))),
            ("ppt/presentation.xml", "<p:presentation xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"/>"));

        Assert.Equal(DocumentPackageRejection.FormatMismatch, WordprocessingFixtures.Rejection(presentation));
    }

    [Fact]
    public void Extract_OpenDocumentPackage_IsMissingPart()
    {
        byte[] odt = ComprehensionPackageBuilder.Build(
            ("mimetype", "application/vnd.oasis.opendocument.text"),
            ("content.xml", "<office:document-content xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\"/>"));

        Assert.Equal(DocumentPackageRejection.MissingPart, WordprocessingFixtures.Rejection(odt));
    }

    // ---------------------------------------------------------------- hostile or broken XML

    [Theory]
    [InlineData("")]
    [InlineData("not xml at all")]
    [InlineData("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p>")]
    [InlineData("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p></w:body></w:document>")]
    public void Extract_MainPartThatIsNotWellFormed_IsMalformedXml(string mainPart)
    {
        Assert.Equal(DocumentPackageRejection.MalformedXml, WordprocessingFixtures.Rejection(WordprocessingFixtures.Package(mainPart)));
    }

    [Fact]
    public void Extract_MalformedXmlLateInTheBody_DiscardsEverythingBeforeIt()
    {
        string main = $"<w:document {WordprocessingFixtures.Namespaces()}><w:body>{WordprocessingFixtures.P("early")}<w:p><w:r><w:t>late</w:r></w:p></w:body></w:document>";

        Assert.Equal(DocumentPackageRejection.MalformedXml, WordprocessingFixtures.Rejection(WordprocessingFixtures.Package(main)));
    }

    [Theory]
    [InlineData("<!DOCTYPE w:document [<!ENTITY secret \"SECRET_VALUE\">]>")]
    [InlineData("<!DOCTYPE w:document SYSTEM \"file:///etc/passwd\">")]
    [InlineData("<!DOCTYPE w:document>")]
    public void Extract_DoctypeInTheMainPart_IsMalformedXml(string doctype)
    {
        // Sensitivity: DtdProcessing.Prohibit. With Parse the entity below would expand to SECRET_VALUE.
        string main = $"<?xml version=\"1.0\"?>{doctype}<w:document {WordprocessingFixtures.Namespaces()}><w:body><w:p><w:r><w:t>&secret;</w:t></w:r></w:p></w:body></w:document>";

        Assert.Equal(DocumentPackageRejection.MalformedXml, WordprocessingFixtures.Rejection(WordprocessingFixtures.Package(main)));
    }

    [Fact]
    public void Extract_DoctypeInTheStylesPart_IsMalformedXml()
    {
        string styles = $"<!DOCTYPE w:styles [<!ENTITY x \"y\">]><w:styles {WordprocessingFixtures.Namespaces()}/>";

        byte[] package = WordprocessingFixtures.Package(WordprocessingFixtures.Document(WordprocessingFixtures.P("x")), stylesXml: styles);

        Assert.Equal(DocumentPackageRejection.MalformedXml, WordprocessingFixtures.Rejection(package));
    }

    [Fact]
    public void Extract_DoctypeInANotesPart_IsMalformedXml()
    {
        string footnotes = $"<!DOCTYPE w:footnotes [<!ENTITY x \"y\">]><w:footnotes {WordprocessingFixtures.Namespaces()}/>";

        byte[] package = WordprocessingFixtures.Package(WordprocessingFixtures.Document(WordprocessingFixtures.P("x")), footnotesXml: footnotes);

        Assert.Equal(DocumentPackageRejection.MalformedXml, WordprocessingFixtures.Rejection(package));
    }

    [Fact]
    public void Extract_BillionLaughsEntityExpansion_IsMalformedXmlAndBounded()
    {
        StringBuilder doctype = new("<!DOCTYPE w:document [<!ENTITY a0 \"lol\">");

        for (int level = 1; level <= 9; level++)
        {
            doctype.Append($"<!ENTITY a{level} \"{string.Concat(Enumerable.Repeat($"&a{level - 1};", 10))}\">");
        }

        doctype.Append("]>");

        string main = $"{doctype}<w:document {WordprocessingFixtures.Namespaces()}><w:body><w:p><w:r><w:t>&a9;</w:t></w:r></w:p></w:body></w:document>";

        Assert.Equal(DocumentPackageRejection.MalformedXml, WordprocessingFixtures.Rejection(WordprocessingFixtures.Package(main)));
    }

    [Fact]
    public void Extract_ElementNestingBeyondTheDepthBound_IsStructureBounds()
    {
        string bomb = string.Concat(Enumerable.Repeat("<w:sdt><w:sdtContent>", 200)) + WordprocessingFixtures.P("deep") + string.Concat(Enumerable.Repeat("</w:sdtContent></w:sdt>", 200));

        Assert.Equal(DocumentPackageRejection.StructureBounds, WordprocessingFixtures.Rejection(WordprocessingFixtures.Docx(bomb)));
    }

    [Fact]
    public void Extract_NestingJustInsideTheDepthBound_IsProjected()
    {
        string nested = string.Concat(Enumerable.Repeat("<w:sdt><w:sdtContent>", 123)) + WordprocessingFixtures.P("deep") + string.Concat(Enumerable.Repeat("</w:sdtContent></w:sdt>", 123));

        Assert.Equal("deep\n", WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(nested)));
    }

    [Fact]
    public void Extract_UnclosedNestingBomb_IsRejectedWithoutExhaustingTheStack()
    {
        // Never closed, so the depth bound is reached long before the end of the part.
        string main = $"<w:document {WordprocessingFixtures.Namespaces()}><w:body>{string.Concat(Enumerable.Repeat("<w:sdt><w:sdtContent>", 5_000))}";

        Assert.Equal(DocumentPackageRejection.StructureBounds, WordprocessingFixtures.Rejection(WordprocessingFixtures.Package(main)));
    }

    // ---------------------------------------------------------------- the container itself

    [Fact]
    public void Extract_BytesThatAreNotAZip_IsNotZip()
    {
        Assert.Equal(DocumentPackageRejection.NotZip, WordprocessingFixtures.Rejection(Encoding.UTF8.GetBytes("this is plainly not a zip container, just text")));
    }

    [Fact]
    public void Extract_OleCompoundFile_IsEncrypted()
    {
        byte[] cfb = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, .. new byte[600]];

        Assert.Equal(DocumentPackageRejection.Encrypted, WordprocessingFixtures.Rejection(cfb));
    }

    [Fact]
    public void Extract_ZipBombMainPart_IsExpansionBounds()
    {
        byte[] bomb = ComprehensionPackageBuilder.Build(
            ("_rels/.rels", WordprocessingFixtures.Relationships(WordprocessingFixtures.Relationship("rId1", WordprocessingFixtures.OfficeDocumentType, "word/document.xml"))),
            ("word/document.xml", new string('A', 40 * 1024 * 1024)));

        Assert.Equal(DocumentPackageRejection.ExpansionBounds, WordprocessingFixtures.Rejection(bomb));
    }

    // ---------------------------------------------------------------- parts that must never be read

    [Fact]
    public void Extract_UnreferencedPartsAreNeverParsed()
    {
        byte[] package = WordprocessingFixtures.Package(
            WordprocessingFixtures.Document(WordprocessingFixtures.P("real")),
            extraEntries:
            [
                ("word/smuggled.xml", WordprocessingFixtures.Document(WordprocessingFixtures.P("SMUGGLED_TEXT"))),
                ("word/smuggled-broken.xml", "<<< this part is deliberately malformed and must never be parsed"),
                ("word/document2.xml", WordprocessingFixtures.Document(WordprocessingFixtures.P("SECOND_MAIN"))),
                ("docProps/core.xml", "<<<not xml"),
                ("customXml/item1.xml", "<<<not xml"),
            ]);

        Assert.Equal("real\n", WordprocessingFixtures.Extract(package));
    }

    [Fact]
    public void Extract_RelatedCommentsHeaderAndFooterParts_AreNeverResolvedOrParsed()
    {
        string commentText = $"<w:comments {WordprocessingFixtures.Namespaces()}><w:comment w:id=\"1\" w:author=\"x\"><w:p><w:r><w:t>COMMENT_TEXT</w:t></w:r></w:p></w:comment></w:comments>";

        string headerText = $"<w:hdr {WordprocessingFixtures.Namespaces()}><w:p><w:r><w:t>HEADER_TEXT</w:t></w:r></w:p></w:hdr>";

        string footerText = $"<w:ftr {WordprocessingFixtures.Namespaces()}><w:p><w:r><w:t>FOOTER_TEXT</w:t></w:r></w:p></w:ftr>";

        byte[] package = WordprocessingFixtures.Package(
            WordprocessingFixtures.Document(WordprocessingFixtures.P("body") + "<w:sectPr><w:headerReference w:type=\"default\" r:id=\"rIdH\"/><w:footerReference w:type=\"default\" r:id=\"rIdF\"/></w:sectPr>"),
            documentRelationships:
            [
                WordprocessingFixtures.Relationship("rIdC", WordprocessingFixtures.CommentsType, "comments.xml"),
                WordprocessingFixtures.Relationship("rIdH", WordprocessingFixtures.HeaderType, "header1.xml"),
                WordprocessingFixtures.Relationship("rIdF", WordprocessingFixtures.FooterType, "footer1.xml"),
                WordprocessingFixtures.Relationship("rIdMissing", WordprocessingFixtures.CommentsType, "missing-comments.xml"),
            ],
            extraEntries:
            [
                ("word/comments.xml", commentText),
                ("word/header1.xml", headerText),
                ("word/footer1.xml", footerText),
            ]);

        Assert.Equal("body\n", WordprocessingFixtures.Extract(package));
    }

    [Fact]
    public void Extract_RelatedCommentsPartThatIsMalformed_DoesNotFailExtraction()
    {
        byte[] package = WordprocessingFixtures.Package(
            WordprocessingFixtures.Document(WordprocessingFixtures.P("body")),
            documentRelationships: [WordprocessingFixtures.Relationship("rIdC", WordprocessingFixtures.CommentsType, "comments.xml")],
            extraEntries: [("word/comments.xml", "<<< malformed")]);

        Assert.Equal("body\n", WordprocessingFixtures.Extract(package));
    }

    [Fact]
    public void Extract_ExternalStylesAndNotesRelationships_AreNeverFollowed()
    {
        byte[] package = WordprocessingFixtures.Package(
            WordprocessingFixtures.Document(WordprocessingFixtures.P("body")),
            documentRelationships:
            [
                WordprocessingFixtures.Relationship("rIdS", WordprocessingFixtures.StylesType, "http://evil.example/styles.xml", external: true),
                WordprocessingFixtures.Relationship("rIdN", WordprocessingFixtures.FootnotesType, "../../outside/footnotes.xml", external: true),
                WordprocessingFixtures.Relationship("rIdE", WordprocessingFixtures.EndnotesType, "\\\\server\\share\\endnotes.xml", external: true),
            ]);

        Assert.Equal("body\n", WordprocessingFixtures.Extract(package));
    }

    [Fact]
    public void Extract_RelationshipTargetEscapingThePackageRoot_IsUnsafeEntryNameWithoutEchoingIt()
    {
        byte[] package = WordprocessingFixtures.Package(
            WordprocessingFixtures.Document(WordprocessingFixtures.P("body")),
            documentRelationships: [WordprocessingFixtures.Relationship("rIdC", WordprocessingFixtures.CommentsType, "../../escape-secret.xml")]);

        DocumentPackageRejectedException exception = Assert.Throws<DocumentPackageRejectedException>(() => WordprocessingFixtures.Extract(package));

        Assert.Equal(DocumentPackageRejection.UnsafeEntryName, exception.Rejection);

        Assert.DoesNotContain("escape-secret", exception.Message);
    }

    [Fact]
    public void Extract_MacroEnabledPackage_ReadsTextAndNeverInflatesTheMacroProject()
    {
        // vbaProject.bin and an embedding are related to the document but hold deflate garbage and declare
        // bomb-sized lengths; reading either would fail, so success proves neither is ever inflated.
        byte[] garbage = [0xFF, 0xFE, 0xFD, 0xFC, 0xFB, 0xFA, 0xF9, 0xF8, 0xF7, 0xF6, 0xF5, 0xF4];

        RawZipEntry macros = RawZipEntry.Deflated("word/vbaProject.bin", garbage);

        macros.DataAreaBytes = garbage;

        macros.UncompressedSize = 30u * 1024 * 1024;

        macros.CompressedSize = (uint)garbage.Length;

        RawZipEntry embedding = RawZipEntry.Deflated("word/embeddings/oleObject1.bin", garbage);

        embedding.DataAreaBytes = garbage;

        embedding.UncompressedSize = 31u * 1024 * 1024;

        embedding.CompressedSize = (uint)garbage.Length;

        RawZipPackage package = RawZipPackage.Of(
            RawZipEntry.Deflated("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>"),
            RawZipEntry.Deflated("_rels/.rels", WordprocessingFixtures.Relationships(WordprocessingFixtures.Relationship("rId1", WordprocessingFixtures.OfficeDocumentType, "word/document.xml"))),
            RawZipEntry.Deflated("word/document.xml", WordprocessingFixtures.Document(WordprocessingFixtures.P("macro text"))),
            RawZipEntry.Deflated(
                "word/_rels/document.xml.rels",
                WordprocessingFixtures.Relationships(
                    WordprocessingFixtures.Relationship("rIdVba", WordprocessingFixtures.VbaProjectType, "vbaProject.bin"),
                    WordprocessingFixtures.Relationship("rIdOle", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/oleObject", "embeddings/oleObject1.bin"))),
            macros,
            embedding);

        Assert.Equal("macro text\n", WordprocessingFixtures.Extract(package.ToBytes()));
    }

    [Fact]
    public void Extract_ReadingTheGarbageEntryDirectly_WouldHaveFailed()
    {
        // Control for the macro test above: the same entry, if read, is rejected, so the macro test is not vacuous.
        byte[] garbage = [0xFF, 0xFE, 0xFD, 0xFC, 0xFB, 0xFA, 0xF9, 0xF8, 0xF7, 0xF6, 0xF5, 0xF4];

        RawZipEntry macros = RawZipEntry.Deflated("word/vbaProject.bin", garbage);

        macros.DataAreaBytes = garbage;

        macros.UncompressedSize = 30u * 1024 * 1024;

        macros.CompressedSize = (uint)garbage.Length;

        BoundedZipPackage package = BoundedZipPackage.Open(RawZipPackage.Of(macros).ToBytes(), CancellationToken.None);

        Assert.Throws<DocumentPackageRejectedException>(() => package.ReadEntry("word/vbaProject.bin", CancellationToken.None));
    }

    [Fact]
    public void Extract_TextBoxesNestedAsDeeplyAsTheBoundAllows_AreProjectedWithoutExhaustingTheStack()
    {
        // Each level is a paragraph, a run, a picture, a shape, a text box and a content element: six elements deep.
        const int levels = 40;

        string open = "<w:p><w:r><w:pict><v:shape><v:textbox><w:txbxContent>";

        string close = "</w:txbxContent></v:textbox></v:shape></w:pict></w:r></w:p>";

        string nested = string.Concat(Enumerable.Repeat(open, levels)) + WordprocessingFixtures.P("innermost") + string.Concat(Enumerable.Repeat(close, levels));

        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(nested));

        Assert.Equal("innermost\n" + string.Concat(Enumerable.Repeat("\n", levels)), text);
    }

    [Fact]
    public void Extract_TextBoxesNestedBeyondTheDepthBound_IsStructureBounds()
    {
        string open = "<w:p><w:r><w:pict><v:shape><v:textbox><w:txbxContent>";

        string close = "</w:txbxContent></v:textbox></v:shape></w:pict></w:r></w:p>";

        string nested = string.Concat(Enumerable.Repeat(open, 60)) + WordprocessingFixtures.P("innermost") + string.Concat(Enumerable.Repeat(close, 60));

        Assert.Equal(DocumentPackageRejection.StructureBounds, WordprocessingFixtures.Rejection(WordprocessingFixtures.Docx(nested)));
    }

    // ---------------------------------------------------------------- ambiguous relationships

    [Fact]
    public void Extract_TwoOfficeDocumentRelationships_IsFormatMismatch()
    {
        // Which main part a consumer would pick is not defined, so the package is refused rather than guessed at.
        byte[] package = ComprehensionPackageBuilder.Build(
            ("_rels/.rels", WordprocessingFixtures.Relationships(
                WordprocessingFixtures.Relationship("rId1", WordprocessingFixtures.OfficeDocumentType, "word/document.xml"),
                WordprocessingFixtures.Relationship("rId2", WordprocessingFixtures.StrictOfficeDocumentType, "word/other.xml"))),
            ("word/document.xml", WordprocessingFixtures.Document(WordprocessingFixtures.P("one"))),
            ("word/other.xml", WordprocessingFixtures.Document(WordprocessingFixtures.P("two"))));

        Assert.Equal(DocumentPackageRejection.FormatMismatch, WordprocessingFixtures.Rejection(package));
    }

    [Theory]
    [InlineData("styles")]
    [InlineData("footnotes")]
    [InlineData("endnotes")]
    public void Extract_TwoRelationshipsOfTheSameRelatedPartType_IsFormatMismatch(string kind)
    {
        string type = $"http://schemas.openxmlformats.org/officeDocument/2006/relationships/{kind}";

        // Both targets are well-formed parts of the right kind, so only the ambiguity can refuse the package.
        string part = kind switch
        {
            "styles" => WordprocessingFixtures.Styles(string.Empty),
            "footnotes" => WordprocessingFixtures.Footnotes(string.Empty),
            _ => WordprocessingFixtures.Endnotes(string.Empty),
        };

        byte[] package = WordprocessingFixtures.Package(
            WordprocessingFixtures.Document(WordprocessingFixtures.P("x")),
            documentRelationships:
            [
                WordprocessingFixtures.Relationship("rIdA", type, "a.xml"),
                WordprocessingFixtures.Relationship("rIdB", type, "b.xml"),
            ],
            extraEntries: [("word/a.xml", part), ("word/b.xml", part)]);

        Assert.Equal(DocumentPackageRejection.FormatMismatch, WordprocessingFixtures.Rejection(package));
    }

    [Fact]
    public void Extract_AnExternalRelationshipBesideAnInternalOneOfTheSameType_IsNotAnAmbiguity()
    {
        byte[] package = WordprocessingFixtures.Package(
            WordprocessingFixtures.Document("<w:p>" + WordprocessingFixtures.R("HIDDEN_BY_INTERNAL_STYLES", "<w:rStyle w:val=\"Hidden\"/>") + "</w:p>"),
            stylesXml: WordprocessingFixtures.Styles("<w:style w:type=\"character\" w:styleId=\"Hidden\"><w:rPr><w:vanish/></w:rPr></w:style>"),
            documentRelationships: [WordprocessingFixtures.Relationship("rIdX", WordprocessingFixtures.StylesType, "http://evil.example/styles.xml", external: true)]);

        Assert.Equal("\n", WordprocessingFixtures.Extract(package));
    }

    // ---------------------------------------------------------------- parts that are related but not read

    [Fact]
    public void Extract_EveryOtherRelatedPart_IsNeverOpened()
    {
        string[] types =
        [
            "numbering", "settings", "webSettings", "fontTable", "theme", "customXml", "glossaryDocument", "chart", "diagramData",
            "diagramLayout", "diagramColors", "diagramQuickStyle", "image", "oleObject", "package", "people", "commentsExtended", "footer", "header",
        ];

        List<string> relationships = [];

        List<(string Name, string Content)> parts = [];

        for (int index = 0; index < types.Length; index++)
        {
            relationships.Add(WordprocessingFixtures.Relationship($"rIdPart{index}", $"http://schemas.openxmlformats.org/officeDocument/2006/relationships/{types[index]}", $"parts/{types[index]}.xml"));

            parts.Add(($"word/parts/{types[index]}.xml", "<<< malformed, and never to be opened"));
        }

        byte[] package = WordprocessingFixtures.Package(
            WordprocessingFixtures.Document(WordprocessingFixtures.P("body")),
            documentRelationships: [.. relationships],
            extraEntries: [.. parts]);

        Assert.Equal("body\n", WordprocessingFixtures.Extract(package));
    }

    // ---------------------------------------------------------------- output is text only

    [Fact]
    public void Extract_AttributeValuesAndPartNames_NeverReachTheOutput()
    {
        const string marker = "LEAKED_MARKER";

        string body =
            $"<w:p w:rsidR=\"{marker}\"><w:pPr><w:pStyle w:val=\"{marker}\"/></w:pPr><w:bookmarkStart w:id=\"1\" w:name=\"{marker}\"/>"
            + $"<w:hyperlink r:id=\"{marker}\" w:anchor=\"{marker}\" w:tooltip=\"{marker}\"><w:r w:rsidR=\"{marker}\"><w:rPr><w:rFonts w:ascii=\"{marker}\"/><w:lang w:val=\"{marker}\"/></w:rPr><w:t>link</w:t></w:r></w:hyperlink>"
            + $"<w:fldSimple w:instr=\" {marker} \"><w:r><w:t>field</w:t></w:r></w:fldSimple>"
            + $"<w:r><w:drawing><wp:inline><wp:extent cx=\"1\" cy=\"1\"/><wp:docPr id=\"1\" name=\"{marker}\" descr=\"{marker}\" title=\"{marker}\"/><a:graphic><a:graphicData uri=\"{marker}\"><wps:wsp><wps:txbx><w:txbxContent>"
            + WordprocessingFixtures.P("box") + "</w:txbxContent></wps:txbx></wps:wsp></a:graphicData></a:graphic></wp:inline></w:drawing></w:r>"
            + $"<w:r><w:pict><v:shape alt=\"{marker}\" title=\"{marker}\" o:spid=\"{marker}\" xmlns:o=\"urn:schemas-microsoft-com:office:office\"><v:textbox><w:txbxContent>" + WordprocessingFixtures.P("vml") + "</w:txbxContent></v:textbox></v:shape></w:pict></w:r>"
            + $"</w:p><w:sdt><w:sdtPr><w:alias w:val=\"{marker}\"/><w:tag w:val=\"{marker}\"/></w:sdtPr><w:sdtContent>" + WordprocessingFixtures.P("control") + "</w:sdtContent></w:sdt>";

        byte[] package = ComprehensionPackageBuilder.Build(
            ("_rels/.rels", WordprocessingFixtures.Relationships(WordprocessingFixtures.Relationship("rId1", WordprocessingFixtures.OfficeDocumentType, "word/LEAKED_MARKER-main.xml"))),
            ("word/LEAKED_MARKER-main.xml", WordprocessingFixtures.Document(body)));

        string text = WordprocessingFixtures.Extract(package);

        Assert.Equal("linkfieldbox\nvml\n\ncontrol\n", text);

        Assert.DoesNotContain(marker, text);
    }

    // ---------------------------------------------------------------- character encodings

    [Fact]
    public void Extract_Utf16MainPart_IsDecoded()
    {
        string xml = WordprocessingFixtures.Document(WordprocessingFixtures.P("utf sixteen \u00E9\U0001F600")).Replace("encoding=\"UTF-8\"", "encoding=\"UTF-16\"");

        byte[] utf16 = [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(xml)];

        RawZipPackage raw = RawZipPackage.Of(
            RawZipEntry.Deflated("_rels/.rels", WordprocessingFixtures.Relationships(WordprocessingFixtures.Relationship("rId1", WordprocessingFixtures.OfficeDocumentType, "word/document.xml"))),
            RawZipEntry.Deflated("word/document.xml", utf16));

        Assert.Equal("utf sixteen \u00E9\U0001F600\n", WordprocessingFixtures.Extract(raw.ToBytes()));
    }

    [Fact]
    public void Extract_UnsupportedEncodingDeclaration_IsMalformedXml()
    {
        string xml = WordprocessingFixtures.Document(WordprocessingFixtures.P("x")).Replace("encoding=\"UTF-8\"", "encoding=\"no-such-encoding\"");

        Assert.Equal(DocumentPackageRejection.MalformedXml, WordprocessingFixtures.Rejection(WordprocessingFixtures.Package(xml)));
    }

    [Fact]
    public void Extract_InvalidCharacterReference_IsMalformedXml()
    {
        Assert.Equal(
            DocumentPackageRejection.MalformedXml,
            WordprocessingFixtures.Rejection(WordprocessingFixtures.Docx("<w:p><w:r><w:t>nul &#0; and &#1; here</w:t></w:r></w:p>")));
    }

    // ---------------------------------------------------------------- stopping and cancelling

    [Fact]
    public void Extract_SinkThatRefusesTheFirstAppend_IsNotAppendedToAgainAndLaterPartsAreNeverRead()
    {
        // The footnotes part is malformed: reading it after the sink stopped would throw.
        byte[] package = WordprocessingFixtures.Package(
            WordprocessingFixtures.Document(WordprocessingFixtures.P("one") + WordprocessingFixtures.P("two") + WordprocessingFixtures.P("three")),
            footnotesXml: "<<< malformed");

        WordprocessingRecordingSink sink = new((_, _) => false);

        WordprocessingFixtures.Run(package, sink, CancellationToken.None);

        Assert.Equal(1, sink.Appends);

        Assert.Empty(sink.Text);
    }

    [Fact]
    public void Extract_SinkThatRefusesAfterSomeText_StopsAtOnce()
    {
        string body = string.Concat(Enumerable.Range(0, 50).Select(index => WordprocessingFixtures.P($"para{index}")));

        WordprocessingRecordingSink sink = new((appends, _) => appends <= 3);

        WordprocessingFixtures.Run(WordprocessingFixtures.Docx(body), sink, CancellationToken.None);

        Assert.Equal(4, sink.Appends);

        Assert.Equal("para0\npara1", sink.Text);
    }

    [Fact]
    public void Extract_OutputCeiling_TruncatesAndReportsIt()
    {
        SessionAttachmentExtractionResult result = WordprocessingFixtures.ExtractResult(
            WordprocessingFixtures.Docx(WordprocessingFixtures.P("Hello World")), maximumCharacters: 5);

        Assert.Equal("Hello", result.Text);

        Assert.True(result.WasTruncated);
    }

    [Fact]
    public void Extract_AlreadyCancelledToken_Throws()
    {
        using CancellationTokenSource cancellation = new();

        cancellation.Cancel();

        BoundedDocumentTextSink sink = new(1024, CancellationToken.None);

        Assert.ThrowsAny<OperationCanceledException>(
            () => WordprocessingFixtures.Run(WordprocessingFixtures.Docx(WordprocessingFixtures.P("x")), sink, cancellation.Token));
    }

    [Fact]
    public void Extract_CancellationDuringALargeBody_IsObservedWhileWalking()
    {
        // A sink that cancels on its first append; the body has far more than 1,024 nodes still to read.
        string body = string.Concat(Enumerable.Range(0, 5000).Select(index => WordprocessingFixtures.P("p" + index)));

        using CancellationTokenSource cancellation = new();

        WordprocessingRecordingSink sink = new((_, _) =>
        {
            cancellation.Cancel();

            return true;
        });

        Assert.ThrowsAny<OperationCanceledException>(
            () => WordprocessingFixtures.Run(WordprocessingFixtures.Docx(body), sink, cancellation.Token));

        Assert.True(sink.Appends < 5000);
    }

    [Fact]
    public void Extract_CancellationBetweenParts_IsObservedBeforeTheNotesAreRead()
    {
        byte[] package = WordprocessingFixtures.Package(
            WordprocessingFixtures.Document(WordprocessingFixtures.P("body")),
            footnotesXml: WordprocessingFixtures.Footnotes("<w:footnote w:id=\"1\">" + WordprocessingFixtures.P("note") + "</w:footnote>"));

        using CancellationTokenSource cancellation = new();

        WordprocessingRecordingSink sink = new((_, text) =>
        {
            if (text == "\n")
            {
                cancellation.Cancel();
            }

            return true;
        });

        Assert.ThrowsAny<OperationCanceledException>(
            () => WordprocessingFixtures.Run(package, sink, cancellation.Token));

        Assert.DoesNotContain("note", sink.Text);
    }

    [Fact]
    public void Extract_CancellationAfterTheBody_IsObservedEvenWhenNoPartFollows()
    {
        // Sensitivity: the explicit check between the body and the notes. With no notes part related, no part read would
        // ever notice the cancellation, and a small body has too few nodes for the reader's own periodic check.
        using CancellationTokenSource cancellation = new();

        WordprocessingRecordingSink sink = new((_, text) =>
        {
            if (text == "\n")
            {
                cancellation.Cancel();
            }

            return true;
        });

        Assert.ThrowsAny<OperationCanceledException>(
            () => WordprocessingFixtures.Run(WordprocessingFixtures.Docx(WordprocessingFixtures.P("body")), sink, cancellation.Token));
    }
}
