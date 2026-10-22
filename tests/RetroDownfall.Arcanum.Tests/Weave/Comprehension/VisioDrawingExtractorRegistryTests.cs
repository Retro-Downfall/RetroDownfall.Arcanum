using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Weave;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension;

/// <summary>
/// The Visio extractor through the real registry and host: both Visio MIME constants reach it, its text is what
/// the direct tests expect, and a refusal maps to the D4 status and its code-owned reason.
/// </summary>
public sealed class VisioDrawingExtractorRegistryTests
{
    [Fact]
    public async Task ExtractDocument_VisioDrawingMime_DispatchesToTheVisioExtractor()
    {
        byte[] package = new VisioPackageBuilder()
            .AddMaster(2, VisioFixture.Shape(5, "STENCIL-LABEL"))
            .AddPage("Page 1", "<Shape ID='1' Type='Shape' Master='2'/>" + VisioFixture.Shape(2, "Note"))
            .AddPage("Page 2", VisioFixture.Shape(1, "Plain"))
            .Build();

        SessionAttachmentExtractionResult result = await ExtractAsync(package, SessionAttachmentComprehensionPolicy.VisioDrawingMimeType);

        Assert.Equal(SessionAttachmentExtractionStatus.Extracted, result.Status);

        Assert.Equal("Page: Page 1\nSTENCIL-LABEL\nNote\nPage: Page 2\nPlain\n", result.Text);

        Assert.False(result.WasTruncated);
    }

    [Fact]
    public async Task ExtractDocument_VisioMacroEnabledMime_DispatchesToTheVisioExtractorAndNeverReadsTheMacros()
    {
        byte[] package = new VisioPackageBuilder()
            .MacroEnabled()
            .AddPage("Page-1", VisioFixture.Shape(1, "Macro drawing"))
            .AddRaw(VisioPackageBuilder.UninflatableEntry("visio/vbaProject.bin"))
            .Build();

        SessionAttachmentExtractionResult result = await ExtractAsync(package, SessionAttachmentComprehensionPolicy.VisioMacroEnabledDrawingMimeType);

        Assert.Equal(SessionAttachmentExtractionStatus.Extracted, result.Status);

        Assert.Equal("Page: Page-1\nMacro drawing\n", result.Text);
    }

    [Theory]
    [InlineData(" Application/Vnd.MS-Visio.Drawing ; charset=binary")]
    [InlineData("APPLICATION/VND.MS-VISIO.DRAWING.MACROENABLED.12")]
    public async Task ExtractDocument_VisioMimeWithCaseAndParameters_StillDispatches(string mimeType)
    {
        byte[] package = new VisioPackageBuilder().AddPage("Page-1", VisioFixture.Shape(1, "Text")).Build();

        SessionAttachmentExtractionResult result = await ExtractAsync(package, mimeType);

        Assert.Equal("Page: Page-1\nText\n", result.Text);
    }

    public static TheoryData<string, string, string> Refusals() => new()
    {
        {
            "docx",
            nameof(SessionAttachmentExtractionStatus.Failed),
            "Document package is missing a required part."
        },
        {
            "wrong-root",
            nameof(SessionAttachmentExtractionStatus.Failed),
            "Document package does not match its declared format."
        },
        {
            "malformed-page",
            nameof(SessionAttachmentExtractionStatus.Failed),
            "Document part is not well-formed XML or declares a DTD."
        },
        {
            "not-zip",
            nameof(SessionAttachmentExtractionStatus.Failed),
            "Document package is not a valid ZIP container."
        },
        {
            "encrypted",
            nameof(SessionAttachmentExtractionStatus.NotEligible),
            "Encrypted document packages are not supported."
        },
        {
            "too-many-pages",
            nameof(SessionAttachmentExtractionStatus.Failed),
            "Document structure exceeds its bounds."
        },
        {
            "too-many-masters",
            nameof(SessionAttachmentExtractionStatus.Failed),
            "Document structure exceeds its bounds."
        },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task ExtractDocument_RefusedPackage_MapsToTheD4StatusAndReasonWithNoText(
        string kind,
        string status,
        string reason)
    {
        SessionAttachmentExtractionResult result = await ExtractAsync(RefusedPackage(kind), SessionAttachmentComprehensionPolicy.VisioDrawingMimeType);

        Assert.Equal(status, result.Status.ToString());

        Assert.Equal(reason, result.FailureReason);

        Assert.Empty(result.Text);

        Assert.False(result.WasTruncated);
    }

    [Fact]
    public async Task ExtractDocument_RefusedVisioPackage_DoesNotQuarantineTheFormatForTheHealthyOneBehindIt()
    {
        byte[] poison = RefusedPackage("malformed-page");

        byte[] healthy = new VisioPackageBuilder().AddPage("Page-1", VisioFixture.Shape(1, "Healthy")).Build();

        SessionAttachmentExtractionResult rejected = await ExtractAsync(poison, SessionAttachmentComprehensionPolicy.VisioDrawingMimeType);

        SessionAttachmentExtractionResult accepted = await ExtractAsync(healthy, SessionAttachmentComprehensionPolicy.VisioDrawingMimeType);

        Assert.Equal(SessionAttachmentExtractionStatus.Failed, rejected.Status);

        Assert.Equal(SessionAttachmentExtractionStatus.Extracted, accepted.Status);

        Assert.Equal("Page: Page-1\nHealthy\n", accepted.Text);
    }

    [Fact]
    public async Task ExtractDocument_PackageWithNoTextAtAll_IsNotEligible()
    {
        // A drawing with no pages in pages.xml projects nothing, which the host reports as NotEligible (empty output).
        byte[] package = new VisioPackageBuilder().AddPage("Only", string.Empty).Replace("visio/pages/pages.xml", VisioFixture.Xml("Pages", string.Empty)).Build();

        SessionAttachmentExtractionResult result = await ExtractAsync(package, SessionAttachmentComprehensionPolicy.VisioDrawingMimeType);

        Assert.Equal(SessionAttachmentExtractionStatus.NotEligible, result.Status);

        Assert.Empty(result.Text);
    }

    private static byte[] RefusedPackage(string kind)
    {
        VisioPackageBuilder page = new VisioPackageBuilder().AddPage("Page-1", VisioFixture.Shape(1, "Body"));

        switch (kind)
        {
            case "docx":
                return ComprehensionPackageBuilder.Build(
                    ("_rels/.rels", VisioPackageBuilder.Rels(("rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument", "word/document.xml", false))),
                    ("word/document.xml", "<w:document xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main'/>"));

            case "wrong-root":
                return page.Replace("visio/document.xml", VisioFixture.Xml("Pages", string.Empty)).Build();

            case "malformed-page":
                return page.Replace("visio/pages/page1.xml", $"<PageContents xmlns='{VisioFixture.Main}'><Shapes><Shape>").Build();

            case "not-zip":
                return System.Text.Encoding.UTF8.GetBytes("not a zip");

            case "encrypted":
                return [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, .. new byte[600]];

            case "too-many-pages":
                // One page part listed once per page: the package stays small while the page count passes the bound.
                return page.Replace(
                    "visio/pages/pages.xml",
                    VisioFixture.Xml("Pages", string.Concat(Enumerable.Repeat("<Page ID='0' Name='P'><Rel r:id='rId1'/></Page>", VisioDrawingExtractor.MaximumPages + 1)))).Build();

            case "too-many-masters":
                return page.AddMaster(1, string.Empty).Replace(
                    "visio/masters/masters.xml",
                    VisioFixture.Xml("Masters", string.Concat(Enumerable.Repeat("<Master ID='1'><Rel r:id='rId1'/></Master>", VisioDrawingExtractor.MaximumMasters + 1)))).Build();

            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    private static Task<SessionAttachmentExtractionResult> ExtractAsync(byte[] bytes, string mimeType)
    {
        SessionAttachmentRecord record = new(
            Guid.NewGuid(), Guid.NewGuid(), null, null, SessionAttachmentState.Bound,
            "drawing", "drawing.vsdx", 1, "untrusted/path", "hash",
            mimeType, bytes.Length, SessionAttachmentKind.Binary,
            DateTimeOffset.UtcNow);

        ArcanumSettings settings = new()
        {
            Features = new FeatureSettings { AttachmentRetrieval = true, Comprehension = true },
        };

        return SessionAttachmentTextExtractor.ExtractDocumentAsync(
            new NoOpSessionAttachmentStore(readBytes: (_, _) => Task.FromResult<ReadOnlyMemory<byte>>(bytes)),
            record,
            settings,
            1_048_576,
            CancellationToken.None);
    }
}
