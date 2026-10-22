using System.Text;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Weave;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension;

/// <summary>
/// Refusals, graph-walking rules and structural bounds of the Visio extractor: wrong or incomplete packages, malformed
/// XML, external and smuggled parts, and the page, depth and cancellation limits.
/// </summary>
public sealed class VisioDrawingExtractorRejectionTests
{
    // ---------------------------------------------------------------- format mismatch and missing parts

    [Fact]
    public void Extract_WordDocumentFedToTheVisioExtractor_IsMissingPartBecauseThereIsNoVisioDocumentRelationship()
    {
        byte[] docx = ComprehensionPackageBuilder.Build(
            ("[Content_Types].xml", "<Types xmlns='http://schemas.openxmlformats.org/package/2006/content-types'/>"),
            ("_rels/.rels", VisioPackageBuilder.Rels(("rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument", "word/document.xml", false))),
            ("word/document.xml", "<w:document xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main'><w:body><w:p><w:r><w:t>DOCX-TEXT</w:t></w:r></w:p></w:body></w:document>"));

        Assert.Equal(DocumentPackageRejection.MissingPart, VisioFixture.Reject(docx));
    }

    [Fact]
    public void Extract_PackageWithoutAnyRelationshipPart_IsMissingPart()
    {
        byte[] package = ComprehensionPackageBuilder.Build(("readme.txt", "just text"));

        Assert.Equal(DocumentPackageRejection.MissingPart, VisioFixture.Reject(package));
    }

    [Fact]
    public void Extract_ExternalDocumentRelationship_IsNeverFollowedAndIsMissingPart()
    {
        byte[] package = new VisioPackageBuilder()
            .AddPage("Page-1", VisioFixture.Shape(1, "Body"))
            .Replace("_rels/.rels", VisioPackageBuilder.Rels(("rId1", VisioFixture.VisioRelationshipPrefix + "document", "http://evil.example/visio/document.xml", true)))
            .Build();

        Assert.Equal(DocumentPackageRejection.MissingPart, VisioFixture.Reject(package));
    }

    [Fact]
    public void Extract_DocumentRelationshipToAbsentPart_IsMissingPart()
    {
        byte[] package = new VisioPackageBuilder()
            .AddPage("Page-1", VisioFixture.Shape(1, "Body"))
            .Remove("visio/document.xml")
            .Build();

        Assert.Equal(DocumentPackageRejection.MissingPart, VisioFixture.Reject(package));
    }

    [Theory]
    [InlineData("Pages")]
    [InlineData("VisioDocumentX")]
    [InlineData("Document")]
    public void Extract_DocumentPartWithAnotherRootElement_IsFormatMismatch(string root)
    {
        byte[] package = new VisioPackageBuilder()
            .AddPage("Page-1", VisioFixture.Shape(1, "Body"))
            .Replace("visio/document.xml", VisioFixture.Xml(root, string.Empty))
            .Build();

        Assert.Equal(DocumentPackageRejection.FormatMismatch, VisioFixture.Reject(package));
    }

    [Fact]
    public void Extract_DocumentRootInAnotherNamespace_IsFormatMismatch()
    {
        byte[] package = new VisioPackageBuilder()
            .AddPage("Page-1", VisioFixture.Shape(1, "Body"))
            .Replace("visio/document.xml", "<VisioDocument xmlns='urn:not-visio'/>")
            .Build();

        Assert.Equal(DocumentPackageRejection.FormatMismatch, VisioFixture.Reject(package));
    }

    [Fact]
    public void Extract_DocumentWithoutAPagesRelationship_IsMissingPart()
    {
        byte[] package = new VisioPackageBuilder()
            .AddPage("Page-1", VisioFixture.Shape(1, "Body"))
            .Replace("visio/_rels/document.xml.rels", VisioPackageBuilder.Rels(("rId3", VisioFixture.VisioRelationshipPrefix + "windows", "windows.xml", false)))
            .Build();

        Assert.Equal(DocumentPackageRejection.MissingPart, VisioFixture.Reject(package));
    }

    [Fact]
    public void Extract_PagesRelationshipToAbsentPart_IsMissingPart()
    {
        byte[] package = new VisioPackageBuilder()
            .AddPage("Page-1", VisioFixture.Shape(1, "Body"))
            .Remove("visio/pages/pages.xml")
            .Build();

        Assert.Equal(DocumentPackageRejection.MissingPart, VisioFixture.Reject(package));
    }

    [Theory]
    [InlineData("Masters")]
    [InlineData("Page")]
    public void Extract_PagesPartWithAnotherRootElement_IsFormatMismatch(string root)
    {
        byte[] package = new VisioPackageBuilder()
            .AddPage("Page-1", VisioFixture.Shape(1, "Body"))
            .Replace("visio/pages/pages.xml", VisioFixture.Xml(root, string.Empty))
            .Build();

        Assert.Equal(DocumentPackageRejection.FormatMismatch, VisioFixture.Reject(package));
    }

    [Theory]
    [InlineData("MasterContents")]
    [InlineData("Shapes")]
    public void Extract_PagePartWithAnotherRootElement_IsFormatMismatch(string root)
    {
        byte[] package = new VisioPackageBuilder()
            .AddPage("Page-1", VisioFixture.Shape(1, "Body"))
            .Replace("visio/pages/page1.xml", VisioFixture.Xml(root, string.Empty))
            .Build();

        Assert.Equal(DocumentPackageRejection.FormatMismatch, VisioFixture.Reject(package));
    }

    [Fact]
    public void Extract_PageWithoutARelElement_IsMissingPart()
    {
        string pages = VisioFixture.Xml("Pages", "<Page ID='0' Name='Page-1' NameU='Page-1'><PageSheet/></Page>");

        byte[] package = new VisioPackageBuilder()
            .AddPage("Page-1", VisioFixture.Shape(1, "Body"))
            .Replace("visio/pages/pages.xml", pages)
            .Build();

        Assert.Equal(DocumentPackageRejection.MissingPart, VisioFixture.Reject(package));
    }

    [Fact]
    public void Extract_PageRelationshipIdThatIsNotDeclared_IsMissingPart()
    {
        string pages = VisioFixture.Xml("Pages", "<Page ID='0' Name='Page-1' NameU='Page-1'><PageSheet/><Rel r:id='rId99'/></Page>");

        byte[] package = new VisioPackageBuilder()
            .AddPage("Page-1", VisioFixture.Shape(1, "Body"))
            .Replace("visio/pages/pages.xml", pages)
            .Build();

        Assert.Equal(DocumentPackageRejection.MissingPart, VisioFixture.Reject(package));
    }

    [Fact]
    public void Extract_PageRelationshipOfAnotherType_IsMissingPart()
    {
        // Sensitivity: the IsVisioType(type, "page") filter. rId1 exists but is a master relationship.
        byte[] package = new VisioPackageBuilder()
            .AddPage("Page-1", VisioFixture.Shape(1, "Body"))
            .Replace("visio/pages/_rels/pages.xml.rels", VisioPackageBuilder.Rels(("rId1", VisioFixture.VisioRelationshipPrefix + "master", "page1.xml", false)))
            .Build();

        Assert.Equal(DocumentPackageRejection.MissingPart, VisioFixture.Reject(package));
    }

    [Fact]
    public void Extract_PageRelationshipToAbsentPart_IsMissingPart()
    {
        byte[] package = new VisioPackageBuilder()
            .AddPage("Page-1", VisioFixture.Shape(1, "Body"))
            .Remove("visio/pages/page1.xml")
            .Build();

        Assert.Equal(DocumentPackageRejection.MissingPart, VisioFixture.Reject(package));
    }

    [Fact]
    public void Extract_PageRelationshipThatIsExternal_IsNeverFollowedAndIsMissingPart()
    {
        byte[] package = new VisioPackageBuilder()
            .AddPage("Page-1", VisioFixture.Shape(1, "Body"))
            .Replace("visio/pages/_rels/pages.xml.rels", VisioPackageBuilder.Rels(("rId1", VisioFixture.VisioRelationshipPrefix + "page", "http://evil.example/page1.xml", true)))
            .Build();

        Assert.Equal(DocumentPackageRejection.MissingPart, VisioFixture.Reject(package));
    }

    [Fact]
    public void Extract_ExternalRelationshipsAlongsideInternalOnesAreIgnored()
    {
        byte[] package = new VisioPackageBuilder()
            .AddPage("Page-1", VisioFixture.Shape(1, "Body"))
            .Replace("visio/pages/_rels/pages.xml.rels", VisioPackageBuilder.Rels(
                ("rId1", VisioFixture.VisioRelationshipPrefix + "page", "page1.xml", false),
                ("rId2", VisioFixture.VisioRelationshipPrefix + "page", "http://evil.example/page2.xml", true),
                ("rId3", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink", "http://evil.example/", true)))
            .Build();

        Assert.Equal("Page: Page-1\nBody\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_PartTargetsWithPercentEscapesResolveToTheUnescapedEntryNames()
    {
        // Pages, a page and a master are stored under names with a space and referenced as %20, as OPC producers write them.
        VisioPackageBuilder builder = new VisioPackageBuilder()
            .AddMaster(2, VisioFixture.Shape(5, "Stencil label"))
            .AddPage("P", "<Shape ID='1' Type='Shape' Master='2'/>");

        Dictionary<string, string> parts = builder.Entries().ToDictionary(entry => entry.Name, entry => entry.Content);

        byte[] package = builder
            .Remove("visio/pages/pages.xml")
            .Remove("visio/pages/_rels/pages.xml.rels")
            .Remove("visio/pages/page1.xml")
            .Remove("visio/masters/master1.xml")
            .Replace("visio/pages/my pages.xml", parts["visio/pages/pages.xml"])
            .Replace("visio/pages/_rels/my pages.xml.rels", VisioPackageBuilder.Rels(("rId1", VisioFixture.VisioRelationshipPrefix + "page", "page%201.xml", false)))
            .Replace("visio/pages/page 1.xml", parts["visio/pages/page1.xml"])
            .Replace("visio/masters/master 1.xml", parts["visio/masters/master1.xml"])
            .Replace("visio/masters/_rels/masters.xml.rels", VisioPackageBuilder.Rels(("rId1", VisioFixture.VisioRelationshipPrefix + "master", "master%201.xml", false)))
            .Replace("visio/_rels/document.xml.rels", VisioPackageBuilder.Rels(
                ("rId1", VisioFixture.VisioRelationshipPrefix + "pages", "pages/my%20pages.xml", false),
                ("rId2", VisioFixture.VisioRelationshipPrefix + "masters", "masters/masters.xml", false)))
            .Build();

        Assert.Equal("Page: P\nStencil label\n", VisioFixture.Extract(package).Text);
    }

    // ---------------------------------------------------------------- smuggled and unreferenced parts

    [Fact]
    public void Extract_UnreferencedPagePartIsNeverReadEvenWhenMalformed()
    {
        // Sensitivity: walking only relationship-reachable parts. A scan of visio/pages/* would fail on the malformed part.
        byte[] package = new VisioPackageBuilder()
            .AddPage("Page-1", VisioFixture.Shape(1, "Body"))
            .Replace("visio/pages/page99.xml", "<PageContents><Shapes><Shape><Text>SMUGGLED-PAGE")
            .Replace("visio/masters/master99.xml", "not xml at all SMUGGLED-MASTER")
            .Replace("visio/other/anything.xml", VisioFixture.Xml("PageContents", $"<Shapes>{VisioFixture.Shape(1, "SMUGGLED-TEXT")}</Shapes>"))
            .Build();

        Assert.Equal("Page: Page-1\nBody\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_PageListedInPagesXmlButNotInItsRelationshipsIsMissingPartNotGuessedByName()
    {
        // pages.xml names a second page whose part exists as visio/pages/page2.xml, but the relationship part omits it.
        string pages = VisioFixture.Xml("Pages", "<Page ID='0' Name='A' NameU='A'><PageSheet/><Rel r:id='rId1'/></Page><Page ID='1' Name='B' NameU='B'><PageSheet/><Rel r:id='rId2'/></Page>");

        byte[] package = new VisioPackageBuilder()
            .AddPage("A", VisioFixture.Shape(1, "One"))
            .AddPage("B", VisioFixture.Shape(1, "Two"))
            .Replace("visio/pages/pages.xml", pages)
            .Replace("visio/pages/_rels/pages.xml.rels", VisioPackageBuilder.Rels(("rId1", VisioFixture.VisioRelationshipPrefix + "page", "page1.xml", false)))
            .Build();

        Assert.Equal(DocumentPackageRejection.MissingPart, VisioFixture.Reject(package));
    }

    // ---------------------------------------------------------------- malformed XML

    [Theory]
    [InlineData("<PageContents xmlns='http://schemas.microsoft.com/office/visio/2012/main'><Shapes><Shape ID='1'><Text>unterminated")]
    [InlineData("<PageContents xmlns='http://schemas.microsoft.com/office/visio/2012/main'><Shapes></PageContents>")]
    [InlineData("not xml")]
    [InlineData("")]
    [InlineData("<?xml version='1.0'?>")]
    [InlineData("<!-- a document with no root element -->")]
    public void Extract_MalformedPageXml_IsMalformedXml(string page)
    {
        byte[] package = new VisioPackageBuilder()
            .AddPage("Page-1", VisioFixture.Shape(1, "Body"))
            .Replace("visio/pages/page1.xml", page)
            .Build();

        Assert.Equal(DocumentPackageRejection.MalformedXml, VisioFixture.Reject(package));
    }

    [Fact]
    public void Extract_MalformedTextOnlyReachedAfterEarlierPagesHaveProducedText_StillRejectsWithoutPartialSuccess()
    {
        byte[] package = new VisioPackageBuilder()
            .AddPage("Good", VisioFixture.Shape(1, "Fine"))
            .AddPage("Bad", string.Empty)
            .Replace("visio/pages/page2.xml", VisioFixture.Xml("PageContents", "<Shapes><Shape ID='1'><Text>x</Shape></Shapes>"))
            .Build();

        Assert.Equal(DocumentPackageRejection.MalformedXml, VisioFixture.Reject(package));
    }

    [Fact]
    public void Extract_DoctypeWithAnEntityInAPagePart_IsMalformedXmlAndTheEntityIsNeverExpanded()
    {
        // Sensitivity: DtdProcessing.Prohibit in the shared reader. With Parse the marker would be expanded into the text.
        const string Page =
            "<?xml version='1.0'?><!DOCTYPE PageContents [<!ENTITY marker 'EXPANDED-ENTITY'>]>"
            + $"<PageContents xmlns='{VisioFixture.Main}'><Shapes><Shape ID='1' Type='Shape'><Text>&marker;</Text></Shape></Shapes></PageContents>";

        byte[] package = new VisioPackageBuilder()
            .AddPage("Page-1", VisioFixture.Shape(1, "Body"))
            .Replace("visio/pages/page1.xml", Page)
            .Build();

        Assert.Equal(DocumentPackageRejection.MalformedXml, VisioFixture.Reject(package));
    }

    [Fact]
    public void Extract_DoctypeInThePagesPartAndTheDocumentPart_IsMalformedXml()
    {
        const string Doctype = "<?xml version='1.0'?><!DOCTYPE x [<!ENTITY e 'v'>]>";

        byte[] pagesPackage = new VisioPackageBuilder()
            .AddPage("Page-1", VisioFixture.Shape(1, "Body"))
            .Replace("visio/pages/pages.xml", Doctype + VisioFixture.Xml("Pages", string.Empty).Replace("<?xml version='1.0' encoding='utf-8' ?>", string.Empty, StringComparison.Ordinal))
            .Build();

        Assert.Equal(DocumentPackageRejection.MalformedXml, VisioFixture.Reject(pagesPackage));

        byte[] documentPackage = new VisioPackageBuilder()
            .AddPage("Page-1", VisioFixture.Shape(1, "Body"))
            .Replace("visio/document.xml", Doctype + VisioFixture.Xml("VisioDocument", string.Empty).Replace("<?xml version='1.0' encoding='utf-8' ?>", string.Empty, StringComparison.Ordinal))
            .Build();

        Assert.Equal(DocumentPackageRejection.MalformedXml, VisioFixture.Reject(documentPackage));
    }

    [Fact]
    public void Extract_UndeclaredEntityReferenceInText_IsMalformedXml()
    {
        byte[] package = new VisioPackageBuilder()
            .AddPage("Page-1", VisioFixture.Shape(1, "a &undeclared; b"))
            .Build();

        Assert.Equal(DocumentPackageRejection.MalformedXml, VisioFixture.Reject(package));
    }

    // ---------------------------------------------------------------- container refusals propagate

    [Fact]
    public void Extract_NotAZipContainer_IsNotZip()
    {
        Assert.Equal(DocumentPackageRejection.NotZip, VisioFixture.Reject(Encoding.UTF8.GetBytes("this is not a zip file at all, just text")));
    }

    [Fact]
    public void Extract_PasswordProtectedPackage_IsEncrypted()
    {
        byte[] compoundFile = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, .. new byte[600]];

        Assert.Equal(DocumentPackageRejection.Encrypted, VisioFixture.Reject(compoundFile));
    }

    [Fact]
    public void Extract_RejectionCarriesOnlyTheClosedReasonNeverPartNamesOrDocumentText()
    {
        byte[] package = new VisioPackageBuilder()
            .AddPage("Page-1", VisioFixture.Shape(1, "Body"))
            .Replace("visio/pages/page1.xml", VisioFixture.Xml("PageContents", "<Shapes><Shape ID='1'><Text>LEAKED-TEXT</Shape></Shapes>"))
            .Build();

        DocumentPackageRejectedException exception = Assert.Throws<DocumentPackageRejectedException>(
            () => new VisioDrawingExtractor().ExtractAsync(package, new BoundedDocumentTextSink(262_144, CancellationToken.None), CancellationToken.None));

        Assert.Equal("Document part is not well-formed XML or declares a DTD.", exception.Message);

        Assert.DoesNotContain("LEAKED-TEXT", exception.ToString());

        Assert.DoesNotContain("page1.xml", exception.ToString());

        Assert.Equal(SessionAttachmentExtractionStatus.Failed, exception.ToResult().Status);

        Assert.Empty(exception.ToResult().Text);
    }

    // ---------------------------------------------------------------- structural bounds

    [Fact]
    public void Extract_PageCountAtTheLimit_Succeeds()
    {
        byte[] package = ThreePages();

        Assert.Equal("Page: A\nOne\nPage: B\nTwo\nPage: C\nThree\n", VisioFixture.Extract(package, new VisioDrawingExtractor(maximumPages: 3)).Text);
    }

    [Fact]
    public void Extract_MorePagesThanTheLimit_IsStructureBounds()
    {
        // Sensitivity: the page-count check.
        Assert.Equal(DocumentPackageRejection.StructureBounds, VisioFixture.Reject(ThreePages(), new VisioDrawingExtractor(maximumPages: 2)));
    }

    [Fact]
    public void Extract_ProductionPageLimitIs4096()
    {
        Assert.Equal(4096, VisioDrawingExtractor.MaximumPages);
    }

    [Fact]
    public void Extract_ShapeNestingBeyondTheDepthBound_IsStructureBounds()
    {
        string nested = VisioFixture.Shape(1, "leaf");

        for (int level = 0; level < 200; level++)
        {
            nested = VisioFixture.Shape(2 + level, children: nested);
        }

        byte[] package = new VisioPackageBuilder().AddPage("Page-1", nested).Build();

        Assert.Equal(DocumentPackageRejection.StructureBounds, VisioFixture.Reject(package));
    }

    [Fact]
    public void Extract_ModerateShapeNesting_IsProjected()
    {
        string nested = VisioFixture.Shape(1, "leaf");

        for (int level = 0; level < 100; level++)
        {
            nested = VisioFixture.Shape(2 + level, $"level {level}", children: nested);
        }

        byte[] package = new VisioPackageBuilder().AddPage("Page-1", nested).Build();

        string text = VisioFixture.Extract(package).Text;

        Assert.StartsWith("Page: Page-1\nlevel 99\nlevel 98\n", text, StringComparison.Ordinal);

        Assert.EndsWith("level 0\nleaf\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_OnePagePartReferencedByManyPages_IsFormatMismatchBeforeItIsReadAgain()
    {
        // The page part is ~2 MB of skipped padding. 40 pages that all point at it would have been inflated and parsed 40
        // times (past the 64 MiB aggregate bound); real producers never share a page part, so the second reference is refused
        // as soon as it is met, before the part is read again or the second page is named.
        Random random = new(11);

        string padding = string.Create(2_000_000, random, static (span, source) =>
        {
            for (int index = 0; index < span.Length; index++)
            {
                span[index] = (char)('a' + source.Next(26));
            }
        });

        string shape = $"<Shape ID='1' Type='Shape'><Section N='User'><Row N='pad'><Cell N='Value' V='{padding}'/></Row></Section><Text>Body</Text></Shape>";

        string pages = VisioFixture.Xml("Pages", string.Concat(Enumerable.Range(0, 40).Select(index => $"<Page ID='{index}' Name='P{index}' NameU='P{index}'><PageSheet/><Rel r:id='rId1'/></Page>")));

        byte[] package = new VisioPackageBuilder()
            .AddPage("P0", shape)
            .Replace("visio/pages/pages.xml", pages)
            .Build();

        BoundedDocumentTextSink sink = new(262_144, CancellationToken.None);

        DocumentPackageRejectedException exception = Assert.Throws<DocumentPackageRejectedException>(
            () => new VisioDrawingExtractor().ExtractAsync(package, sink, CancellationToken.None));

        Assert.Equal(DocumentPackageRejection.FormatMismatch, exception.Rejection);

        Assert.Equal("Page: P0\nBody\n", sink.Complete().Text);
    }

    [Theory]
    [InlineData("page1.xml", "page1.xml")]
    [InlineData("page1.xml", "PAGE1.XML")]
    [InlineData("page1.xml", "/visio/pages/page1.xml")]
    [InlineData("page 1.xml", "page%201.xml")]
    public void Extract_TwoPagesNamingOnePartThroughDifferentRelationships_IsFormatMismatch(string first, string second)
    {
        byte[] package = new VisioPackageBuilder()
            .AddPage("One", VisioFixture.Shape(1, "Body"))
            .AddPage("Two", VisioFixture.Shape(1, "Other"))
            .Replace("visio/pages/page 1.xml", VisioFixture.Xml("PageContents", "<Shapes>" + VisioFixture.Shape(1, "Body") + "</Shapes>"))
            .Replace(
                "visio/pages/_rels/pages.xml.rels",
                VisioPackageBuilder.Rels(
                    ("rId1", VisioFixture.VisioRelationshipPrefix + "page", first, false),
                    ("rId2", VisioFixture.VisioRelationshipPrefix + "page", second, false)))
            .Build();

        Assert.Equal(DocumentPackageRejection.FormatMismatch, VisioFixture.Reject(package));
    }

    [Fact]
    public void Extract_APageHiddenFromTheInterfaceThatSharesAPartWithAVisiblePage_IsFormatMismatch()
    {
        byte[] package = new VisioPackageBuilder()
            .AddPage("Shown", VisioFixture.Shape(1, "Body"))
            .AddPage("Hidden", VisioFixture.Shape(1, "HIDDEN"), pageSheet: VisioFixture.Cell("UIVisibility", "1"))
            .Replace(
                "visio/pages/_rels/pages.xml.rels",
                VisioPackageBuilder.Rels(
                    ("rId1", VisioFixture.VisioRelationshipPrefix + "page", "page1.xml", false),
                    ("rId2", VisioFixture.VisioRelationshipPrefix + "page", "page1.xml", false)))
            .Build();

        Assert.Equal(DocumentPackageRejection.FormatMismatch, VisioFixture.Reject(package));
    }

    [Fact]
    public void Extract_PagesNamingTheirOwnPartsEach_AreUnaffectedBySharingChecks()
    {
        byte[] package = new VisioPackageBuilder()
            .AddPage("One", VisioFixture.Shape(1, "A"))
            .AddPage("Two", VisioFixture.Shape(1, "B"))
            .AddPage("Three", VisioFixture.Shape(1, "C"), pageSheet: VisioFixture.Cell("UIVisibility", "1"))
            .Build();

        Assert.Equal("Page: One\nA\nPage: Two\nB\n", VisioFixture.Extract(package).Text);
    }

    // ---------------------------------------------------------------- cancellation

    [Fact]
    public async Task Extract_CancellationRequestedWhileAPageIsBeingRead_Stops()
    {
        // A page of many shapes: the first flush cancels, and the bounded reader's periodic check observes it.
        string shapes = string.Concat(Enumerable.Range(1, 5000).Select(index => VisioFixture.Shape(index, "x")));

        byte[] package = new VisioPackageBuilder().AddPage("Page-1", shapes).Build();

        using CancellationTokenSource cancellation = new();

        CancellingSink sink = new(cancellation);

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await new VisioDrawingExtractor().ExtractAsync(package, sink, cancellation.Token));

        Assert.True(sink.Appends < 5000);
    }

    private static byte[] ThreePages() => new VisioPackageBuilder()
        .AddPage("A", VisioFixture.Shape(1, "One"))
        .AddPage("B", VisioFixture.Shape(1, "Two"))
        .AddPage("C", VisioFixture.Shape(1, "Three"))
        .Build();

    private sealed class CancellingSink(CancellationTokenSource cancellation) : ISessionAttachmentDocumentTextSink
    {
        public int Appends { get; private set; }

        public bool TryAppend(ReadOnlySpan<char> text)
        {
            Appends++;

            cancellation.Cancel();

            return true;
        }

        public void MarkTruncated()
        {
        }
    }
}
