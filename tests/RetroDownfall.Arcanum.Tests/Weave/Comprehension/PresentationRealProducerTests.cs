using System.IO.Compression;
using System.Text;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension;

/// <summary>
/// The PowerPoint extractor against a deck a real producer wrote (python-pptx; see
/// <c>TestData/Comprehension/PowerPoint/README.md</c>), so the rules are proven on bytes this repository did not build.
/// </summary>
public sealed class PresentationRealProducerTests
{
    private const string FixtureName = "python-pptx-three-slides.pptx";

    private const string ExpectedText =
        "Slide 1\nQuarterly review\nRevenue grew\nCosts fell\nSlide 3\nResults\nItem\tCount\nWidgets\t42\nFooter note\n";

    [Fact]
    public void Fixture_CarriesEveryHiddenMarkerTheExtractionMustHonour()
    {
        // Guards the fixture itself: if it were regenerated without these markers the tests below would pass vacuously.
        BoundedZipPackage package = BoundedZipPackage.Open(Fixture(), CancellationToken.None);

        Assert.Contains("show=\"0\"", EntryText(package, "ppt/slides/slide2.xml"), StringComparison.Ordinal);

        Assert.Contains("HIDDEN SLIDE BODY", EntryText(package, "ppt/slides/slide2.xml"), StringComparison.Ordinal);

        Assert.Contains("hidden=\"1\"", EntryText(package, "ppt/slides/slide3.xml"), StringComparison.Ordinal);

        Assert.Contains("HIDDEN SHAPE TEXT", EntryText(package, "ppt/slides/slide3.xml"), StringComparison.Ordinal);

        Assert.Contains("TABLE ALT TEXT", EntryText(package, "ppt/slides/slide3.xml"), StringComparison.Ordinal);

        Assert.Contains("SPEAKER NOTES ONE", EntryText(package, "ppt/notesSlides/notesSlide1.xml"), StringComparison.Ordinal);

        Assert.Contains("SPEAKER NOTES THREE", EntryText(package, "ppt/notesSlides/notesSlide2.xml"), StringComparison.Ordinal);

        Assert.Contains("Click to edit Master title style", EntryText(package, "ppt/slideMasters/slideMaster1.xml"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Extract_PythonPptxDeck_ProjectsOnlyWhatTheSlideShowDisplays()
    {
        string text = await PresentationDeck.TextOfAsync(Fixture());

        Assert.Equal(ExpectedText, text);

        foreach (string forbidden in new[] { "HIDDEN", "SPEAKER NOTES", "ALT TEXT", "Click to edit", "Table 2", "TextBox" })
        {
            Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Extract_PythonPptxDeckRepackagedAsMacroEnabled_IgnoresAMacroProjectItNeverInflates()
    {
        RawZipPackage package = new();

        using (MemoryStream source = new(Fixture()))
        using (ZipArchive archive = new(source, ZipArchiveMode.Read))
        {
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                using Stream stream = entry.Open();

                using MemoryStream content = new();

                stream.CopyTo(content);

                byte[] bytes = content.ToArray();

                if (entry.FullName == "ppt/_rels/presentation.xml.rels")
                {
                    bytes = ComprehensionPackageBuilder.Utf8Bytes(Encoding.UTF8.GetString(bytes).Replace(
                        "</Relationships>",
                        PresentationDeck.Rel("rId99", "http://schemas.microsoft.com/office/2006/relationships/vbaProject", "vbaProject.bin") + "</Relationships>",
                        StringComparison.Ordinal));
                }

                package.Entries.Add(RawZipEntry.Deflated(entry.FullName, bytes));
            }
        }

        package.Entries.Add(new RawZipEntry(ComprehensionPackageBuilder.Utf8Bytes("ppt/vbaProject.bin"), new byte[64], 8)
        {
            DataAreaBytes = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF],
        });

        Assert.Equal(ExpectedText, await PresentationDeck.TextOfAsync(package.ToBytes()));
    }

    private static byte[] Fixture() =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "Comprehension", "PowerPoint", FixtureName));

    private static string EntryText(BoundedZipPackage package, string entryName) =>
        Encoding.UTF8.GetString(package.ReadEntry(entryName, CancellationToken.None).Span);
}
