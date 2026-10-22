using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Weave;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension;

/// <summary>
/// Projection tests for the Visio extractor (D9): page headers, shape text, text markers and field values.
/// Packages are hand-built per MS-VSDX by <see cref="VisioPackageBuilder"/> because no Visio producer exists on
/// the development machines (see <c>TestData/Comprehension/Visio/README.md</c>).
/// </summary>
public sealed class VisioDrawingExtractorTests
{
    [Fact]
    public void Extract_OneShape_ProjectsPageHeaderThenTheTextLine()
    {
        byte[] package = new VisioPackageBuilder()
            .AddPage("Page-1", VisioFixture.Shape(1, "Hello"))
            .Build();

        Assert.Equal("Page: Page-1\nHello\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_ResultIsExtractedAndNotTruncated()
    {
        byte[] package = new VisioPackageBuilder()
            .AddPage("Page-1", VisioFixture.Shape(1, "Hello"))
            .Build();

        SessionAttachmentExtractionResult result = VisioFixture.Extract(package);

        Assert.Equal(SessionAttachmentExtractionStatus.Extracted, result.Status);

        Assert.False(result.WasTruncated);
    }

    [Fact]
    public void Extract_PagesAreProjectedInPagesXmlOrderNotPartNameOrder()
    {
        // Sensitivity: iterating pages.xml order. Part names are deliberately the reverse of document order.
        byte[] package = new VisioPackageBuilder()
            .AddPage("Second", VisioFixture.Shape(1, "Two"))
            .AddPage("First", VisioFixture.Shape(1, "One"))
            .Reorder(1, 0)
            .Build();

        Assert.Equal("Page: First\nOne\nPage: Second\nTwo\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_PageWithoutNameUsesNameU()
    {
        byte[] package = new VisioPackageBuilder()
            .AddPage(string.Empty, VisioFixture.Shape(1, "Body"), nameU: "Universal")
            .Build();

        Assert.Equal("Page: Universal\nBody\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_NameTakesPrecedenceOverNameU()
    {
        byte[] package = new VisioPackageBuilder()
            .AddPage("Shown", VisioFixture.Shape(1, "Body"), nameU: "Universal")
            .Build();

        Assert.Equal("Page: Shown\nBody\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_PageWithoutShapes_EmitsOnlyItsHeader()
    {
        byte[] package = new VisioPackageBuilder()
            .AddPage("Empty", string.Empty)
            .AddPage("Full", VisioFixture.Shape(1, "Text"))
            .Build();

        Assert.Equal("Page: Empty\nPage: Full\nText\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_BackgroundPageIsIncluded()
    {
        // Documented simplification (D9): background pages are projected like any other page.
        byte[] package = new VisioPackageBuilder()
            .AddPage("Background", VisioFixture.Shape(1, "Header on every page"), pageAttributes: "Background='1'")
            .AddPage("Foreground", VisioFixture.Shape(1, "Body"), pageAttributes: "BackPage='0'")
            .Build();

        Assert.Equal("Page: Background\nHeader on every page\nPage: Foreground\nBody\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_PageNameLineBreaksAreFlattenedSoANameCannotForgeAnotherPageHeader()
    {
        // Sensitivity: the control-character flattening of page names. &#10; survives XML attribute normalization.
        byte[] package = new VisioPackageBuilder()
            .AddPage("Real&#10;Page: Forged&#13;&#9;tail&#x2028;end", VisioFixture.Shape(1, "Body"), rawName: true)
            .Build();

        Assert.Equal("Page: Real Page: Forged  tail end\nBody\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_ShapesWithoutTextEmitNothing()
    {
        byte[] package = new VisioPackageBuilder()
            .AddPage("Page-1", VisioFixture.Shape(1) + VisioFixture.Shape(2, "Kept") + VisioFixture.Shape(3))
            .Build();

        Assert.Equal("Page: Page-1\nKept\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_ShapesProjectInDocumentOrderOneLineEach()
    {
        byte[] package = new VisioPackageBuilder()
            .AddPage("Page-1", VisioFixture.Shape(1, "Alpha") + VisioFixture.Shape(2, "Beta") + VisioFixture.Shape(3, "Gamma"))
            .Build();

        Assert.Equal("Page: Page-1\nAlpha\nBeta\nGamma\n", VisioFixture.Extract(package).Text);
    }

    // ---------------------------------------------------------------- Text content model

    [Fact]
    public void Extract_TextMarkersEmitNothingAndFieldValuesAreEmitted()
    {
        byte[] package = new VisioPackageBuilder()
            .AddPage("Page-1", VisioFixture.Shape(1, "<cp IX='0'/>Total: <fld IX='0' Format='0'>42</fld><pp IX='0'/><tp IX='0'/> units"))
            .Build();

        Assert.Equal("Page: Page-1\nTotal: 42 units\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_LineBreaksInsideTextArePreserved()
    {
        byte[] package = new VisioPackageBuilder()
            .AddPage("Page-1", VisioFixture.Shape(1, "<cp IX='0'/>Line one\nLine two<pp IX='1'/>\nLine three"))
            .Build();

        Assert.Equal("Page: Page-1\nLine one\nLine two\nLine three\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_WhitespaceAroundMarkersAndFieldsIsSignificant()
    {
        byte[] package = new VisioPackageBuilder()
            .AddPage("Page-1", VisioFixture.Shape(1, "  a <fld IX='0'>b</fld> <cp IX='1'/> c  "))
            .Build();

        Assert.Equal("Page: Page-1\n  a b  c  \n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_PredefinedEntitiesCharacterReferencesAndCdataAreDecoded()
    {
        byte[] package = new VisioPackageBuilder()
            .AddPage("Page-1", VisioFixture.Shape(1, "&lt;b&gt; &amp; &#x41;&#66; <![CDATA[x<y & z]]>"))
            .Build();

        Assert.Equal("Page: Page-1\n<b> & AB x<y & z\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_UnrecognizedElementsInsideTextAreSkippedWithTheirContent()
    {
        // Only text nodes and fld values are projected; anything else inside Text is not rendered text.
        byte[] package = new VisioPackageBuilder()
            .AddPage("Page-1", VisioFixture.Shape(1, "a<unknown>SECRET</unknown>b<fld IX='0'>c<nested>NESTED-SECRET</nested>d</fld>e"))
            .Build();

        Assert.Equal("Page: Page-1\nabcde\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_FieldNestedInsideAFieldContributesOnlyTheOuterFieldsOwnText()
    {
        // The format defines no nested fields; the inner one is not rendered text of the outer value, so it is skipped whole.
        byte[] package = new VisioPackageBuilder()
            .AddPage("Page-1", VisioFixture.Shape(1, "a<fld IX='0'>b<fld IX='1'>NESTED-FIELD</fld>c</fld>d"))
            .Build();

        Assert.Equal("Page: Page-1\nabcd\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_FieldElementInAnotherNamespaceIsNotAField()
    {
        byte[] package = new VisioPackageBuilder()
            .AddPage("Page-1", VisioFixture.Shape(1, "a<fld xmlns='urn:other' IX='0'>FOREIGN</fld>b"))
            .Build();

        Assert.Equal("Page: Page-1\nab\n", VisioFixture.Extract(package).Text);
    }

    // ---------------------------------------------------------------- nesting

    [Fact]
    public void Extract_GroupTextThenSubShapeTextInDocumentOrderAtAnyDepth()
    {
        string grandChild = VisioFixture.Shape(4, "Grand");

        string child = VisioFixture.Shape(3, "Child B", children: grandChild);

        string group = VisioFixture.Shape(1, "Group", type: "Group", children: VisioFixture.Shape(2, "Child A") + child) + VisioFixture.Shape(5, "After");

        byte[] package = new VisioPackageBuilder().AddPage("Page-1", group).Build();

        Assert.Equal("Page: Page-1\nGroup\nChild A\nChild B\nGrand\nAfter\n", VisioFixture.Extract(package).Text);
    }

    // ---------------------------------------------------------------- non-rendered content is never projected

    [Fact]
    public void Extract_ShapeDataUserCellsHyperlinksAndForeignDataAreNeverProjected()
    {
        string sections =
            "<Section N='Property'><Row N='Secret'><Cell N='Value' V='SHAPE-DATA-SECRET'/><Cell N='Label' V='LABEL-SECRET'/></Row></Section>"
            + "<Section N='User'><Row N='note'><Cell N='Value' V='USER-CELL-SECRET'/></Row></Section>"
            + "<Section N='Hyperlink'><Row N='Link'><Cell N='Description' V='HYPERLINK-SECRET'/><Cell N='Address' V='http://evil.example/'/></Row></Section>";

        string shape = $"<Shape ID='1' Type='Shape'><Cell N='PinX' V='1'/>{sections}<Text>Visible</Text>"
            + "<Data1>DATA1-SECRET</Data1><Data2>DATA2-SECRET</Data2><Data3>DATA3-SECRET</Data3>"
            + "<ForeignData ForeignType='Bitmap'><Rel r:id='rId9'/>FOREIGN-SECRET</ForeignData></Shape>";

        byte[] package = new VisioPackageBuilder().AddPage("Page-1", shape).Build();

        Assert.Equal("Page: Page-1\nVisible\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_ConnectsAndOtherPageContentChildrenAreNeverProjected()
    {
        byte[] package = new VisioPackageBuilder()
            .AddPage("Page-1", VisioFixture.Shape(1, "Body"), pageContentsExtra: "<Connects><Connect FromSheet='1' ToSheet='1'>CONNECT-SECRET</Connect></Connects><Unexpected>UNEXPECTED-SECRET</Unexpected>")
            .Build();

        Assert.Equal("Page: Page-1\nBody\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_ComponentsOutsideTheReferencedGraphAreNeverProjected()
    {
        // The default package carries windows.xml, comments.xml and docProps/core.xml with markers; none may surface.
        byte[] package = new VisioPackageBuilder()
            .AddPage("Page-1", VisioFixture.Shape(1, "Body"))
            .Build();

        string text = VisioFixture.Extract(package).Text;

        Assert.DoesNotContain("WINDOW-MARKER", text);

        Assert.DoesNotContain("COMMENT-MARKER", text);

        Assert.DoesNotContain("CORE-MARKER", text);

        Assert.Equal("Page: Page-1\nBody\n", text);
    }

    // ---------------------------------------------------------------- element matching is by namespace and local name

    [Fact]
    public void Extract_ElementsAreMatchedByNamespaceAndLocalNameNotByPrefix()
    {
        const string Prefixed =
            "<?xml version='1.0' encoding='utf-8' ?>"
            + $"<v:PageContents xmlns:v='{VisioFixture.Main}'><v:Shapes><v:Shape ID='1' Type='Shape'><v:Text>Prefixed</v:Text></v:Shape></v:Shapes></v:PageContents>";

        byte[] package = new VisioPackageBuilder()
            .AddPage("Page-1", string.Empty)
            .Replace("visio/pages/page1.xml", Prefixed)
            .Build();

        Assert.Equal("Page: Page-1\nPrefixed\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_ShapeElementsInAnotherNamespaceAreIgnoredEvenWithTheVisioPrefix()
    {
        const string Spoofed =
            "<?xml version='1.0' encoding='utf-8' ?>"
            + $"<PageContents xmlns='{VisioFixture.Main}' xmlns:v='urn:not-visio'><Shapes><v:Shape ID='1' Type='Shape'><v:Text>Spoofed</v:Text></v:Shape><Shape ID='2' Type='Shape'><Text>Real</Text></Shape></Shapes></PageContents>";

        byte[] package = new VisioPackageBuilder()
            .AddPage("Page-1", string.Empty)
            .Replace("visio/pages/page1.xml", Spoofed)
            .Build();

        Assert.Equal("Page: Page-1\nReal\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_RelationshipIdAttributeIsMatchedByNamespaceNotByPrefix()
    {
        // The Rel element's r:id uses a non-conventional prefix bound to the relationships namespace.
        string pages =
            "<?xml version='1.0' encoding='utf-8' ?>"
            + $"<Pages xmlns='{VisioFixture.Main}' xmlns:rel='{VisioFixture.Relationships}'><Page ID='0' Name='Odd prefix' NameU='Odd prefix'><PageSheet/><Rel rel:id='rId1'/></Page></Pages>";

        byte[] package = new VisioPackageBuilder()
            .AddPage("Odd prefix", VisioFixture.Shape(1, "Body"))
            .Replace("visio/pages/pages.xml", pages)
            .Build();

        Assert.Equal("Page: Odd prefix\nBody\n", VisioFixture.Extract(package).Text);
    }

    // ---------------------------------------------------------------- extractor contract

    [Fact]
    public async Task Extract_StopsAsSoonAsTheSinkRefusesText()
    {
        byte[] package = new VisioPackageBuilder()
            .AddPage("Page-1", string.Concat(Enumerable.Range(1, 50).Select(index => VisioFixture.Shape(index, $"Line {index}"))))
            .Build();

        BoundedDocumentTextSink inner = new(30, CancellationToken.None);

        RefusalWatchingSink sink = new(inner);

        await new VisioDrawingExtractor().ExtractAsync(package, sink, CancellationToken.None);

        SessionAttachmentExtractionResult result = inner.Complete();

        Assert.True(result.WasTruncated);

        Assert.Equal("Page: Page-1\nLine 1\nLine 2\nLin", result.Text);

        Assert.True(sink.Refused);

        Assert.Equal(0, sink.AppendsAfterRefusal);
    }

    [Theory]
    [InlineData(5, "Page:")]
    [InlineData(8, "Page: Pa")]
    [InlineData(12, "Page: Page-1")]
    public async Task Extract_StopsWhenTheSinkRefusesAPageHeaderInItsPrefixItsNameOrItsLineBreak(int ceiling, string expected)
    {
        // The second page part is malformed: it is never read, because nothing is read once the sink has refused.
        byte[] package = new VisioPackageBuilder()
            .AddPage("Page-1", VisioFixture.Shape(1, "Body"))
            .AddPage("Page-2", string.Empty)
            .Replace("visio/pages/page2.xml", "<broken")
            .Build();

        BoundedDocumentTextSink inner = new(ceiling, CancellationToken.None);

        RefusalWatchingSink sink = new(inner);

        await new VisioDrawingExtractor().ExtractAsync(package, sink, CancellationToken.None);

        SessionAttachmentExtractionResult result = inner.Complete();

        Assert.Equal(expected, result.Text);

        Assert.True(result.WasTruncated);

        Assert.True(sink.Refused);

        Assert.Equal(0, sink.AppendsAfterRefusal);
    }

    [Fact]
    public void Extract_CancelledToken_ThrowsOperationCanceled()
    {
        byte[] package = new VisioPackageBuilder()
            .AddPage("Page-1", VisioFixture.Shape(1, "Hello"))
            .Build();

        using CancellationTokenSource cancellation = new();

        cancellation.Cancel();

        BoundedDocumentTextSink sink = new(262_144, CancellationToken.None);

        Assert.Throws<OperationCanceledException>(() => new VisioDrawingExtractor().ExtractAsync(package, sink, cancellation.Token));
    }

    private sealed class RefusalWatchingSink(ISessionAttachmentDocumentTextSink inner) : ISessionAttachmentDocumentTextSink
    {
        public bool Refused { get; private set; }

        public int AppendsAfterRefusal { get; private set; }

        public bool TryAppend(ReadOnlySpan<char> text)
        {
            if (Refused)
            {
                AppendsAfterRefusal++;
            }

            bool accepted = inner.TryAppend(text);

            Refused |= !accepted;

            return accepted;
        }

        public void MarkTruncated() => inner.MarkTruncated();
    }
}
