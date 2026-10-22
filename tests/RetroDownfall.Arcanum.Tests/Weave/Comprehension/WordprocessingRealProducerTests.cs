using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension;

/// <summary>
/// The extractor against packages real producers wrote (macOS <c>textutil</c> and pandoc; see
/// <c>TestData/Comprehension/Word/README.md</c>), so it is proven on bytes it did not build itself.
/// </summary>
public sealed class WordprocessingRealProducerTests
{
    internal static byte[] Read(string fileName) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "Comprehension", "Word", fileName));

    [Fact]
    public void TextutilDocx_ProjectsParagraphsATabStyledRunsAndAccentedText()
    {
        // textutil writes no styles part, so this also covers a document with no styles at all.
        string text = WordprocessingFixtures.Extract(Read("textutil-multi.docx"));

        Assert.Equal(
            "First paragraph with bold and italic text.\n"
            + "Second paragraph with a tab:\tafter the tab.\n"
            + "Accented: café naïve résumé.\n"
            + "\n"
            + "Last paragraph.\n",
            text);
    }

    [Fact]
    public void TextutilDocx_HasNoStylesPart()
    {
        BoundedZipPackage package = BoundedZipPackage.Open(Read("textutil-multi.docx"), CancellationToken.None);

        Assert.False(package.Contains("word/styles.xml"));
    }

    [Fact]
    public void PandocDocx_ProjectsHeadingsRunsTablesListsAndTheFootnoteAfterTheBody()
    {
        // pandoc writes a real styles part, a footnotes part (separators plus one note), an empty comments part and an
        // external hyperlink relationship.
        string text = WordprocessingFixtures.Extract(Read("pandoc-styled.docx"));

        Assert.Equal(
            "Quarterly Report\n"
            + "The quick brown fox jumps over the lazy dog. A second sentence follows with a link.\n"
            + "Item\nCount\nApples\n3\nPears\n12\n"
            + "First point\nSecond point\n"
            + "Closing paragraph with an accented word: café.\n"
            + " The footnote explains the first sentence.\n",
            text);
    }

    [Fact]
    public void PandocDocx_HasTheStylesAndNotesPartsTheExtractorReads()
    {
        BoundedZipPackage package = BoundedZipPackage.Open(Read("pandoc-styled.docx"), CancellationToken.None);

        foreach (string part in new[] { "word/document.xml", "word/styles.xml", "word/footnotes.xml", "word/comments.xml" })
        {
            Assert.True(package.Contains(part), $"{part} should be present.");
        }
    }

    [Fact]
    public void PandocDocxWithRevisionsAndAComment_ProjectsInsertionsAndNeitherDeletionsNorTheComment()
    {
        string text = WordprocessingFixtures.Extract(Read("pandoc-revisions.docx"));

        Assert.Equal(
            "Plain start. inserted words and  end.\n"
            + "Commented text follows.\n",
            text);
    }

    [Fact]
    public void PandocDocxWithRevisions_RelatesACommentsPartWhoseTextNeverAppears()
    {
        BoundedZipPackage package = BoundedZipPackage.Open(Read("pandoc-revisions.docx"), CancellationToken.None);

        string comments = System.Text.Encoding.UTF8.GetString(package.ReadEntry("word/comments.xml", CancellationToken.None).Span);

        Assert.Contains("The comment body text.", comments);

        Assert.DoesNotContain("comment body", WordprocessingFixtures.Extract(Read("pandoc-revisions.docx")));

        Assert.DoesNotContain("deleted words", WordprocessingFixtures.Extract(Read("pandoc-revisions.docx")));
    }

    [Fact]
    public void TruncatedRealPackage_IsAlwaysRefusedAsANonPackage()
    {
        // A prefix of a real package has lost its end-of-central-directory record, and is refused with a closed rejection.
        byte[] whole = Read("pandoc-styled.docx");

        for (int length = 0; length < whole.Length; length += 97)
        {
            byte[] prefix = whole[..length];

            Assert.Throws<DocumentPackageRejectedException>(() => WordprocessingFixtures.Extract(prefix));
        }
    }
}
