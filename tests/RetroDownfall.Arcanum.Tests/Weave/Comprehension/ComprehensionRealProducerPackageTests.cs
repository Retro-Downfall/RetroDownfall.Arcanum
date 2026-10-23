using System.Text;
using System.Xml;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension;

/// <summary>
/// Container-level checks against packages a real producer wrote (macOS <c>textutil</c>; see
/// <c>TestData/Comprehension/README.md</c>), so the library is proven on bytes it did not build itself.
/// </summary>
public sealed class ComprehensionRealProducerPackageTests
{
    private const string TextMime = "application/vnd.oasis.opendocument.text";

    [Fact]
    public void TextutilOdt_OpensAndItsManifestIsAcceptedForOpenDocumentText()
    {
        // Sensitivity: reading the manifest with DtdProcessing.Ignore. textutil's manifest carries a DOCTYPE.
        BoundedZipPackage package = Open("textutil-basic.odt");

        foreach (string part in new[] { "mimetype", "content.xml", "styles.xml", "meta.xml", OpenDocumentManifest.EntryName })
        {
            Assert.True(package.Contains(part), $"{part} should be present.");
        }

        OpenDocumentManifest manifest = OpenDocumentManifest.Read(package, TextMime, CancellationToken.None);

        Assert.True(manifest.Declares("content.xml"));

        Assert.True(manifest.Declares("styles.xml"));

        Assert.Equal(TextMime, Encoding.ASCII.GetString(package.ReadEntry("mimetype", CancellationToken.None).Span));
    }

    [Fact]
    public void TextutilOdt_ManifestIsRefusedForAnotherOpenDocumentFormat()
    {
        BoundedZipPackage package = Open("textutil-basic.odt");

        DocumentPackageRejectedException exception = Assert.Throws<DocumentPackageRejectedException>(
            () => OpenDocumentManifest.Read(package, "application/vnd.oasis.opendocument.spreadsheet", CancellationToken.None));

        Assert.Equal(DocumentPackageRejection.FormatMismatch, exception.Rejection);
    }

    [Fact]
    public void TextutilOdt_ContentXmlParsesUnderTheStrictProfileAndCarriesTheText()
    {
        BoundedZipPackage package = Open("textutil-basic.odt");

        using BoundedXmlReader reader = BoundedXmlReader.Create(package.ReadEntry("content.xml", CancellationToken.None), CancellationToken.None);

        Assert.Contains("Alpha", TextOf(reader));
    }

    [Fact]
    public void TextutilOdt_IsNotAnOpcPackage()
    {
        Assert.Empty(OpcPackageRelationships.Read(Open("textutil-basic.odt"), string.Empty, CancellationToken.None));
    }

    [Fact]
    public void TextutilDocx_OfficeDocumentRelationshipResolvesToAnExistingEntryCarryingTheText()
    {
        BoundedZipPackage package = Open("textutil-basic.docx");

        OpcRelationship officeDocument = Assert.Single(
            OpcPackageRelationships.Read(package, string.Empty, CancellationToken.None),
            relationship => OpcPackageRelationships.IsOfficeDocumentType(relationship.Type, "officeDocument"));

        string? entry = OpcPackageRelationships.ResolveEntry(package, officeDocument.TargetEntryName);

        Assert.Equal("word/document.xml", entry);

        using BoundedXmlReader reader = BoundedXmlReader.Create(package.ReadEntry(entry!, CancellationToken.None), CancellationToken.None);

        Assert.Contains("Alpha", TextOf(reader));
    }

    [Fact]
    public void TextutilDocx_PartRelationshipsResolveRelativeAndParentTargetsToExistingEntries()
    {
        BoundedZipPackage package = Open("textutil-basic.docx");

        IReadOnlyList<OpcRelationship> relationships = OpcPackageRelationships.Read(package, "word/document.xml", CancellationToken.None);

        string[] targets = [.. relationships.Select(relationship => relationship.TargetEntryName).Order(StringComparer.Ordinal)];

        Assert.Equal(["docProps/meta.xml", "word/theme/theme1.xml"], targets);

        Assert.All(targets, target => Assert.NotNull(OpcPackageRelationships.ResolveEntry(package, target)));
    }

    [Fact]
    public void TextutilDocx_IsNotAnOpenDocumentPackage()
    {
        BoundedZipPackage package = Open("textutil-basic.docx");

        DocumentPackageRejectedException exception = Assert.Throws<DocumentPackageRejectedException>(
            () => OpenDocumentManifest.Read(package, TextMime, CancellationToken.None));

        Assert.Equal(DocumentPackageRejection.MissingPart, exception.Rejection);
    }

    private static BoundedZipPackage Open(string fileName) =>
        BoundedZipPackage.Open(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "Comprehension", fileName)), CancellationToken.None);

    private static string TextOf(BoundedXmlReader reader)
    {
        StringBuilder text = new();

        while (reader.Read())
        {
            if (reader.Reader.NodeType is XmlNodeType.Text or XmlNodeType.SignificantWhitespace)
            {
                text.Append(reader.Reader.Value);
            }
        }

        return text.ToString();
    }
}
