using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Weave;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension;

/// <summary>
/// Macro-enabled drawings, parts that must never be opened, text that is not projected, and the buffering bounds of
/// a single top-level shape.
/// </summary>
public sealed class VisioDrawingExtractorPackageTests
{
    // ---------------------------------------------------------------- macro-enabled drawings and parts never opened

    [Fact]
    public void Extract_MacroEnabledDrawing_ProjectsTextAndNeverInflatesTheReferencedVbaProject()
    {
        // The vbaProject.bin entry is referenced by a document relationship, declares a bomb-sized length and holds garbage
        // where deflate data should be. Reading it would reject the package, so success proves it is never inflated.
        byte[] package = new VisioPackageBuilder()
            .MacroEnabled()
            .AddPage("Page-1", VisioFixture.Shape(1, "Macro drawing text"))
            .AddRaw(VisioPackageBuilder.UninflatableEntry("visio/vbaProject.bin"))
            .Build();

        Assert.Equal("Page: Page-1\nMacro drawing text\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_MacroEnabledDrawingWithTheSameBytesAsAPlainOne_ProjectsTheSameText()
    {
        VisioPackageBuilder plain = new VisioPackageBuilder().AddPage("Page-1", VisioFixture.Shape(1, "Same"));

        VisioPackageBuilder macro = new VisioPackageBuilder().MacroEnabled().AddPage("Page-1", VisioFixture.Shape(1, "Same")).AddRaw(VisioPackageBuilder.UninflatableEntry("visio/vbaProject.bin"));

        Assert.Equal(VisioFixture.Extract(plain.Build()).Text, VisioFixture.Extract(macro.Build()).Text);
    }

    [Fact]
    public void Extract_EmbeddingsImagesAndCustomUiReferencedFromAnyPart_AreNeverInflated()
    {
        VisioPackageBuilder builder = new VisioPackageBuilder()
            .AddPage("Page-1", $"<Shape ID='1' Type='Foreign'><Text>Picture caption</Text><ForeignData ForeignType='Bitmap'><Rel r:id='rId9'/></ForeignData></Shape>")
            .Replace("visio/pages/_rels/pages.xml.rels", VisioPackageBuilder.Rels(("rId1", VisioFixture.VisioRelationshipPrefix + "page", "page1.xml", false)))
            .Replace("visio/pages/_rels/page1.xml.rels", VisioPackageBuilder.Rels(
                ("rId9", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/image", "../media/image1.png", false),
                ("rId10", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/oleObject", "../embeddings/oleObject1.bin", false),
                ("rId11", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/package", "../embeddings/Microsoft_Word_Document1.docx", false)))
            .Replace("visio/_rels/document.xml.rels", VisioPackageBuilder.Rels(
                ("rId1", VisioFixture.VisioRelationshipPrefix + "pages", "pages/pages.xml", false),
                ("rId6", "http://schemas.microsoft.com/office/2006/relationships/ui/extensibility", "customUI/customUI.xml", false),
                ("rId7", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/customXml", "../customXml/item1.xml", false)))
            .AddRaw(VisioPackageBuilder.UninflatableEntry("visio/media/image1.png"))
            .AddRaw(VisioPackageBuilder.UninflatableEntry("visio/embeddings/oleObject1.bin"))
            .AddRaw(VisioPackageBuilder.UninflatableEntry("visio/embeddings/Microsoft_Word_Document1.docx"))
            .AddRaw(VisioPackageBuilder.UninflatableEntry("visio/customUI/customUI.xml"));

        Assert.Equal("Page: Page-1\nPicture caption\n", VisioFixture.Extract(builder.Build()).Text);
    }

    [Fact]
    public void Extract_PageWithOnlyForeignDataAndNoText_ProjectsNothingBeyondItsHeader()
    {
        byte[] package = new VisioPackageBuilder()
            .AddPage("Page-1", "<Shape ID='1' Type='Foreign'><ForeignData ForeignType='Bitmap'><Rel r:id='rId9'/></ForeignData></Shape>")
            .Build();

        Assert.Equal("Page: Page-1\n", VisioFixture.Extract(package).Text);
    }

    // ---------------------------------------------------------------- shape sections that are not rendered text

    [Fact]
    public void Extract_FieldFormulasAnnotationsActionsAndCharacterSectionsAreNeverProjected()
    {
        const string Sections =
            "<Section N='Field'><Row IX='0'><Cell N='Value' V='FIELD-VALUE-SECRET' F='=PAGENUMBER()'/></Row></Section>"
            + "<Section N='Annotation'><Row IX='0'><Cell N='Comment' V='ANNOTATION-SECRET'/><Cell N='Name' V='ANNOTATION-AUTHOR'/></Row></Section>"
            + "<Section N='Action'><Row N='Act'><Cell N='Menu' V='ACTION-MENU-SECRET'/><Cell N='Action' V='RUNADDON(&quot;x&quot;)'/></Row></Section>"
            + "<Section N='Character'><Row IX='0'><Cell N='Font' V='1'/><Cell N='Color' V='#ffffff'/></Row></Section>"
            + "<Section N='Scratch'><Row IX='0'><Cell N='A' V='SCRATCH-SECRET'/></Row></Section>";

        byte[] package = new VisioPackageBuilder()
            .AddPage("Page-1", $"<Shape ID='1' Type='Shape'><Cell N='PinX' V='1'/>{Sections}<Text>Rendered <fld IX='0'>Page-1</fld></Text></Shape>")
            .Build();

        Assert.Equal("Page: Page-1\nRendered Page-1\n", VisioFixture.Extract(package).Text);
    }

    [Fact]
    public void Extract_ShapeNamesAndAttributeValuesAreNeverProjectedOnlyPageNamesAre()
    {
        byte[] package = new VisioPackageBuilder()
            .AddPage("Page-1", VisioFixture.Shape(1, "Text", attributes: "Name='SHAPE-NAME-SECRET' NameU='SHAPE-NAMEU-SECRET' UniqueID='SECRET-GUID' Prompt='PROMPT-SECRET'"))
            .Build();

        Assert.Equal("Page: Page-1\nText\n", VisioFixture.Extract(package).Text);
    }

    // ---------------------------------------------------------------- buffering bounds of one top-level shape

    [Fact]
    public void Extract_TextBeyondTheSinkCeiling_IsTruncatedByTheSinkNotWithheld()
    {
        byte[] package = new VisioPackageBuilder().AddPage("P", VisioFixture.Shape(1, new string('x', 300_000))).Build();

        SessionAttachmentExtractionResult result = VisioFixture.Extract(package);

        Assert.True(result.WasTruncated);

        Assert.Equal(262_144, result.Text.Length);

        Assert.StartsWith("Page: P\nxxxx", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_ASingleTopLevelShapeBeyondTheBufferingBound_IsWithheldAndMarksTruncation()
    {
        // Random letters, so the page part stays under the inflation-ratio bound.
        string filler = RandomText(262_144, seed: 3);

        string group = VisioFixture.Shape(
            1,
            type: "Group",
            children: string.Concat(Enumerable.Range(2, 5).Select(id => VisioFixture.Shape(id, filler))));

        byte[] package = new VisioPackageBuilder()
            .AddPage("P", VisioFixture.Shape(10, "Before") + group + VisioFixture.Shape(11, "NEVER-REACHED"))
            .Build();

        SessionAttachmentExtractionResult result = VisioFixture.Extract(package, sinkCharacters: 4_000_000);

        Assert.Equal("Page: P\nBefore\n", result.Text);

        Assert.True(result.WasTruncated);
    }

    [Fact]
    public void Extract_InheritedTextThatOverflowsTheBufferingBound_IsWithheldAndMarksTruncation()
    {
        // Each of the five sub-shapes inherits a 262,144-character master text; together they pass the 1,048,576 bound.
        string master = VisioFixture.Shape(
            10,
            type: "Group",
            children: string.Concat(Enumerable.Range(11, 5).Select(id => VisioFixture.Shape(id, id == 11 ? new string('z', 262_144) : null))));

        string subShapes = string.Concat(Enumerable.Range(2, 5).Select(id => $"<Shape ID='{id}' Type='Shape' MasterShape='11'/>"));

        byte[] package = new VisioPackageBuilder()
            .AddMaster(3, master)
            .AddPage("P", VisioFixture.Shape(20, "Before") + VisioFixture.Shape(1, type: "Group", attributes: "Master='3'", children: subShapes) + VisioFixture.Shape(21, "NEVER-REACHED"))
            .Build();

        SessionAttachmentExtractionResult result = VisioFixture.Extract(package, sinkCharacters: 4_000_000);

        Assert.Equal("Page: P\nBefore\n", result.Text);

        Assert.True(result.WasTruncated);
    }

    [Fact]
    public void Extract_ManyTopLevelShapesAreFlushedAsTheyEndSoTheirTotalIsNotBuffered()
    {
        // 40 shapes of 100,000 characters: 4,000,000 in total, far past the bound, but each is flushed when it ends.
        string shapes = string.Concat(Enumerable.Range(1, 40).Select(id => VisioFixture.Shape(id, RandomText(100_000, seed: id))));

        byte[] package = new VisioPackageBuilder().AddPage("P", shapes).Build();

        SessionAttachmentExtractionResult result = VisioFixture.Extract(package, sinkCharacters: 4_500_000);

        Assert.False(result.WasTruncated);

        Assert.Equal(8 + (40 * 100_001), result.Text.Length);
    }

    // ---------------------------------------------------------------- cancellation between pages

    [Fact]
    public async Task Extract_CancellationRequestedBetweenPages_StopsBeforeTheNextPageIsRead()
    {
        byte[] package = new VisioPackageBuilder()
            .AddPage("One", VisioFixture.Shape(1, "First"))
            .AddPage("Two", VisioFixture.Shape(1, "Second"))
            .Build();

        using CancellationTokenSource cancellation = new();

        CancelWhenTextSeenSink sink = new(cancellation, "First");

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await new VisioDrawingExtractor().ExtractAsync(package, sink, cancellation.Token));

        // Neither the second page's text nor its header: the page is not started once cancellation was requested.
        Assert.DoesNotContain("Second", sink.Text, StringComparison.Ordinal);

        Assert.DoesNotContain("Two", sink.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Extract_CancellationRequestedBeforeAMasterIsFirstNeeded_StopsBeforeTheMasterPartIsRead()
    {
        byte[] package = new VisioPackageBuilder()
            .AddMaster(2, VisioFixture.Shape(5, "Label"))
            .AddPage("One", VisioFixture.Shape(1, "First") + "<Shape ID='2' Type='Shape' Master='2'/>")
            .Build();

        using CancellationTokenSource cancellation = new();

        CancelWhenTextSeenSink sink = new(cancellation, "First");

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await new VisioDrawingExtractor().ExtractAsync(package, sink, cancellation.Token));

        Assert.DoesNotContain("Label", sink.Text, StringComparison.Ordinal);
    }

    private static string RandomText(int length, int seed)
    {
        Random random = new(seed);

        return string.Create(length, random, static (span, source) =>
        {
            for (int index = 0; index < span.Length; index++)
            {
                span[index] = (char)('a' + source.Next(26));
            }
        });
    }

    private sealed class CancelWhenTextSeenSink(CancellationTokenSource cancellation, string trigger) : ISessionAttachmentDocumentTextSink
    {
        private readonly System.Text.StringBuilder _text = new();

        public string Text => _text.ToString();

        public bool TryAppend(ReadOnlySpan<char> text)
        {
            _text.Append(text);

            if (_text.ToString().Contains(trigger, StringComparison.Ordinal))
            {
                cancellation.Cancel();
            }

            return true;
        }

        public void MarkTruncated()
        {
        }
    }
}
