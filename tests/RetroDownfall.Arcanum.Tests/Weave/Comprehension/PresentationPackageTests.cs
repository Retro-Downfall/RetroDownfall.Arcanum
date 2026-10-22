using System.Text;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Weave;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension;

/// <summary>
/// Package walking for PowerPoint: parts are reached only through relationships and the slide list, nothing else is
/// opened, and every bound and malformed input ends in a closed rejection or an honest stop.
/// </summary>
public sealed class PresentationPackageTests
{
    private const string SlideRels = "ppt/slides/_rels/slide1.xml.rels";

    private const string PresentationRels = "ppt/_rels/presentation.xml.rels";

    private const string PackageRels = "_rels/.rels";

    private static string Paragraph(string text) => PresentationDeck.Paragraph(text);

    private static string Shape(int id, string paragraphs, string properties = "") => PresentationDeck.Shape(id, paragraphs, properties);

    private static string Slide(string shapes, string rootAttributes = "") => PresentationDeck.Slide(shapes, rootAttributes);

    private static string VisibleSlide() => Slide(Shape(2, Paragraph("Visible")));

    private static void AddRelationship(List<(string Name, string Content)> entries, string relsName, string relationship)
    {
        int index = entries.FindIndex(entry => entry.Name == relsName);

        if (index < 0)
        {
            entries.Add((relsName, PresentationDeck.Rels(relationship)));

            return;
        }

        entries[index] = (relsName, entries[index].Content.Replace("</Relationships>", relationship + "</Relationships>", StringComparison.Ordinal));
    }

    /// <summary>An entry whose headers are valid but whose deflate data is garbage: any attempt to inflate it fails.</summary>
    private static RawZipEntry Poisoned(string name) =>
        new(ComprehensionPackageBuilder.Utf8Bytes(name), new byte[64], 8)
        {
            DataAreaBytes = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF],
        };

    /// <summary>An entry that declares two gibibytes of output in both headers; inflating it would be refused as a bomb.</summary>
    private static RawZipEntry DeclaredBomb(string name) =>
        new(ComprehensionPackageBuilder.Utf8Bytes(name), new byte[16], 8)
        {
            DataAreaBytes = ComprehensionPackageBuilder.Deflate(new byte[16]),
            UncompressedSize = 0x7FFFFFFF,
        };

    private static byte[] Raw(List<(string Name, string Content)> entries, params RawZipEntry[] extra)
    {
        RawZipPackage package = new();

        foreach ((string name, string content) in entries)
        {
            package.Entries.Add(RawZipEntry.Deflated(name, content));
        }

        package.Entries.AddRange(extra);

        return package.ToBytes();
    }

    // ---------------------------------------------------------------- parts that are never resolved

    public static TheoryData<string, string, string, string> NeverResolvedParts() => new()
    {
        { SlideRels, PresentationDeck.Type("notesSlide"), "../notesSlides/notesSlide1.xml", "ppt/notesSlides/notesSlide1.xml" },
        { SlideRels, PresentationDeck.Type("comments"), "../comments/comment1.xml", "ppt/comments/comment1.xml" },
        { SlideRels, "http://schemas.microsoft.com/office/2018/10/relationships/comments", "../comments/modernComment_100_1.xml", "ppt/comments/modernComment_100_1.xml" },
        { SlideRels, PresentationDeck.Type("slideLayout"), "../slideLayouts/slideLayout1.xml", "ppt/slideLayouts/slideLayout1.xml" },
        { SlideRels, PresentationDeck.Type("chart"), "../charts/chart1.xml", "ppt/charts/chart1.xml" },
        { SlideRels, PresentationDeck.Type("diagramData"), "../diagrams/data1.xml", "ppt/diagrams/data1.xml" },
        { SlideRels, "http://schemas.microsoft.com/office/2007/relationships/diagramDrawing", "../diagrams/drawing1.xml", "ppt/diagrams/drawing1.xml" },
        { SlideRels, PresentationDeck.Type("oleObject"), "../embeddings/oleObject1.bin", "ppt/embeddings/oleObject1.bin" },
        { SlideRels, PresentationDeck.Type("package"), "../embeddings/Microsoft_Excel_Worksheet.xlsx", "ppt/embeddings/Microsoft_Excel_Worksheet.xlsx" },
        { SlideRels, PresentationDeck.Type("control"), "../activeX/activeX1.xml", "ppt/activeX/activeX1.xml" },
        { SlideRels, PresentationDeck.Type("image"), "../media/image1.png", "ppt/media/image1.png" },
        { SlideRels, PresentationDeck.Type("slide"), "slide2.xml", "ppt/slides/slide2.xml" },
        { PresentationRels, PresentationDeck.Type("slideMaster"), "slideMasters/slideMaster1.xml", "ppt/slideMasters/slideMaster1.xml" },
        { PresentationRels, PresentationDeck.Type("notesMaster"), "notesMasters/notesMaster1.xml", "ppt/notesMasters/notesMaster1.xml" },
        { PresentationRels, PresentationDeck.Type("handoutMaster"), "handoutMasters/handoutMaster1.xml", "ppt/handoutMasters/handoutMaster1.xml" },
        { PresentationRels, PresentationDeck.Type("commentAuthors"), "commentAuthors.xml", "ppt/commentAuthors.xml" },
        { PresentationRels, "http://schemas.microsoft.com/office/2006/relationships/vbaProject", "vbaProject.bin", "ppt/vbaProject.bin" },
        { PresentationRels, PresentationDeck.Type("theme"), "theme/theme1.xml", "ppt/theme/theme1.xml" },
        { PresentationRels, PresentationDeck.Type("customXml"), "../customXml/item1.xml", "customXml/item1.xml" },
        { PackageRels, "http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties", "docProps/core.xml", "docProps/core.xml" },
        { PackageRels, "http://schemas.openxmlformats.org/package/2006/relationships/metadata/thumbnail", "docProps/thumbnail.jpeg", "docProps/thumbnail.jpeg" },
        { PackageRels, PresentationDeck.Type("extended-properties"), "docProps/app.xml", "docProps/app.xml" },
        { PackageRels, "http://schemas.microsoft.com/office/2006/relationships/ui/extensibility", "customUI/customUI.xml", "customUI/customUI.xml" },
    };

    [Theory]
    [MemberData(nameof(NeverResolvedParts))]
    public async Task Extract_RelatedPartThatIsNotOnTheTextPath_IsNeverInflated(string relsName, string type, string target, string entryName)
    {
        // The entry's deflate data is garbage, so reading it at all would reject the package: only an untouched
        // entry lets the extraction finish with the slide's own text.
        List<(string Name, string Content)> entries = PresentationDeck.Entries(VisibleSlide());

        AddRelationship(entries, relsName, PresentationDeck.Rel("rIdExtra", type, target));

        Assert.Equal("Slide 1\nVisible\n", await PresentationDeck.TextOfAsync(Raw(entries, Poisoned(entryName))));
    }

    [Fact]
    public async Task Extract_PoisonedEntryInTheSlideList_IsRejectedProvingTheFixtureIsPoisonous()
    {
        List<(string Name, string Content)> entries = PresentationDeck.Entries(VisibleSlide());

        entries.RemoveAll(entry => entry.Name == "ppt/slides/slide1.xml");

        await PresentationDeck.AssertRejectedAsync(DocumentPackageRejection.NotZip, Raw(entries, Poisoned("ppt/slides/slide1.xml")));
    }

    [Fact]
    public async Task Extract_MacroAndEmbeddingEntriesDeclaringBombSizes_DoNotFailTheExtraction()
    {
        List<(string Name, string Content)> entries = PresentationDeck.Entries(VisibleSlide());

        AddRelationship(entries, PresentationRels, PresentationDeck.Rel("rId50", "http://schemas.microsoft.com/office/2006/relationships/vbaProject", "vbaProject.bin"));

        AddRelationship(entries, SlideRels, PresentationDeck.Rel("rId51", PresentationDeck.Type("oleObject"), "../embeddings/oleObject1.bin"));

        byte[] package = Raw(entries, DeclaredBomb("ppt/vbaProject.bin"), DeclaredBomb("ppt/embeddings/oleObject1.bin"));

        Assert.Equal("Slide 1\nVisible\n", await PresentationDeck.TextOfAsync(package));
    }

    [Fact]
    public async Task Extract_SlideEntryDeclaringMoreThanTheEntryCap_IsRejectedAsExpansion()
    {
        List<(string Name, string Content)> entries = PresentationDeck.Entries(VisibleSlide());

        entries.RemoveAll(entry => entry.Name == "ppt/slides/slide1.xml");

        await PresentationDeck.AssertRejectedAsync(DocumentPackageRejection.ExpansionBounds, Raw(entries, DeclaredBomb("ppt/slides/slide1.xml")));
    }

    [Fact]
    public async Task Extract_UnreferencedSlideParts_AreNeverReadEvenWhenTheyAreMalformed()
    {
        List<(string Name, string Content)> entries = PresentationDeck.Entries(VisibleSlide());

        entries.Add(("ppt/slides/slide99.xml", Slide(Shape(2, Paragraph("SMUGGLEDSLIDE")))));

        entries.Add(("ppt/slides/slide98.xml", "<not-well-formed"));

        Assert.Equal("Slide 1\nVisible\n", await PresentationDeck.TextOfAsync(PresentationDeck.Build(entries)));
    }

    [Fact]
    public async Task Extract_SlideRelationshipOutsideTheSlideList_IsNotProjected()
    {
        List<(string Name, string Content)> entries = PresentationDeck.Entries(VisibleSlide());

        entries.Add(("ppt/slides/slide99.xml", Slide(Shape(2, Paragraph("RELATEDBUTUNLISTED")))));

        AddRelationship(entries, PresentationRels, PresentationDeck.Rel("rId99", PresentationDeck.Type("slide"), "slides/slide99.xml"));

        Assert.Equal("Slide 1\nVisible\n", await PresentationDeck.TextOfAsync(PresentationDeck.Build(entries)));
    }

    [Fact]
    public async Task Extract_TextInASlideRelationshipTarget_IsNeverFollowed()
    {
        List<(string Name, string Content)> entries = PresentationDeck.Entries(VisibleSlide());

        entries.Add(("ppt/notesSlides/notesSlide1.xml", Slide(Shape(2, Paragraph("SPEAKERNOTES")))));

        entries.Add(("ppt/comments/comment1.xml", "<comments><comment>COMMENTTEXT</comment></comments>"));

        AddRelationship(entries, SlideRels, PresentationDeck.Rel("rId1", PresentationDeck.Type("notesSlide"), "../notesSlides/notesSlide1.xml"));

        AddRelationship(entries, SlideRels, PresentationDeck.Rel("rId2", PresentationDeck.Type("comments"), "../comments/comment1.xml"));

        AddRelationship(entries, SlideRels, PresentationDeck.Rel("rId3", PresentationDeck.Type("hyperlink"), "https://evil.example/COMMENTTEXT", external: true));

        Assert.Equal("Slide 1\nVisible\n", await PresentationDeck.TextOfAsync(PresentationDeck.Build(entries)));
    }

    // ---------------------------------------------------------------- the slide list

    [Fact]
    public async Task Extract_SlideListEntryWithoutARelationshipId_IsMissingPart()
    {
        List<(string Name, string Content)> entries = PresentationDeck.Entries(VisibleSlide());

        PresentationDeck.Replace(
            entries,
            "ppt/presentation.xml",
            PresentationDeck.Presentation(PresentationDeck.P, PresentationDeck.R, "rId1").Replace(" r:id=\"rId1\"", string.Empty, StringComparison.Ordinal));

        await PresentationDeck.AssertRejectedAsync(DocumentPackageRejection.MissingPart, PresentationDeck.Build(entries));
    }

    [Fact]
    public async Task Extract_SlideListEntryWithAnUnknownRelationshipId_IsMissingPart()
    {
        List<(string Name, string Content)> entries = PresentationDeck.Entries(VisibleSlide());

        PresentationDeck.Replace(
            entries,
            "ppt/presentation.xml",
            PresentationDeck.Presentation(PresentationDeck.P, PresentationDeck.R, "rId1", "rId77"));

        await PresentationDeck.AssertRejectedAsync(DocumentPackageRejection.MissingPart, PresentationDeck.Build(entries));
    }

    [Fact]
    public async Task Extract_SlideListEntryPointingAtANonSlideRelationship_IsMissingPart()
    {
        // Only relationships of type slide are slides: a notes page cannot be listed as one.
        List<(string Name, string Content)> entries = PresentationDeck.Entries(VisibleSlide());

        entries.Add(("ppt/notesSlides/notesSlide1.xml", Slide(Shape(2, Paragraph("SPEAKERNOTES")))));

        AddRelationship(entries, PresentationRels, PresentationDeck.Rel("rId9", PresentationDeck.Type("notesSlide"), "notesSlides/notesSlide1.xml"));

        PresentationDeck.Replace(
            entries,
            "ppt/presentation.xml",
            PresentationDeck.Presentation(PresentationDeck.P, PresentationDeck.R, "rId1", "rId9"));

        await PresentationDeck.AssertRejectedAsync(DocumentPackageRejection.MissingPart, PresentationDeck.Build(entries));
    }

    [Fact]
    public async Task Extract_SlideListEntryPointingAtAnExternalSlideRelationship_IsMissingPartAndNeverFollowed()
    {
        List<(string Name, string Content)> entries = PresentationDeck.Entries(VisibleSlide());

        AddRelationship(entries, PresentationRels, PresentationDeck.Rel("rId9", PresentationDeck.Type("slide"), "https://evil.example/slide.xml", external: true));

        PresentationDeck.Replace(
            entries,
            "ppt/presentation.xml",
            PresentationDeck.Presentation(PresentationDeck.P, PresentationDeck.R, "rId1", "rId9"));

        await PresentationDeck.AssertRejectedAsync(DocumentPackageRejection.MissingPart, PresentationDeck.Build(entries));
    }

    [Fact]
    public async Task Extract_SlideRelationshipWhoseTargetIsAbsent_IsMissingPart()
    {
        List<(string Name, string Content)> entries = PresentationDeck.Entries(VisibleSlide());

        entries.RemoveAll(entry => entry.Name == "ppt/slides/slide1.xml");

        await PresentationDeck.AssertRejectedAsync(DocumentPackageRejection.MissingPart, PresentationDeck.Build(entries));
    }

    [Fact]
    public async Task Extract_SlideRelationshipWhoseTargetIsADirectory_IsMissingPart()
    {
        List<(string Name, string Content)> entries = PresentationDeck.Entries(VisibleSlide());

        entries.RemoveAll(entry => entry.Name == "ppt/slides/slide1.xml");

        entries.Add(("ppt/slides/slide1.xml/", string.Empty));

        await PresentationDeck.AssertRejectedAsync(DocumentPackageRejection.MissingPart, PresentationDeck.Build(entries));
    }

    [Fact]
    public async Task Extract_SlideListWithoutAnyRelationshipsPart_IsMissingPart()
    {
        List<(string Name, string Content)> entries = PresentationDeck.Entries(VisibleSlide());

        entries.RemoveAll(entry => entry.Name == PresentationRels);

        await PresentationDeck.AssertRejectedAsync(DocumentPackageRejection.MissingPart, PresentationDeck.Build(entries));
    }

    [Fact]
    public async Task Extract_PresentationWithoutSlides_ProducesNoText()
    {
        List<(string Name, string Content)> entries = PresentationDeck.Entries();

        SessionAttachmentExtractionResult result = await PresentationDeck.ExtractAsync(PresentationDeck.Build(entries));

        Assert.Equal(SessionAttachmentExtractionStatus.NotEligible, result.Status);

        PresentationDeck.Replace(
            entries,
            "ppt/presentation.xml",
            $"{PresentationDeck.Declaration}<p:presentation xmlns:p=\"{PresentationDeck.P}\"/>");

        result = await PresentationDeck.ExtractAsync(PresentationDeck.Build(entries));

        Assert.Equal(SessionAttachmentExtractionStatus.NotEligible, result.Status);

        PresentationDeck.Replace(
            entries,
            "ppt/presentation.xml",
            $"{PresentationDeck.Declaration}<p:presentation xmlns:p=\"{PresentationDeck.P}\"><p:sldIdLst/></p:presentation>");

        result = await PresentationDeck.ExtractAsync(PresentationDeck.Build(entries));

        Assert.Equal(SessionAttachmentExtractionStatus.NotEligible, result.Status);
    }

    [Fact]
    public async Task Extract_OnlyTheFirstSlideListCounts()
    {
        List<(string Name, string Content)> entries = PresentationDeck.Entries(
            Slide(Shape(2, Paragraph("Listed"))),
            Slide(Shape(2, Paragraph("SECONDLISTSLIDE"))));

        PresentationDeck.Replace(
            entries,
            "ppt/presentation.xml",
            PresentationDeck.Presentation(PresentationDeck.P, PresentationDeck.R, "rId1")
                .Replace("</p:presentation>", "<p:sldIdLst><p:sldId id=\"300\" r:id=\"rId2\"/></p:sldIdLst></p:presentation>", StringComparison.Ordinal));

        Assert.Equal("Slide 1\nListed\n", await PresentationDeck.TextOfAsync(PresentationDeck.Build(entries)));
    }

    [Fact]
    public async Task Extract_SlideListNestedInsideAnotherElement_IsNotTheSlideList()
    {
        List<(string Name, string Content)> entries = PresentationDeck.Entries(
            Slide(Shape(2, Paragraph("Listed"))),
            Slide(Shape(2, Paragraph("NESTEDLISTSLIDE"))));

        string presentation = PresentationDeck.Presentation(PresentationDeck.P, PresentationDeck.R, "rId1").Replace(
            "<p:sldIdLst>",
            "<p:extLst><p:ext uri=\"{1}\"><p:sldIdLst><p:sldId id=\"300\" r:id=\"rId2\"/></p:sldIdLst></p:ext></p:extLst><p:sldIdLst>",
            StringComparison.Ordinal);

        PresentationDeck.Replace(entries, "ppt/presentation.xml", presentation);

        Assert.Equal("Slide 1\nListed\n", await PresentationDeck.TextOfAsync(PresentationDeck.Build(entries)));
    }

    [Fact]
    public async Task Extract_UnresolvableSlideAfterResolvableOnes_FailsBeforeAnyTextIsProduced()
    {
        // Sensitivity: resolving each slide while the list is read, rather than when its text is extracted.
        List<(string Name, string Content)> entries = PresentationDeck.Entries(VisibleSlide(), VisibleSlide());

        entries.RemoveAll(entry => entry.Name == "ppt/slides/slide2.xml");

        RecordingSink sink = new(_ => true);

        DocumentPackageRejectedException exception = await Assert.ThrowsAsync<DocumentPackageRejectedException>(
            () => new PresentationDocumentExtractor().ExtractAsync(PresentationDeck.Build(entries), sink, CancellationToken.None).AsTask());

        Assert.Equal(DocumentPackageRejection.MissingPart, exception.Rejection);

        Assert.Empty(sink.Appends);
    }

    [Theory]
    [InlineData("rId1", "rId1")]
    [InlineData("rId1", "rId2")]
    public async Task Extract_SameSlidePartListedTwice_IsFormatMismatchBeforeAnySlideIsRead(string first, string second)
    {
        // Real producers never share a slide part between positions; the second mention is refused whether it reuses
        // the relationship id or a second relationship naming the same part, and nothing is projected first.
        List<(string Name, string Content)> entries = PresentationDeck.Entries(Slide(Shape(2, Paragraph("Repeated"))));

        PresentationDeck.Replace(
            entries,
            "ppt/presentation.xml",
            PresentationDeck.Presentation(PresentationDeck.P, PresentationDeck.R, first, second));

        PresentationDeck.Replace(
            entries,
            PresentationRels,
            PresentationDeck.Rels(
                PresentationDeck.Rel("rId1", PresentationDeck.Type("slide"), "slides/slide1.xml"),
                PresentationDeck.Rel("rId2", PresentationDeck.Type("slide"), "./SLIDES/slide1.xml")));

        RecordingSink sink = new(_ => true);

        DocumentPackageRejectedException exception = await Assert.ThrowsAsync<DocumentPackageRejectedException>(
            () => new PresentationDocumentExtractor().ExtractAsync(PresentationDeck.Build(entries), sink, CancellationToken.None).AsTask());

        Assert.Equal(DocumentPackageRejection.FormatMismatch, exception.Rejection);

        Assert.Empty(sink.Appends);
    }

    [Fact]
    public void Extract_ManySlideListEntriesNamingOneLargeSlide_AreRefusedWithoutInflatingIt()
    {
        // One slide part just under the ratio floor, named by every one of 4,096 slide list entries. Each mention would
        // cost a full inflate and parse of it, so the second mention must end the extraction before any slide is read.
        string padding = Paragraph(new string('p', 1_000_000));

        string hiddenSlide = Slide(Shape(2, padding, " hidden=\"1\""));

        Assert.InRange(Encoding.UTF8.GetByteCount(hiddenSlide), 1_000_000, 1_048_576);

        List<(string Name, string Content)> entries = PresentationDeck.Entries(hiddenSlide);

        PresentationDeck.Replace(
            entries,
            "ppt/presentation.xml",
            PresentationDeck.Presentation(PresentationDeck.P, PresentationDeck.R, [.. Enumerable.Repeat("rId1", PresentationDocumentExtractor.MaximumSlides)]));

        byte[] deck = PresentationDeck.Build(entries);

        Assert.InRange(deck.Length, 1, 32_768);

        RecordingSink sink = new(_ => true);

        PresentationDocumentExtractor extractor = new();

        long before = GC.GetAllocatedBytesForCurrentThread();

        DocumentPackageRejectedException exception = Assert.Throws<DocumentPackageRejectedException>(
            () => extractor.ExtractAsync(deck, sink, CancellationToken.None));

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(DocumentPackageRejection.FormatMismatch, exception.Rejection);

        Assert.Empty(sink.Appends);

        // One inflate of the slide alone allocates at least its 1 MB; refusing at the second mention allocates a few
        // hundred kilobytes for the slide list. Running into the aggregate bound instead allocates over 64 MiB.
        Assert.True(allocated < 16L * 1024 * 1024, $"Refusing the shared slide allocated {allocated:N0} bytes.");
    }

    [Fact]
    public async Task Extract_StrictRelationshipNamespaceOnATransitionalPresentation_ResolvesTheSlides()
    {
        List<(string Name, string Content)> entries = PresentationDeck.Entries(Slide(Shape(2, Paragraph("Mixed"))));

        PresentationDeck.Replace(
            entries,
            "ppt/presentation.xml",
            PresentationDeck.Presentation(PresentationDeck.P, PresentationDeck.StrictR, "rId1"));

        PresentationDeck.Replace(
            entries,
            PresentationRels,
            PresentationDeck.Rels(PresentationDeck.Rel("rId1", PresentationDeck.StrictType("slide"), "slides/slide1.xml")));

        Assert.Equal("Slide 1\nMixed\n", await PresentationDeck.TextOfAsync(PresentationDeck.Build(entries)));
    }

    [Theory]
    [InlineData("slides/slide1.xml")]
    [InlineData("./slides/slide1.xml")]
    [InlineData("slides/../slides/slide1.xml")]
    [InlineData("/ppt/slides/slide1.xml")]
    [InlineData("SLIDES/Slide1.XML")]
    [InlineData("slides/slid%65%31.xml")]
    public async Task Extract_SlideTargetSpellings_ResolveToTheSamePart(string target)
    {
        List<(string Name, string Content)> entries = PresentationDeck.Entries(VisibleSlide());

        PresentationDeck.Replace(
            entries,
            PresentationRels,
            PresentationDeck.Rels(PresentationDeck.Rel("rId1", PresentationDeck.Type("slide"), target)));

        Assert.Equal("Slide 1\nVisible\n", await PresentationDeck.TextOfAsync(PresentationDeck.Build(entries)));
    }

    [Fact]
    public async Task Extract_PresentationPartAtThePackageRoot_ResolvesItsSlidesRelativeToIt()
    {
        byte[] package = ComprehensionPackageBuilder.Build(
            (PackageRels, PresentationDeck.Rels(PresentationDeck.Rel("rId1", PresentationDeck.Type("officeDocument"), "presentation.xml"))),
            ("presentation.xml", PresentationDeck.Presentation(PresentationDeck.P, PresentationDeck.R, "rId1")),
            ("_rels/presentation.xml.rels", PresentationDeck.Rels(PresentationDeck.Rel("rId1", PresentationDeck.Type("slide"), "slides/one.xml"))),
            ("slides/one.xml", VisibleSlide()));

        Assert.Equal("Slide 1\nVisible\n", await PresentationDeck.TextOfAsync(package));
    }

    // ---------------------------------------------------------------- slide count

    [Fact]
    public void MaximumSlides_IsFourThousandNinetySix()
    {
        Assert.Equal(4096, PresentationDocumentExtractor.MaximumSlides);
    }

    [Fact]
    public async Task Extract_MoreSlidesThanTheLimit_IsStructureBounds()
    {
        PresentationDocumentExtractor extractor = new(maximumSlides: 2);

        byte[] deck = PresentationDeck.Deck(VisibleSlide(), VisibleSlide(), VisibleSlide());

        await PresentationDeck.AssertRejectedAsync(DocumentPackageRejection.StructureBounds, deck, extractor);
    }

    [Fact]
    public async Task Extract_ExactlyTheSlideLimit_IsAccepted()
    {
        PresentationDocumentExtractor extractor = new(maximumSlides: 2);

        SessionAttachmentExtractionResult result = await PresentationDeck.ExtractAsync(PresentationDeck.Deck(VisibleSlide(), VisibleSlide()), extractor);

        Assert.Equal("Slide 1\nVisible\nSlide 2\nVisible\n", result.Text);
    }

    [Fact]
    public async Task Extract_TheDefaultSlideLimit_IsEnforcedOnTheSlideList()
    {
        // Every slide is its own part: a slide part named twice is refused, so the limit is reached with distinct parts.
        await PresentationDeck.AssertRejectedAsync(
            DocumentPackageRejection.StructureBounds,
            PresentationDeck.Deck([.. Enumerable.Repeat(VisibleSlide(), PresentationDocumentExtractor.MaximumSlides + 1)]));

        SessionAttachmentExtractionResult result = await PresentationDeck.ExtractAsync(
            PresentationDeck.Deck([.. Enumerable.Repeat(VisibleSlide(), PresentationDocumentExtractor.MaximumSlides)]));

        Assert.Equal(SessionAttachmentExtractionStatus.Extracted, result.Status);

        Assert.StartsWith("Slide 1\nVisible\nSlide 2\nVisible\n", result.Text, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- malformed and hostile XML

    [Theory]
    [InlineData("<p:sld xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"><p:cSld>")]
    [InlineData("")]
    [InlineData("not xml at all")]
    [InlineData("<p:sld xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"><a:x/></p:sld>")]
    public async Task Extract_MalformedSlideXml_IsMalformedXml(string slide)
    {
        List<(string Name, string Content)> entries = PresentationDeck.Entries(VisibleSlide());

        PresentationDeck.Replace(entries, "ppt/slides/slide1.xml", slide);

        await PresentationDeck.AssertRejectedAsync(DocumentPackageRejection.MalformedXml, PresentationDeck.Build(entries));
    }

    [Fact]
    public async Task Extract_SlideCutOffAfterItsFirstText_IsMalformedXmlAndYieldsNoPartialText()
    {
        string truncated = Slide(Shape(2, Paragraph("Complete") + Paragraph("Cut"))).Replace("</a:t></a:r></a:p></p:txBody>", "</a:t", StringComparison.Ordinal);

        List<(string Name, string Content)> entries = PresentationDeck.Entries(truncated);

        RecordingSink sink = new(_ => true);

        DocumentPackageRejectedException exception = await Assert.ThrowsAsync<DocumentPackageRejectedException>(
            () => new PresentationDocumentExtractor().ExtractAsync(PresentationDeck.Build(entries), sink, CancellationToken.None).AsTask());

        Assert.Equal(DocumentPackageRejection.MalformedXml, exception.Rejection);

        // The slide's text is held until the slide has been read to its end, so a cut-off slide leaves only its header.
        Assert.Equal(["Slide 1\n"], sink.Appends);
    }

    [Theory]
    [InlineData("ppt/slides/slide1.xml")]
    [InlineData("ppt/presentation.xml")]
    [InlineData("ppt/_rels/presentation.xml.rels")]
    public async Task Extract_PartWithADocumentTypeDeclaration_IsMalformedXml(string part)
    {
        List<(string Name, string Content)> entries = PresentationDeck.Entries(VisibleSlide());

        string original = entries.Single(entry => entry.Name == part).Content;

        string withDocType = original.Replace(
            PresentationDeck.Declaration,
            PresentationDeck.Declaration + "<!DOCTYPE root [<!ENTITY marker \"ENTITYMARKER\">]>",
            StringComparison.Ordinal);

        PresentationDeck.Replace(entries, part, withDocType);

        await PresentationDeck.AssertRejectedAsync(DocumentPackageRejection.MalformedXml, PresentationDeck.Build(entries));
    }

    [Fact]
    public async Task Extract_SlideReferencingAnExternalEntity_IsMalformedXml()
    {
        string slide = VisibleSlide().Replace(
            PresentationDeck.Declaration,
            PresentationDeck.Declaration + "<!DOCTYPE p:sld [<!ENTITY secret SYSTEM \"file:///etc/hosts\">]>",
            StringComparison.Ordinal).Replace(">Visible<", ">&secret;<", StringComparison.Ordinal);

        await PresentationDeck.AssertRejectedAsync(DocumentPackageRejection.MalformedXml, PresentationDeck.Deck(slide));
    }

    [Fact]
    public async Task Extract_SlideRootThatIsNotASlide_IsFormatMismatch()
    {
        string slide = $"{PresentationDeck.Declaration}<p:notes xmlns:p=\"{PresentationDeck.P}\"/>";

        await PresentationDeck.AssertRejectedAsync(DocumentPackageRejection.FormatMismatch, PresentationDeck.Deck(slide));
    }

    [Fact]
    public async Task Extract_SlideRootInTheWrongNamespace_IsFormatMismatch()
    {
        string slide = VisibleSlide().Replace(PresentationDeck.P, "urn:not-presentationml", StringComparison.Ordinal);

        await PresentationDeck.AssertRejectedAsync(DocumentPackageRejection.FormatMismatch, PresentationDeck.Deck(slide));
    }

    [Fact]
    public async Task Extract_EmptySlideRoot_ProjectsOnlyItsHeader()
    {
        string slide = $"{PresentationDeck.Declaration}<p:sld xmlns:p=\"{PresentationDeck.P}\"/>";

        Assert.Equal("Slide 1\n", await PresentationDeck.TextOfAsync(PresentationDeck.Deck(slide)));
    }

    [Theory]
    [InlineData(255, true)]
    [InlineData(256, false)]
    public async Task Extract_ElementDepth_IsBoundedAtTwoHundredFiftySix(int nesting, bool accepted)
    {
        // The innermost element of a 255-deep chain sits at depth 256, the last depth the reader allows.
        string chain = string.Concat(Enumerable.Repeat("<a:x>", nesting - 1)) + "<a:x/>" + string.Concat(Enumerable.Repeat("</a:x>", nesting - 1));

        string slide = Slide(Shape(2, Paragraph("Deep"))).Replace("</p:cSld>", chain + "</p:cSld>", StringComparison.Ordinal);

        byte[] deck = PresentationDeck.Deck(slide);

        if (accepted)
        {
            Assert.Equal("Slide 1\nDeep\n", await PresentationDeck.TextOfAsync(deck));
        }
        else
        {
            await PresentationDeck.AssertRejectedAsync(DocumentPackageRejection.StructureBounds, deck);
        }
    }

    [Fact]
    public async Task Extract_AlternateContentAtTheDeepestAllowedDepth_IsAccepted()
    {
        // The per-depth state arrays must reach the reader's last allowed depth, 256.
        string chain = string.Concat(Enumerable.Repeat("<a:x>", 254)) + $"<mc:AlternateContent xmlns:mc=\"{PresentationDeck.Mc}\"/>" + string.Concat(Enumerable.Repeat("</a:x>", 254));

        string slide = Slide(Shape(2, Paragraph("Deep"))).Replace("</p:cSld>", chain + "</p:cSld>", StringComparison.Ordinal);

        Assert.Equal("Slide 1\nDeep\n", await PresentationDeck.TextOfAsync(PresentationDeck.Deck(slide)));
    }

    [Fact]
    public async Task Extract_DeeplyNestedGroups_StillProjectTheirText()
    {
        string group = "<p:grpSp><p:nvGrpSpPr><p:cNvPr id=\"3\" name=\"G\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>";

        string shapes = string.Concat(Enumerable.Repeat(group, 100)) + Shape(2, Paragraph("Nested deep")) + string.Concat(Enumerable.Repeat("</p:grpSp>", 100));

        Assert.Equal("Slide 1\nNested deep\n", await PresentationDeck.TextOfAsync(PresentationDeck.Deck(Slide(shapes))));
    }

    // ---------------------------------------------------------------- sink and cancellation

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Extract_StopsAtOnceWhenTheSinkRefusesText(int acceptedAppends)
    {
        // Appends in order: the slide header, then the slide's whole text once it has been read. The second slide is
        // malformed, so reading on after a refusal would surface as a rejection.
        string shape = Shape(2, "<a:p><a:r><a:t>A</a:t></a:r><a:br/></a:p><a:p/>");

        List<(string Name, string Content)> entries = PresentationDeck.Entries(Slide(shape), "<broken");

        RecordingSink sink = new(appendCount => appendCount < acceptedAppends);

        await new PresentationDocumentExtractor().ExtractAsync(PresentationDeck.Build(entries), sink, CancellationToken.None);

        Assert.Equal(acceptedAppends, sink.Appends.Count);

        if (acceptedAppends == 2)
        {
            Assert.Equal("A\n\n\n", sink.Appends[1]);
        }
    }

    [Fact]
    public async Task Extract_ExtractionBeyondTheSinkCeiling_StopsAndReportsTruncation()
    {
        string paragraphs = string.Concat(Enumerable.Range(0, 200).Select(index => Paragraph($"Line {index:D3}")));

        SessionAttachmentExtractionResult result = await PresentationDeck.ExtractAsync(
            PresentationDeck.Deck(Slide(Shape(2, paragraphs))),
            maximumCharacters: 40);

        Assert.True(result.WasTruncated);

        Assert.Equal(40, result.Text.Length);

        Assert.StartsWith("Slide 1\nLine 000\nLine 001\n", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Extract_OneHugeTextRun_IsCutAtTheSinkCeiling()
    {
        Random random = new(1234);

        string huge = new([.. Enumerable.Range(0, 700_000).Select(_ => (char)('a' + random.Next(26)))]);

        SessionAttachmentExtractionResult result = await PresentationDeck.ExtractAsync(
            PresentationDeck.Deck(Slide(Shape(2, Paragraph(huge)))),
            maximumCharacters: 1000);

        Assert.True(result.WasTruncated);

        Assert.Equal(1000, result.Text.Length);
    }

    [Fact]
    public async Task Extract_AlreadyCancelled_Throws()
    {
        using CancellationTokenSource cancellation = new();

        await cancellation.CancelAsync();

        RecordingSink sink = new(_ => true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new PresentationDocumentExtractor().ExtractAsync(PresentationDeck.Deck(VisibleSlide()), sink, cancellation.Token).AsTask());
    }

    [Fact]
    public async Task Extract_CancelledWhileReadingALargeSlide_Throws()
    {
        // The reader checks cancellation every 1,024 nodes, so a slide of tens of thousands of nodes notices a
        // cancellation that arrives after the header has been appended.
        string paragraphs = string.Concat(Enumerable.Range(0, 3000).Select(index => Paragraph($"Line {index}")));

        using CancellationTokenSource cancellation = new();

        RecordingSink sink = new(_ =>
        {
            cancellation.Cancel();

            return true;
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new PresentationDocumentExtractor().ExtractAsync(PresentationDeck.Deck(Slide(Shape(2, paragraphs))), sink, cancellation.Token).AsTask());

        // The text is held until the slide has been read, so the cancelled slide leaves only its header behind.
        Assert.Equal(["Slide 1\n"], sink.Appends);
    }

    [Fact]
    public async Task Extract_CancelledBetweenSlides_StopsBeforeReadingTheNextSlide()
    {
        using CancellationTokenSource cancellation = new();

        RecordingSink sink = new(_ =>
        {
            cancellation.Cancel();

            return true;
        });

        byte[] deck = PresentationDeck.Deck(VisibleSlide(), VisibleSlide(), VisibleSlide());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new PresentationDocumentExtractor().ExtractAsync(deck, sink, cancellation.Token).AsTask());

        Assert.Equal(["Slide 1\n", "Visible\n"], sink.Appends);
    }

    [Fact]
    public async Task Extract_RejectionMessages_NeverCarryPartNamesOrDocumentText()
    {
        List<(string Name, string Content)> entries = PresentationDeck.Entries(Slide(Shape(2, Paragraph("DOCUMENTSECRET"))));

        PresentationDeck.Replace(
            entries,
            "ppt/presentation.xml",
            PresentationDeck.Presentation(PresentationDeck.P, PresentationDeck.R, "rId1", "rId-SECRETRELATIONSHIP"));

        DocumentPackageRejectedException exception = await Assert.ThrowsAsync<DocumentPackageRejectedException>(
            () => PresentationDeck.ExtractAsync(PresentationDeck.Build(entries)));

        Assert.Equal("Document package is missing a required part.", exception.Message);
    }

    private sealed class RecordingSink(Func<int, bool> accept) : ISessionAttachmentDocumentTextSink
    {
        public List<string> Appends { get; } = [];

        public bool TryAppend(ReadOnlySpan<char> text)
        {
            Appends.Add(text.ToString());

            return accept(Appends.Count);
        }

        public void MarkTruncated()
        {
        }
    }
}
