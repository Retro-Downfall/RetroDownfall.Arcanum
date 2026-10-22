using System.Text;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Weave;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension;

/// <summary>Shared helpers for the Wordprocessing extractor tests: package fixtures and extraction drivers.</summary>
internal static class WordprocessingFixtures
{
    public const string WordNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    public const string StrictWordNamespace = "http://purl.oclc.org/ooxml/wordprocessingml/main";

    public const string OfficeDocumentType = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument";

    public const string StrictOfficeDocumentType = "http://purl.oclc.org/ooxml/officeDocument/relationships/officeDocument";

    public const string StylesType = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles";

    public const string FootnotesType = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/footnotes";

    public const string EndnotesType = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/endnotes";

    public const string CommentsType = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/comments";

    public const string HeaderType = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/header";

    public const string FooterType = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/footer";

    public const string VbaProjectType = "http://schemas.microsoft.com/office/2006/relationships/vbaProject";

    public static string Namespaces(string wordNamespace = WordNamespace) =>
        $"xmlns:w=\"{wordNamespace}\" "
        + "xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\" "
        + "xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" "
        + "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" "
        + "xmlns:wps=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\" "
        + "xmlns:wpg=\"http://schemas.microsoft.com/office/word/2010/wordprocessingGroup\" "
        + "xmlns:wpc=\"http://schemas.microsoft.com/office/word/2010/wordprocessingCanvas\" "
        + "xmlns:wp14=\"http://schemas.microsoft.com/office/word/2010/wordprocessingDrawing\" "
        + "xmlns:w14=\"http://schemas.microsoft.com/office/word/2010/wordml\" "
        + "xmlns:w15=\"http://schemas.microsoft.com/office/word/2012/wordml\" "
        + "xmlns:v=\"urn:schemas-microsoft-com:vml\" "
        + "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";

    /// <summary>A WordprocessingML main part whose body holds <paramref name="bodyContent"/>.</summary>
    public static string Document(string bodyContent, string wordNamespace = WordNamespace) =>
        $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document {Namespaces(wordNamespace)}><w:body>{bodyContent}</w:body></w:document>";

    public static string Styles(string styleContent, string wordNamespace = WordNamespace) =>
        $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:styles {Namespaces(wordNamespace)}>{styleContent}</w:styles>";

    public static string Footnotes(string noteContent) =>
        $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:footnotes {Namespaces()}>{noteContent}</w:footnotes>";

    public static string Endnotes(string noteContent) =>
        $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:endnotes {Namespaces()}>{noteContent}</w:endnotes>";

    /// <summary>A paragraph holding one plain run.</summary>
    public static string P(string text) => $"<w:p><w:r><w:t>{text}</w:t></w:r></w:p>";

    /// <summary>A run with optional run properties.</summary>
    public static string R(string text, string? properties = null) =>
        $"<w:r>{(properties is null ? string.Empty : $"<w:rPr>{properties}</w:rPr>")}<w:t>{text}</w:t></w:r>";

    public static string Relationships(params string[] relationships) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
        + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
        + string.Concat(relationships)
        + "</Relationships>";

    public static string Relationship(string id, string type, string target, bool external = false) =>
        $"<Relationship Id=\"{id}\" Type=\"{type}\" Target=\"{target}\"{(external ? " TargetMode=\"External\"" : string.Empty)}/>";

    /// <summary>
    /// A valid <c>.docx</c> package: root relationships to <c>word/document.xml</c>, and a document relationships
    /// part that points at whichever optional parts are supplied.
    /// </summary>
    public static byte[] Package(
        string documentXml,
        string? stylesXml = null,
        string? footnotesXml = null,
        string? endnotesXml = null,
        string[]? documentRelationships = null,
        (string Name, string Content)[]? extraEntries = null,
        string officeDocumentType = OfficeDocumentType)
    {
        List<(string Name, string Content)> entries =
        [
            ("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>"),
            ("_rels/.rels", Relationships(Relationship("rId1", officeDocumentType, "word/document.xml"))),
            ("word/document.xml", documentXml),
        ];

        List<string> relationships = [];

        if (stylesXml is not null)
        {
            entries.Add(("word/styles.xml", stylesXml));

            relationships.Add(Relationship("rIdStyles", StylesType, "styles.xml"));
        }

        if (footnotesXml is not null)
        {
            entries.Add(("word/footnotes.xml", footnotesXml));

            relationships.Add(Relationship("rIdFootnotes", FootnotesType, "footnotes.xml"));
        }

        if (endnotesXml is not null)
        {
            entries.Add(("word/endnotes.xml", endnotesXml));

            relationships.Add(Relationship("rIdEndnotes", EndnotesType, "endnotes.xml"));
        }

        if (documentRelationships is not null)
        {
            relationships.AddRange(documentRelationships);
        }

        if (relationships.Count > 0)
        {
            entries.Add(("word/_rels/document.xml.rels", Relationships([.. relationships])));
        }

        if (extraEntries is not null)
        {
            entries.AddRange(extraEntries);
        }

        return ComprehensionPackageBuilder.Build([.. entries]);
    }

    /// <summary>A valid <c>.docx</c> package holding only <paramref name="bodyContent"/>.</summary>
    public static byte[] Docx(string bodyContent) => Package(Document(bodyContent));

    /// <summary>Runs the extractor over <paramref name="package"/> into a real sink and returns the projected text.</summary>
    public static string Extract(byte[] package, int maximumCharacters = 262_144) => ExtractResult(package, maximumCharacters).Text;

    public static SessionAttachmentExtractionResult ExtractResult(byte[] package, int maximumCharacters = 262_144)
    {
        BoundedDocumentTextSink sink = new(maximumCharacters, CancellationToken.None);

        ValueTask extraction = new WordprocessingDocumentExtractor().ExtractAsync(package, sink, CancellationToken.None);

        Assert.True(extraction.IsCompletedSuccessfully, "The extractor does its work synchronously.");

        return sink.Complete();
    }

    /// <summary>Runs the extractor into <paramref name="sink"/>; the work is synchronous, so a failure surfaces here.</summary>
    public static void Run(byte[] package, ISessionAttachmentDocumentTextSink sink, CancellationToken cancellationToken)
    {
        ValueTask extraction = new WordprocessingDocumentExtractor().ExtractAsync(package, sink, cancellationToken);

        Assert.True(extraction.IsCompletedSuccessfully, "The extractor does its work synchronously.");
    }

    /// <summary>The closed rejection the extractor raises for <paramref name="package"/>.</summary>
    public static DocumentPackageRejection Rejection(byte[] package)
    {
        DocumentPackageRejectedException exception = Assert.Throws<DocumentPackageRejectedException>(() => Extract(package));

        return exception.Rejection;
    }
}

/// <summary>A sink that records appends and cancels or refuses on demand.</summary>
internal sealed class WordprocessingRecordingSink(Func<int, string, bool>? onAppend = null) : ISessionAttachmentDocumentTextSink
{
    private readonly StringBuilder _text = new();

    public int Appends { get; private set; }

    public bool Truncated { get; private set; }

    public string Text => _text.ToString();

    public bool TryAppend(ReadOnlySpan<char> text)
    {
        Appends++;

        string appended = text.ToString();

        bool accepted = onAppend?.Invoke(Appends, appended) ?? true;

        if (accepted)
        {
            _text.Append(appended);
        }

        return accepted;
    }

    public void MarkTruncated() => Truncated = true;
}

public sealed class WordprocessingDocumentExtractorTests
{
    // ---------------------------------------------------------------- text projection (D9)

    [Fact]
    public void Extract_Paragraphs_EndWithANewlineAndRunsConcatenate()
    {
        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            "<w:p><w:r><w:t>Hel</w:t></w:r><w:r><w:t>lo</w:t></w:r></w:p>" + WordprocessingFixtures.P("World")));

        Assert.Equal("Hello\nWorld\n", text);
    }

    [Fact]
    public void Extract_EmptyParagraph_IsAnEmptyLine()
    {
        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            WordprocessingFixtures.P("a") + "<w:p/>" + WordprocessingFixtures.P("b")));

        Assert.Equal("a\n\nb\n", text);
    }

    [Fact]
    public void Extract_TabAndBreakElementsInARun_BecomeTabsAndNewlines()
    {
        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            "<w:p><w:r><w:t>a</w:t><w:tab/><w:t>b</w:t><w:br/><w:t>c</w:t><w:cr/><w:t>d</w:t><w:br w:type=\"page\"/><w:t>e</w:t></w:r></w:p>"));

        Assert.Equal("a\tb\nc\nd\ne\n", text);
    }

    [Fact]
    public void Extract_NoBreakHyphenIsAHyphenWhileSoftHyphenAndSymbolEmitNothing()
    {
        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            "<w:p><w:r><w:t>non</w:t><w:noBreakHyphen/><w:t>break</w:t><w:softHyphen/><w:t>ing</w:t><w:sym w:font=\"Wingdings\" w:char=\"F0FC\"/></w:r></w:p>"));

        Assert.Equal("non-breaking\n", text);
    }

    [Fact]
    public void Extract_WhitespaceOnlyText_IsSignificant()
    {
        // Sensitivity: emitting Whitespace and SignificantWhitespace nodes inside w:t.
        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            "<w:p><w:r><w:t>a</w:t></w:r><w:r><w:t> </w:t></w:r><w:r><w:t>b</w:t></w:r><w:r><w:t xml:space=\"preserve\">  </w:t></w:r><w:r><w:t>c</w:t></w:r></w:p>"));

        Assert.Equal("a b  c\n", text);
    }

    [Fact]
    public void Extract_TabStopDefinitionInParagraphProperties_IsNotATab()
    {
        // Sensitivity: tabs are emitted only for a w:tab that is run content, never for w:pPr/w:tabs definitions.
        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            "<w:p><w:pPr><w:tabs><w:tab w:val=\"left\" w:pos=\"720\"/><w:tab w:val=\"right\" w:pos=\"9000\"/></w:tabs></w:pPr><w:r><w:t>x</w:t></w:r></w:p>"));

        Assert.Equal("x\n", text);
    }

    [Fact]
    public void Extract_CharacterDataOutsideTextElements_IsNeverEmitted()
    {
        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            "<w:p>stray paragraph text<w:r>stray run text<w:t>kept</w:t>more stray</w:r></w:p>"));

        Assert.Equal("kept\n", text);
    }

    [Fact]
    public void Extract_EntitiesCharacterReferencesAndCdata_AreDecoded()
    {
        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            "<w:p><w:r><w:t>a &amp; b &lt;c&gt; &#169; &#x263A; <![CDATA[<d> & e]]></w:t></w:r></w:p>"));

        Assert.Equal("a & b <c> © ☺ <d> & e\n", text);
    }

    [Fact]
    public void Extract_ElementNestedInsideATextElement_IsSkippedWithItsSubtree()
    {
        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            "<w:p><w:r><w:t>a<w:foo>NESTED<w:t>DEEPER</w:t></w:foo>b</w:t></w:r></w:p>"));

        Assert.Equal("ab\n", text);
    }

    [Fact]
    public void Extract_EmptyTextElement_EmitsNothing()
    {
        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            "<w:p><w:r><w:t/></w:r><w:r><w:t></w:t></w:r><w:r><w:t>x</w:t></w:r></w:p>"));

        Assert.Equal("x\n", text);
    }

    [Fact]
    public void Extract_NonBmpAndAccentedText_RoundTrips()
    {
        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            WordprocessingFixtures.P("café naïve \U0001F600 日本")));

        Assert.Equal("café naïve \U0001F600 日本\n", text);
    }

    // ---------------------------------------------------------------- stopping

    public static TheoryData<string, string> StopPoints() =>
        new()
        {
            { "text", "<w:p><w:r><w:t>aaaaaaaa<<" },
            { "body paragraph", "<w:p><w:r><w:t>aaaaaaaa<<" },
            { "table cell", "<w:tbl><w:tr><w:tc><w:p><w:r><w:t>aaaaaaaa<<" },
            { "alternate content", "<w:p><mc:AlternateContent><mc:Choice Requires=\"w14\"><w:r><w:t>aaaaaaaa<<" },
            { "drawing", "<w:p><w:r><w:drawing><wp:inline><wp:docPr id=\"1\" name=\"b\"/><a:graphic><a:graphicData><wps:wsp><wps:txbx><w:txbxContent><w:p><w:r><w:t>aaaaaaaa<<" },
            { "vml text box", "<w:p><w:r><w:pict><v:shape><v:textbox><w:txbxContent><w:p><w:r><w:t>aaaaaaaa<<" },
            { "hyperlink", "<w:p><w:hyperlink><w:r><w:t>aaaaaaaa<<" },
            { "content control", "<w:sdt><w:sdtContent><w:p><w:r><w:t>aaaaaaaa<<" },
        };

    [Theory]
    [MemberData(nameof(StopPoints))]
    public void Extract_OutputCeilingReached_ReadsNothingFurther(string location, string unfinishedBody)
    {
        // Sensitivity: every walk loop honours Stopped. The text after the stop point is not even well-formed, so reading
        // one node more would fail the extraction instead of truncating it.
        string main = $"<w:document {WordprocessingFixtures.Namespaces()}><w:body>{unfinishedBody}";

        SessionAttachmentExtractionResult result = WordprocessingFixtures.ExtractResult(WordprocessingFixtures.Package(main), maximumCharacters: 4);

        Assert.Equal("aaaa", result.Text);

        Assert.True(result.WasTruncated, location);
    }

    [Theory]
    [InlineData("<w:tab/>")]
    [InlineData("<w:br/>")]
    [InlineData("<w:cr/>")]
    [InlineData("<w:noBreakHyphen/>")]
    public void Extract_OutputCeilingReachedByARunCharacter_ReadsNothingFurther(string runCharacter)
    {
        string main = $"<w:document {WordprocessingFixtures.Namespaces()}><w:body><w:p><w:r><w:t>aaaa</w:t>{runCharacter}<<";

        SessionAttachmentExtractionResult result = WordprocessingFixtures.ExtractResult(WordprocessingFixtures.Package(main), maximumCharacters: 4);

        Assert.Equal("aaaa", result.Text);

        Assert.True(result.WasTruncated);
    }

    [Fact]
    public void Extract_OutputCeilingReachedByAParagraphEnd_ReadsNothingFurther()
    {
        string main = $"<w:document {WordprocessingFixtures.Namespaces()}><w:body><w:p><w:r><w:t>aaaa</w:t></w:r></w:p><w:p/><<";

        SessionAttachmentExtractionResult result = WordprocessingFixtures.ExtractResult(WordprocessingFixtures.Package(main), maximumCharacters: 4);

        Assert.Equal("aaaa", result.Text);

        Assert.True(result.WasTruncated);
    }

    [Fact]
    public void Extract_OutputCeilingReachedInAFootnote_ReadsNothingFurtherInThePart()
    {
        string footnotes = $"<w:footnotes {WordprocessingFixtures.Namespaces()}><w:footnote w:id=\"1\"><w:p><w:r><w:t>aaaaaaaa<<";

        SessionAttachmentExtractionResult result = WordprocessingFixtures.ExtractResult(
            WordprocessingFixtures.Package(WordprocessingFixtures.Document(string.Empty), footnotesXml: footnotes), maximumCharacters: 4);

        Assert.Equal("aaaa", result.Text);

        Assert.True(result.WasTruncated);
    }

    [Fact]
    public void Extract_OutputCeilingReachedInAFootnote_NeverReadsASecondFootnote()
    {
        string footnotes = $"<w:footnotes {WordprocessingFixtures.Namespaces()}><w:footnote w:id=\"1\"><w:p><w:r><w:t>aaaa</w:t></w:r></w:p></w:footnote><w:footnote w:id=\"2\"><w:p><w:r><w:t>b</w:t></w:r></w:p></w:footnote><<";

        SessionAttachmentExtractionResult result = WordprocessingFixtures.ExtractResult(
            WordprocessingFixtures.Package(WordprocessingFixtures.Document(string.Empty), footnotesXml: footnotes), maximumCharacters: 4);

        Assert.Equal("aaaa", result.Text);

        Assert.True(result.WasTruncated);
    }

    // ---------------------------------------------------------------- included structures

    [Fact]
    public void Extract_TableCells_AreProjectedInDocumentOrder()
    {
        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            "<w:tbl><w:tblPr><w:tblW w:w=\"0\" w:type=\"auto\"/></w:tblPr><w:tblGrid><w:gridCol w:w=\"1000\"/><w:gridCol w:w=\"1000\"/></w:tblGrid>"
            + "<w:tr><w:tc><w:tcPr><w:tcW w:w=\"1000\" w:type=\"dxa\"/></w:tcPr>" + WordprocessingFixtures.P("A") + "</w:tc><w:tc>" + WordprocessingFixtures.P("B") + "</w:tc></w:tr>"
            + "<w:tr><w:tc>" + WordprocessingFixtures.P("C") + "</w:tc><w:tc>" + WordprocessingFixtures.P("D") + "</w:tc></w:tr></w:tbl>"
            + WordprocessingFixtures.P("after")));

        Assert.Equal("A\nB\nC\nD\nafter\n", text);
    }

    [Fact]
    public void Extract_NestedTable_IsProjected()
    {
        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            "<w:tbl><w:tr><w:tc>" + WordprocessingFixtures.P("outer")
            + "<w:tbl><w:tr><w:tc>" + WordprocessingFixtures.P("inner") + "</w:tc></w:tr></w:tbl><w:p/></w:tc></w:tr></w:tbl>"));

        Assert.Equal("outer\ninner\n\n", text);
    }

    [Fact]
    public void Extract_HyperlinkRunsAndFieldResults_AreProjected()
    {
        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            "<w:p><w:r><w:t xml:space=\"preserve\">see </w:t></w:r><w:hyperlink r:id=\"rId9\"><w:r><w:t>the link</w:t></w:r></w:hyperlink>"
            + "<w:r><w:t xml:space=\"preserve\"> and </w:t></w:r><w:hyperlink w:anchor=\"bm\" w:history=\"1\"><w:r><w:t>the anchor</w:t></w:r></w:hyperlink></w:p>"));

        Assert.Equal("see the link and the anchor\n", text);
    }

    [Fact]
    public void Extract_StructuredDocumentTagContent_IsProjectedAndItsPropertiesAreNot()
    {
        // Sensitivity: skipping the w:sdtPr subtree (the contrived run inside it would otherwise be emitted).
        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            "<w:sdt><w:sdtPr><w:alias w:val=\"Alias\"/><w:tag w:val=\"tag\"/><w:text/><w:r><w:t>INSDTPR</w:t></w:r></w:sdtPr>"
            + "<w:sdtContent>" + WordprocessingFixtures.P("control text") + "</w:sdtContent></w:sdt>"));

        Assert.Equal("control text\n", text);
    }

    [Fact]
    public void Extract_InlineStructuredDocumentTag_IsProjected()
    {
        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            "<w:p><w:r><w:t xml:space=\"preserve\">before </w:t></w:r><w:sdt><w:sdtPr/><w:sdtContent><w:r><w:t>inside</w:t></w:r></w:sdtContent></w:sdt></w:p>"));

        Assert.Equal("before inside\n", text);
    }

    [Fact]
    public void Extract_InsertedAndMovedToText_IsProjected()
    {
        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            "<w:p><w:ins w:id=\"1\" w:author=\"a\" w:date=\"2026-10-22T10:00:00Z\"><w:r><w:t>inserted</w:t></w:r></w:ins>"
            + "<w:r><w:t xml:space=\"preserve\"> </w:t></w:r>"
            + "<w:moveTo w:id=\"2\" w:author=\"a\" w:date=\"2026-10-22T10:00:00Z\"><w:r><w:t>moved here</w:t></w:r></w:moveTo></w:p>"));

        Assert.Equal("inserted moved here\n", text);
    }

    [Fact]
    public void Extract_VmlTextBoxContent_IsProjected()
    {
        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            "<w:p><w:r><w:pict><v:shape id=\"s1\" type=\"#t\" style=\"position:absolute;width:100pt\"><v:textbox><w:txbxContent>"
            + WordprocessingFixtures.P("vml box") + "</w:txbxContent></v:textbox></v:shape></w:pict></w:r></w:p>"));

        Assert.Equal("vml box\n\n", text);
    }

    [Fact]
    public void Extract_DrawingMlTextBoxContent_IsProjected()
    {
        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            "<w:p><w:r><w:drawing><wp:inline><wp:extent cx=\"100\" cy=\"100\"/><wp:docPr id=\"1\" name=\"Box\" descr=\"ALT_TEXT_DESCRIPTION\"/>"
            + "<a:graphic><a:graphicData uri=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\"><wps:wsp><wps:txbx><w:txbxContent>"
            + WordprocessingFixtures.P("drawing box") + "</w:txbxContent></wps:txbx></wps:wsp></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>"));

        Assert.Equal("drawing box\n\n", text);
    }

    [Fact]
    public void Extract_UnknownWrapperElements_AreTransparent()
    {
        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            "<w:p><w:smartTag w:uri=\"urn:x\" w:element=\"place\"><w:r><w:t>smart</w:t></w:r></w:smartTag>"
            + "<w:customXml w:uri=\"urn:y\" w:element=\"item\"><w:r><w:t> custom</w:t></w:r></w:customXml></w:p>"));

        Assert.Equal("smart custom\n", text);
    }

    // ---------------------------------------------------------------- namespaces

    [Fact]
    public void Extract_DefaultNamespaceAndAnotherPrefix_MatchByNamespaceNotPrefix()
    {
        string defaultNamespace = $"<document xmlns=\"{WordprocessingFixtures.WordNamespace}\"><body><p><r><t>default</t></r></p></body></document>";

        string otherPrefix = $"<x:document xmlns:x=\"{WordprocessingFixtures.WordNamespace}\"><x:body><x:p><x:r><x:t>prefixed</x:t></x:r></x:p></x:body></x:document>";

        Assert.Equal("default\n", WordprocessingFixtures.Extract(WordprocessingFixtures.Package(defaultNamespace)));

        Assert.Equal("prefixed\n", WordprocessingFixtures.Extract(WordprocessingFixtures.Package(otherPrefix)));
    }

    [Fact]
    public void Extract_ForeignNamespaceElementsNamedLikeWordElements_AreNotText()
    {
        // The w:p / w:t local names in a foreign namespace carry no Word meaning.
        string text = WordprocessingFixtures.Extract(WordprocessingFixtures.Docx(
            "<w:p><w:r><w:t>real</w:t></w:r></w:p><f:p xmlns:f=\"urn:foreign\"><f:r><f:t>FOREIGN</f:t></f:r></f:p>"));

        Assert.Equal("real\n", text);
    }

    [Fact]
    public void Extract_StrictNamespaceDocument_IsProjected()
    {
        byte[] package = WordprocessingFixtures.Package(
            WordprocessingFixtures.Document(
                "<w:p><w:r><w:t>strict</w:t><w:tab/><w:t>text</w:t></w:r></w:p>",
                WordprocessingFixtures.StrictWordNamespace),
            officeDocumentType: WordprocessingFixtures.StrictOfficeDocumentType);

        Assert.Equal("strict\ttext\n", WordprocessingFixtures.Extract(package));
    }
}
