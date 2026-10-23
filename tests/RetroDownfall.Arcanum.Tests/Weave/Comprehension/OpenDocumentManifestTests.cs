using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension;

public sealed class OpenDocumentManifestTests
{
    private const string TextMime = "application/vnd.oasis.opendocument.text";

    private const string SpreadsheetMime = "application/vnd.oasis.opendocument.spreadsheet";

    private const string ManifestNamespace = "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0";

    private const string TextutilDocumentType = "<!DOCTYPE manifest:manifest PUBLIC \"-//OpenOffice.org//DTD Manifest 1.0//EN\" \"Manifest.dtd\">";

    // ---------------------------------------------------------------- accepted manifests

    [Fact]
    public void Read_TextutilStyleManifestWithTheOpenOfficeDocumentType_IsAccepted()
    {
        // Sensitivity: the manifest is read with DtdProcessing.Ignore (plan D6). macOS textutil writes this DOCTYPE.
        string manifest = Manifest(
            Entry("/", TextMime) + Entry("content.xml", "text/xml") + Entry("styles.xml", "text/xml") + Entry("meta.xml", "text/xml"),
            TextutilDocumentType);

        OpenDocumentManifest result = OpenDocumentManifest.Read(Package(manifest), TextMime, CancellationToken.None);

        Assert.True(result.Declares("content.xml"));

        Assert.True(result.Declares("styles.xml"));

        Assert.True(result.Declares("meta.xml"));
    }

    [Fact]
    public void Read_ManifestWithoutADocumentType_IsAccepted()
    {
        string manifest = Manifest(Entry("/", TextMime) + Entry("content.xml", "text/xml"));

        Assert.True(OpenDocumentManifest.Read(Package(manifest), TextMime, CancellationToken.None).Declares("content.xml"));
    }

    [Fact]
    public void Declares_ComparesPathsCaseInsensitivelyAndOnlyDeclaredOnes()
    {
        string manifest = Manifest(Entry("/", TextMime) + Entry("content.xml", "text/xml") + Entry("Thumbnails/thumbnail.png", "image/png"));

        OpenDocumentManifest result = OpenDocumentManifest.Read(Package(manifest), TextMime, CancellationToken.None);

        Assert.True(result.Declares("CONTENT.XML"));

        Assert.True(result.Declares("thumbnails/THUMBNAIL.png"));

        Assert.True(result.Declares("/"));

        Assert.False(result.Declares("styles.xml"));

        Assert.False(result.Declares("settings.xml"));
    }

    [Fact]
    public void Read_RootMediaTypeComparison_IgnoresCaseAndParameters()
    {
        string manifest = Manifest(Entry("/", "Application/VND.oasis.opendocument.TEXT; version=1.3") + Entry("content.xml", "text/xml"));

        Assert.True(OpenDocumentManifest.Read(Package(manifest), TextMime, CancellationToken.None).Declares("content.xml"));
    }

    [Fact]
    public void Read_ManifestAtExactlyTheMaximumEntryCount_IsAccepted()
    {
        string manifest = Manifest(Entry("/", TextMime) + Entry("content.xml", "text/xml") + ManyEntries(OpenDocumentManifest.MaximumEntries - 2));

        Assert.True(OpenDocumentManifest.Read(Package(manifest), TextMime, CancellationToken.None).Declares("content.xml"));
    }

    // ---------------------------------------------------------------- encryption

    [Fact]
    public void Read_EncryptionData_IsEncryptedWithoutSurfacingAnyCipherMaterial()
    {
        // Sensitivity: the "encryption-data" element check. Only a closed status leaves the reader; the checksum and
        // salt text stay inside the manifest part.
        string encrypted = Entry("content.xml", "text/xml", "<manifest:encryption-data manifest:checksum-type=\"SHA1/1K\" manifest:checksum=\"CIPHERSECRET\">"
            + "<manifest:algorithm manifest:algorithm-name=\"http://www.w3.org/2001/04/xmlenc#aes256-cbc\" manifest:initialisation-vector=\"CIPHERSECRET\"/>"
            + "<manifest:key-derivation manifest:key-derivation-name=\"PBKDF2\" manifest:salt=\"CIPHERSECRET\" manifest:iteration-count=\"100000\"/>"
            + "</manifest:encryption-data>");

        string manifest = Manifest(Entry("/", TextMime) + encrypted);

        DocumentPackageRejectedException exception = Rejected(DocumentPackageRejection.Encrypted, () => OpenDocumentManifest.Read(Package(manifest), TextMime, CancellationToken.None));

        Assert.DoesNotContain("CIPHERSECRET", exception.Message);
    }

    [Fact]
    public void Read_KeyInfo_IsEncrypted()
    {
        string manifest = Manifest(Entry("/", TextMime) + Entry("content.xml", "text/xml", "<manifest:keyinfo><manifest:encrypted-key/></manifest:keyinfo>"));

        Rejected(DocumentPackageRejection.Encrypted, () => OpenDocumentManifest.Read(Package(manifest), TextMime, CancellationToken.None));
    }

    [Fact]
    public void Read_EncryptionElementInAnotherNamespace_IsStillEncrypted()
    {
        string manifest = Manifest(Entry("/", TextMime) + Entry("content.xml", "text/xml", "<x:encryption-data xmlns:x=\"urn:somewhere:else\"/>"));

        Rejected(DocumentPackageRejection.Encrypted, () => OpenDocumentManifest.Read(Package(manifest), TextMime, CancellationToken.None));
    }

    [Fact]
    public void Read_EncryptedManifestWithAWrongRootMediaType_IsEncryptedNotFormatMismatch()
    {
        string manifest = Manifest(Entry("/", SpreadsheetMime) + Entry("content.xml", "text/xml", "<manifest:encryption-data/>"));

        Rejected(DocumentPackageRejection.Encrypted, () => OpenDocumentManifest.Read(Package(manifest), TextMime, CancellationToken.None));
    }

    // ---------------------------------------------------------------- format mismatch

    [Fact]
    public void Read_RootMediaTypeOfAnotherFormat_IsFormatMismatch()
    {
        string manifest = Manifest(Entry("/", SpreadsheetMime) + Entry("content.xml", "text/xml"));

        Rejected(DocumentPackageRejection.FormatMismatch, () => OpenDocumentManifest.Read(Package(manifest), TextMime, CancellationToken.None));
    }

    [Fact]
    public void Read_NoRootEntry_IsFormatMismatch()
    {
        string manifest = Manifest(Entry("content.xml", "text/xml"));

        Rejected(DocumentPackageRejection.FormatMismatch, () => OpenDocumentManifest.Read(Package(manifest), TextMime, CancellationToken.None));
    }

    [Fact]
    public void Read_RootEntryWithoutAMediaType_IsFormatMismatch()
    {
        string manifest = Manifest("<manifest:file-entry manifest:full-path=\"/\"/>" + Entry("content.xml", "text/xml"));

        Rejected(DocumentPackageRejection.FormatMismatch, () => OpenDocumentManifest.Read(Package(manifest), TextMime, CancellationToken.None));
    }

    [Fact]
    public void Read_ContradictoryRootEntries_AreFormatMismatchWhicheverComesFirst()
    {
        // Sensitivity: every root entry must match ("rootsMatch &="), not just the first one seen.
        string matchingFirst = Manifest(Entry("/", TextMime) + Entry("/", SpreadsheetMime) + Entry("content.xml", "text/xml"));

        string matchingLast = Manifest(Entry("/", SpreadsheetMime) + Entry("/", TextMime) + Entry("content.xml", "text/xml"));

        Rejected(DocumentPackageRejection.FormatMismatch, () => OpenDocumentManifest.Read(Package(matchingFirst), TextMime, CancellationToken.None));

        Rejected(DocumentPackageRejection.FormatMismatch, () => OpenDocumentManifest.Read(Package(matchingLast), TextMime, CancellationToken.None));
    }

    [Fact]
    public void Read_RootEntryInAnotherNamespace_IsNotARootEntry()
    {
        string manifest = Manifest($"<x:file-entry xmlns:x=\"urn:somewhere:else\" x:full-path=\"/\" x:media-type=\"{TextMime}\"/>" + Entry("content.xml", "text/xml"));

        Rejected(DocumentPackageRejection.FormatMismatch, () => OpenDocumentManifest.Read(Package(manifest), TextMime, CancellationToken.None));
    }

    [Fact]
    public void Read_TheMimetypeEntryIsNotTrusted_OnlyTheManifestDecidesTheFormat()
    {
        string manifest = Manifest(Entry("/", SpreadsheetMime) + Entry("content.xml", "text/xml"));

        byte[] bytes = ComprehensionPackageBuilder.BuildStored(("mimetype", TextMime), (OpenDocumentManifest.EntryName, manifest), ("content.xml", "<c/>"));

        BoundedZipPackage package = BoundedZipPackage.Open(bytes, CancellationToken.None);

        Rejected(DocumentPackageRejection.FormatMismatch, () => OpenDocumentManifest.Read(package, TextMime, CancellationToken.None));
    }

    // ---------------------------------------------------------------- missing parts

    [Fact]
    public void Read_NoManifestEntry_IsMissingPart()
    {
        BoundedZipPackage package = BoundedZipPackage.Open(ComprehensionPackageBuilder.Build(("content.xml", "<c/>")), CancellationToken.None);

        Rejected(DocumentPackageRejection.MissingPart, () => OpenDocumentManifest.Read(package, TextMime, CancellationToken.None));
    }

    [Fact]
    public void Read_ContentXmlNotDeclared_IsMissingPart()
    {
        string manifest = Manifest(Entry("/", TextMime) + Entry("styles.xml", "text/xml"));

        Rejected(DocumentPackageRejection.MissingPart, () => OpenDocumentManifest.Read(Package(manifest), TextMime, CancellationToken.None));
    }

    [Fact]
    public void Read_ContentXmlDeclaredButAbsentFromThePackage_IsMissingPart()
    {
        string manifest = Manifest(Entry("/", TextMime) + Entry("content.xml", "text/xml"));

        BoundedZipPackage package = BoundedZipPackage.Open(ComprehensionPackageBuilder.Build((OpenDocumentManifest.EntryName, manifest)), CancellationToken.None);

        Rejected(DocumentPackageRejection.MissingPart, () => OpenDocumentManifest.Read(package, TextMime, CancellationToken.None));
    }

    // ---------------------------------------------------------------- bounds and XML hardening

    [Fact]
    public void Read_MoreThanTheMaximumManifestEntries_IsStructureBounds()
    {
        // Sensitivity: the MaximumEntries guard.
        string manifest = Manifest(Entry("/", TextMime) + Entry("content.xml", "text/xml") + ManyEntries(OpenDocumentManifest.MaximumEntries - 1));

        Rejected(DocumentPackageRejection.StructureBounds, () => OpenDocumentManifest.Read(Package(manifest), TextMime, CancellationToken.None));
    }

    [Fact]
    public void Read_InternalEntityInTheManifest_IsMalformedXmlAndIsNeverExpanded()
    {
        // Sensitivity: DtdProcessing.Ignore (not Parse). With Parse, &x; would expand to "content.xml" and this
        // manifest would be accepted; with Ignore the entity is undeclared and the part fails.
        string manifest = "<?xml version=\"1.0\"?><!DOCTYPE manifest:manifest [<!ENTITY x \"content.xml\">]>"
            + $"<manifest:manifest xmlns:manifest=\"{ManifestNamespace}\">{Entry("/", TextMime)}<manifest:file-entry manifest:full-path=\"&x;\" manifest:media-type=\"text/xml\"/></manifest:manifest>";

        Rejected(DocumentPackageRejection.MalformedXml, () => OpenDocumentManifest.Read(Package(manifest), TextMime, CancellationToken.None));
    }

    [Fact]
    public void Read_MalformedManifest_IsMalformedXml()
    {
        Rejected(DocumentPackageRejection.MalformedXml, () => OpenDocumentManifest.Read(Package("<manifest:manifest"), TextMime, CancellationToken.None));
    }

    [Fact]
    public void Read_ManifestNestedPastTheXmlDepthBound_IsStructureBounds()
    {
        string deep = string.Concat(Enumerable.Repeat("<a>", 300)) + string.Concat(Enumerable.Repeat("</a>", 300));

        string manifest = Manifest(Entry("/", TextMime) + Entry("content.xml", "text/xml") + deep);

        Rejected(DocumentPackageRejection.StructureBounds, () => OpenDocumentManifest.Read(Package(manifest), TextMime, CancellationToken.None));
    }

    // ---------------------------------------------------------------- helpers

    private static string Manifest(string entries, string documentType = "") =>
        $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>{documentType}<manifest:manifest xmlns:manifest=\"{ManifestNamespace}\" manifest:version=\"1.2\">{entries}</manifest:manifest>";

    private static string Entry(string path, string mediaType, string children = "") =>
        children.Length == 0
            ? $"<manifest:file-entry manifest:full-path=\"{path}\" manifest:media-type=\"{mediaType}\"/>"
            : $"<manifest:file-entry manifest:full-path=\"{path}\" manifest:media-type=\"{mediaType}\">{children}</manifest:file-entry>";

    private static string ManyEntries(int count) =>
        string.Concat(Enumerable.Range(0, count).Select(index => Entry($"p{index}", "text/xml")));

    private static BoundedZipPackage Package(string manifest) =>
        BoundedZipPackage.Open(
            ComprehensionPackageBuilder.Build((OpenDocumentManifest.EntryName, manifest), ("content.xml", "<c/>"), ("styles.xml", "<s/>")),
            CancellationToken.None);

    private static DocumentPackageRejectedException Rejected(DocumentPackageRejection expected, Func<object?> act)
    {
        DocumentPackageRejectedException exception = Assert.Throws<DocumentPackageRejectedException>(act);

        Assert.Equal(expected, exception.Rejection);

        Assert.Equal(DocumentPackageRejections.Reason(expected), exception.Message);

        Assert.Null(exception.InnerException);

        return exception;
    }
}
