using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Weave;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension;

/// <summary>
/// The Visio extractor over the on-disk, hand-built packages in <c>TestData/Comprehension/Visio</c>
/// (see its README: no Visio producer exists locally, so these follow MS-VSDX rather than a real file).
/// </summary>
public sealed class VisioDrawingExtractorFileTests
{
    private const string StencilProjection =
        "Page: Page-1\nProcess step\nReview the plan\n"
        + "Page: Page-2\nNotes without a stencil\n"
        + "Page: Page-3\nProcess step\nUpdated: 2026-10-22\n";

    [Fact]
    public void Extract_HandBuiltStencilDrawing_ShowsTheMasterLabelOnlyOnPagesWithAnInstance()
    {
        SessionAttachmentExtractionResult result = VisioFixture.Extract(ReadFixture("hand-built-stencil.vsdx"));

        Assert.Equal(StencilProjection, result.Text);

        Assert.Equal(SessionAttachmentExtractionStatus.Extracted, result.Status);
    }

    [Fact]
    public void Extract_HandBuiltStencilDrawing_NeverProjectsHiddenTextUnusedMastersOrWindows()
    {
        string text = VisioFixture.Extract(ReadFixture("hand-built-stencil.vsdx")).Text;

        Assert.DoesNotContain("Hidden note", text, StringComparison.Ordinal);

        Assert.DoesNotContain("UNUSED-DECISION-LABEL", text, StringComparison.Ordinal);

        Assert.DoesNotContain("WINDOW-MARKER", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_HandBuiltMacroEnabledDrawing_ProjectsTheSameTextAndNeverTheMacroProject()
    {
        string text = VisioFixture.Extract(ReadFixture("hand-built-stencil.vsdm")).Text;

        Assert.Equal(StencilProjection, text);

        Assert.DoesNotContain("VBA-MACRO-MARKER", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_HandBuiltPackageIsAValidBoundedZipWithTheDocumentedRelationshipGraph()
    {
        BoundedZipPackage package = BoundedZipPackage.Open(ReadFixture("hand-built-stencil.vsdx"), CancellationToken.None);

        OpcRelationship document = Assert.Single(
            OpcPackageRelationships.Read(package, string.Empty, CancellationToken.None),
            relationship => OpcPackageRelationships.IsVisioType(relationship.Type, "document"));

        Assert.Equal("visio/document.xml", document.TargetEntryName);

        Assert.Contains(
            OpcPackageRelationships.Read(package, document.TargetEntryName, CancellationToken.None),
            relationship => OpcPackageRelationships.IsVisioType(relationship.Type, "masters"));
    }

    private static byte[] ReadFixture(string fileName) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "Comprehension", "Visio", fileName));
}
