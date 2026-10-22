using System.Text;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension;

public sealed class OpcPackageRelationshipsTests
{
    private const string OfficeDocument = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument";

    private const string Styles = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles";

    // ---------------------------------------------------------------- naming

    [Theory]
    [InlineData("", "_rels/.rels")]
    [InlineData("word/document.xml", "word/_rels/document.xml.rels")]
    [InlineData("document.xml", "_rels/document.xml.rels")]
    [InlineData("ppt/slides/slide1.xml", "ppt/slides/_rels/slide1.xml.rels")]
    public void RelationshipEntryName_PlacesTheRelsPartBesideTheSource(string source, string expected)
    {
        Assert.Equal(expected, OpcPackageRelationships.RelationshipEntryName(source));
    }

    // ---------------------------------------------------------------- reading

    [Fact]
    public void Read_PackageRootRelationships_ReturnsIdTypeAndRootRelativeTarget()
    {
        BoundedZipPackage package = Package(("_rels/.rels", Rels($"<Relationship Id=\"rId1\" Type=\"{OfficeDocument}\" Target=\"word/document.xml\"/>")));

        IReadOnlyList<OpcRelationship> relationships = OpcPackageRelationships.Read(package, string.Empty, CancellationToken.None);

        OpcRelationship relationship = Assert.Single(relationships);

        Assert.Equal(new OpcRelationship("rId1", OfficeDocument, "word/document.xml"), relationship);
    }

    [Fact]
    public void Read_AbsentRelationshipPart_ReturnsAnEmptyList()
    {
        BoundedZipPackage package = Package(
            ("_rels/.rels", Rels($"<Relationship Id=\"rId1\" Type=\"{OfficeDocument}\" Target=\"word/document.xml\"/>")),
            ("word/document.xml", "<d/>"));

        Assert.Empty(OpcPackageRelationships.Read(package, "word/document.xml", CancellationToken.None));
    }

    [Fact]
    public void Read_TargetThatDoesNotExistInThePackage_IsStillReturned()
    {
        BoundedZipPackage package = Package(("_rels/.rels", Rels("<Relationship Id=\"rId1\" Type=\"t\" Target=\"nowhere.xml\"/>")));

        Assert.Equal("nowhere.xml", Assert.Single(OpcPackageRelationships.Read(package, string.Empty, CancellationToken.None)).TargetEntryName);
    }

    [Theory]
    [InlineData("styles.xml", "word/styles.xml")]
    [InlineData("./numbering.xml", "word/numbering.xml")]
    [InlineData("theme/theme1.xml", "word/theme/theme1.xml")]
    [InlineData("media/../theme/theme1.xml", "word/theme/theme1.xml")]
    [InlineData("../customXml/item1.xml", "customXml/item1.xml")]
    [InlineData("/docProps/core.xml", "docProps/core.xml")]
    [InlineData("/word/../xl/workbook.xml", "xl/workbook.xml")]
    [InlineData("styles.xml#anchor", "word/styles.xml")]
    public void Read_TargetFromAPart_ResolvesRelativeToThePartsFolder(string target, string expected)
    {
        BoundedZipPackage package = Package(("word/_rels/document.xml.rels", Rels($"<Relationship Id=\"rId1\" Type=\"{Styles}\" Target=\"{target}\"/>")));

        Assert.Equal(expected, Assert.Single(OpcPackageRelationships.Read(package, "word/document.xml", CancellationToken.None)).TargetEntryName);
    }

    [Fact]
    public void Read_TargetFromThePackageRoot_ResolvesRelativeToTheRoot()
    {
        BoundedZipPackage package = Package(("_rels/.rels", Rels("<Relationship Id=\"rId1\" Type=\"t\" Target=\"docProps/../word/document.xml\"/>")));

        Assert.Equal("word/document.xml", Assert.Single(OpcPackageRelationships.Read(package, string.Empty, CancellationToken.None)).TargetEntryName);
    }

    [Theory]
    [InlineData("word/_rels/document.xml.rels", "word/document.xml", "../../evil.xml")]
    [InlineData("word/_rels/document.xml.rels", "word/document.xml", "../../../evil.xml")]
    [InlineData("word/_rels/document.xml.rels", "word/document.xml", "/../evil.xml")]
    [InlineData("_rels/.rels", "", "../evil.xml")]
    [InlineData("_rels/.rels", "", "a/../../evil.xml")]
    public void Read_TargetEscapingThePackageRoot_IsUnsafeEntryName(string relsName, string source, string target)
    {
        // Sensitivity: the "pop from an empty stack" refusal while normalizing '..' segments.
        BoundedZipPackage package = Package((relsName, Rels($"<Relationship Id=\"rId1\" Type=\"t\" Target=\"{target}\"/>")));

        DocumentPackageRejectedException exception = Rejected(DocumentPackageRejection.UnsafeEntryName, () => OpcPackageRelationships.Read(package, source, CancellationToken.None));

        Assert.DoesNotContain("evil", exception.ToString());
    }

    [Theory]
    [InlineData("C:/evil.xml")]
    [InlineData("a\\evil.xml")]
    [InlineData("a//evil.xml")]
    [InlineData("dir/evil/")]
    [InlineData(".")]
    [InlineData("a&#9;evil.xml")]
    public void Read_InternalTargetThatCouldNeverBeAnEntryName_IsUnsafeEntryName(string target)
    {
        BoundedZipPackage package = Package(("_rels/.rels", Rels($"<Relationship Id=\"rId1\" Type=\"t\" Target=\"{target}\"/>")));

        Rejected(DocumentPackageRejection.UnsafeEntryName, () => OpcPackageRelationships.Read(package, string.Empty, CancellationToken.None));
    }

    [Fact]
    public void Read_ExternalTargets_AreOmittedAndNeverValidatedOrFollowed()
    {
        // Sensitivity: the TargetMode="External" skip. The external targets would otherwise be unsafe names.
        BoundedZipPackage package = Package(("_rels/.rels", Rels(
            "<Relationship Id=\"rId1\" Type=\"t\" Target=\"http://evil.example/x.xml\" TargetMode=\"External\"/>",
            "<Relationship Id=\"rId2\" Type=\"t\" Target=\"../../outside.xml\" TargetMode=\"external\"/>",
            "<Relationship Id=\"rId3\" Type=\"t\" Target=\"word/document.xml\" TargetMode=\"Internal\"/>",
            "<Relationship Id=\"rId4\" Type=\"t\" Target=\"word/other.xml\"/>")));

        IReadOnlyList<OpcRelationship> relationships = OpcPackageRelationships.Read(package, string.Empty, CancellationToken.None);

        Assert.Equal(["rId3", "rId4"], relationships.Select(relationship => relationship.Id));
    }

    [Fact]
    public void Read_FragmentOnlyTarget_IsMalformedXml()
    {
        BoundedZipPackage package = Package(("_rels/.rels", Rels("<Relationship Id=\"rId1\" Type=\"t\" Target=\"#frag\"/>")));

        Rejected(DocumentPackageRejection.MalformedXml, () => OpcPackageRelationships.Read(package, string.Empty, CancellationToken.None));
    }

    [Fact]
    public void Read_DuplicateId_IsMalformedXml()
    {
        BoundedZipPackage package = Package(("_rels/.rels", Rels(
            "<Relationship Id=\"rId1\" Type=\"t\" Target=\"a.xml\"/>",
            "<Relationship Id=\"rId1\" Type=\"t\" Target=\"b.xml\"/>")));

        Rejected(DocumentPackageRejection.MalformedXml, () => OpcPackageRelationships.Read(package, string.Empty, CancellationToken.None));
    }

    [Fact]
    public void Read_DuplicateIdWhereOneIsExternal_IsStillMalformedXml()
    {
        BoundedZipPackage package = Package(("_rels/.rels", Rels(
            "<Relationship Id=\"rId1\" Type=\"t\" Target=\"http://x/y\" TargetMode=\"External\"/>",
            "<Relationship Id=\"rId1\" Type=\"t\" Target=\"b.xml\"/>")));

        Rejected(DocumentPackageRejection.MalformedXml, () => OpcPackageRelationships.Read(package, string.Empty, CancellationToken.None));
    }

    [Fact]
    public void Read_IdsDifferingOnlyInCase_AreDistinct()
    {
        BoundedZipPackage package = Package(("_rels/.rels", Rels(
            "<Relationship Id=\"rId1\" Type=\"t\" Target=\"a.xml\"/>",
            "<Relationship Id=\"RID1\" Type=\"t\" Target=\"b.xml\"/>")));

        Assert.Equal(2, OpcPackageRelationships.Read(package, string.Empty, CancellationToken.None).Count);
    }

    [Theory]
    [InlineData("<Relationship Type=\"t\" Target=\"a.xml\"/>")]
    [InlineData("<Relationship Id=\"rId1\" Target=\"a.xml\"/>")]
    [InlineData("<Relationship Id=\"rId1\" Type=\"t\"/>")]
    [InlineData("<Relationship Id=\"\" Type=\"t\" Target=\"a.xml\"/>")]
    [InlineData("<Relationship Id=\"rId1\" Type=\"\" Target=\"a.xml\"/>")]
    [InlineData("<Relationship Id=\"rId1\" Type=\"t\" Target=\"\"/>")]
    public void Read_RelationshipMissingARequiredAttribute_IsMalformedXml(string relationship)
    {
        BoundedZipPackage package = Package(("_rels/.rels", Rels(relationship)));

        Rejected(DocumentPackageRejection.MalformedXml, () => OpcPackageRelationships.Read(package, string.Empty, CancellationToken.None));
    }

    [Fact]
    public void Read_ExactlyTheMaximumRelationshipCount_IsAllowed()
    {
        BoundedZipPackage package = Package(("_rels/.rels", Rels(ManyRelationships(OpcPackageRelationships.MaximumRelationships))));

        Assert.Equal(OpcPackageRelationships.MaximumRelationships, OpcPackageRelationships.Read(package, string.Empty, CancellationToken.None).Count);
    }

    [Fact]
    public void Read_MoreThanTheMaximumRelationshipCount_IsStructureBounds()
    {
        // Sensitivity: the MaximumRelationships guard. External relationships count too.
        BoundedZipPackage package = Package(("_rels/.rels", Rels(ManyRelationships(OpcPackageRelationships.MaximumRelationships + 1))));

        Rejected(DocumentPackageRejection.StructureBounds, () => OpcPackageRelationships.Read(package, string.Empty, CancellationToken.None));
    }

    [Fact]
    public void Read_ExternalRelationshipsCountTowardTheMaximum()
    {
        string many = string.Concat(Enumerable.Range(0, OpcPackageRelationships.MaximumRelationships + 1)
            .Select(index => $"<Relationship Id=\"r{index}\" Type=\"t\" Target=\"http://x/{index}\" TargetMode=\"External\"/>"));

        BoundedZipPackage package = Package(("_rels/.rels", Rels(many)));

        Rejected(DocumentPackageRejection.StructureBounds, () => OpcPackageRelationships.Read(package, string.Empty, CancellationToken.None));
    }

    [Fact]
    public void Read_RelationshipElementsOutsideTheRelationshipsNamespace_AreIgnored()
    {
        BoundedZipPackage package = Package((
            "_rels/.rels",
            "<Relationships xmlns=\"urn:not:opc\"><Relationship Id=\"rId1\" Type=\"t\" Target=\"a.xml\"/></Relationships>"));

        Assert.Empty(OpcPackageRelationships.Read(package, string.Empty, CancellationToken.None));
    }

    [Fact]
    public void Read_RelationshipPartWithADocumentType_IsMalformedXml()
    {
        // Sensitivity: the relationship part is read with DtdProcessing.Prohibit, never Ignore.
        BoundedZipPackage package = Package((
            "_rels/.rels",
            "<!DOCTYPE Relationships [<!ENTITY x \"INJECTED\">]><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"/>"));

        Rejected(DocumentPackageRejection.MalformedXml, () => OpcPackageRelationships.Read(package, string.Empty, CancellationToken.None));
    }

    [Fact]
    public void Read_MalformedRelationshipPart_IsMalformedXml()
    {
        BoundedZipPackage package = Package(("_rels/.rels", "<Relationships><Relationship"));

        Rejected(DocumentPackageRejection.MalformedXml, () => OpcPackageRelationships.Read(package, string.Empty, CancellationToken.None));
    }

    // ---------------------------------------------------------------- ResolveEntry

    [Fact]
    public void ResolveEntry_LiteralName_ReturnsItCaseInsensitively()
    {
        BoundedZipPackage package = Package(("word/Document.xml", "<d/>"));

        Assert.Equal("WORD/document.XML", OpcPackageRelationships.ResolveEntry(package, "WORD/document.XML"));
    }

    [Fact]
    public void ResolveEntry_PercentEscapedTarget_ResolvesThroughOneUnescape()
    {
        BoundedZipPackage package = Package(("word/my doc.xml", "<d/>"), ("word/100%.xml", "<p/>"));

        Assert.Equal("word/my doc.xml", OpcPackageRelationships.ResolveEntry(package, "word/my%20doc.xml"));

        Assert.Equal("word/100%.xml", OpcPackageRelationships.ResolveEntry(package, "word/100%25.xml"));
    }

    [Fact]
    public void ResolveEntry_PrefersTheLiteralNameOverTheUnescapedOne()
    {
        BoundedZipPackage package = Package(("word/a%20b.xml", "<literal/>"), ("word/a b.xml", "<unescaped/>"));

        Assert.Equal("word/a%20b.xml", OpcPackageRelationships.ResolveEntry(package, "word/a%20b.xml"));
    }

    [Theory]
    [InlineData("word/missing.xml")]
    [InlineData("word/%2e%2e/secret.xml")]
    [InlineData("word%5Cdocument.xml")]
    [InlineData("word/document%252Exml")]
    public void ResolveEntry_NameThatIsNotInThePackageEvenAfterOneUnescape_IsNull(string target)
    {
        BoundedZipPackage package = Package(("word/document.xml", "<d/>"), ("secret.xml", "<s/>"));

        Assert.Null(OpcPackageRelationships.ResolveEntry(package, target));
    }

    // ---------------------------------------------------------------- relationship type helpers

    [Theory]
    [InlineData("http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument", "officeDocument", true)]
    [InlineData("http://purl.oclc.org/ooxml/officeDocument/relationships/officeDocument", "officeDocument", true)]
    [InlineData("http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet", "worksheet", true)]
    [InlineData("http://purl.oclc.org/ooxml/officeDocument/relationships/slide", "slide", true)]
    [InlineData("http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet", "officeDocument", false)]
    [InlineData("http://schemas.openxmlformats.org/officeDocument/2006/relationships/OfficeDocument", "officeDocument", false)]
    [InlineData("http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument/", "officeDocument", false)]
    [InlineData("HTTP://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument", "officeDocument", false)]
    [InlineData("http://example.com/officeDocument/2006/relationships/officeDocument", "officeDocument", false)]
    [InlineData("officeDocument", "officeDocument", false)]
    [InlineData("", "officeDocument", false)]
    public void IsOfficeDocumentType_MatchesTransitionalAndStrictUrisExactly(string type, string localName, bool expected)
    {
        Assert.Equal(expected, OpcPackageRelationships.IsOfficeDocumentType(type, localName));
    }

    [Theory]
    [InlineData("http://schemas.microsoft.com/visio/2010/relationships/document", "document", true)]
    [InlineData("http://schemas.microsoft.com/visio/2010/relationships/pages", "pages", true)]
    [InlineData("http://schemas.microsoft.com/visio/2010/relationships/pages", "document", false)]
    [InlineData("http://schemas.microsoft.com/visio/2010/relationships/Document", "document", false)]
    [InlineData("http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument", "officeDocument", false)]
    [InlineData("", "document", false)]
    public void IsVisioType_MatchesTheVisioRelationshipNamespaceExactly(string type, string localName, bool expected)
    {
        Assert.Equal(expected, OpcPackageRelationships.IsVisioType(type, localName));
    }

    // ---------------------------------------------------------------- helpers

    private static BoundedZipPackage Package(params (string Name, string Content)[] entries) =>
        BoundedZipPackage.Open(ComprehensionPackageBuilder.Build(entries), CancellationToken.None);

    private static string Rels(params string[] relationships) =>
        $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">{string.Concat(relationships)}</Relationships>";

    private static string ManyRelationships(int count) =>
        string.Concat(Enumerable.Range(0, count).Select(index => $"<Relationship Id=\"r{index}\" Type=\"t\" Target=\"a{index}\"/>"));

    private static DocumentPackageRejectedException Rejected(DocumentPackageRejection expected, Func<object?> act)
    {
        DocumentPackageRejectedException exception = Assert.Throws<DocumentPackageRejectedException>(act);

        Assert.Equal(expected, exception.Rejection);

        Assert.Equal(DocumentPackageRejections.Reason(expected), exception.Message);

        Assert.Null(exception.InnerException);

        return exception;
    }
}
