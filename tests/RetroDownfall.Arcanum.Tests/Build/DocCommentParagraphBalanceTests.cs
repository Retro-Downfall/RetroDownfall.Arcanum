using System.Globalization;

using RetroDownfall.Arcanum.Tests.NativeSqlCipher;

namespace RetroDownfall.Arcanum.Tests.Build;

/// <summary>
/// Every doc comment in the tree closes every paragraph it opens.
/// </summary>
/// <remarks>
/// <para>No project enables <c>GenerateDocumentationFile</c>, so the compiler never reports a doc
/// comment's XML as malformed and a dropped <c>&lt;/para&gt;</c> builds with zero warnings. That is not
/// cosmetic where a remark's paragraphs exist to keep two results apart: the break the writer put
/// between them is gone, a reader is handed one paragraph saying both things, and that is precisely how
/// a remark stops being able to correct itself.</para>
///
/// <para>A text count rather than an XML parse, deliberately. The question is whether the two tags
/// balance, and counting them is the whole of it - parsing would need every other tag in the comment to
/// be well formed too, which is a different and much larger claim.</para>
///
/// <para>What the count cannot do, in both directions. The <c>///</c> filter below is a prefix test with
/// no lexical context, so a raw string literal whose lines begin with <c>///</c> is counted as though it
/// were a comment, and a file whose real doc comments balance can be reported anyway - this file's own
/// occurrences of the tags outside a doc comment escape the count only because their lines do not start
/// with <c>///</c>. And the counts accumulate per file rather than per comment, so a spare close in one
/// comment cancels a missing close in another comment of the same file. A red is a file to read, not a
/// located defect.</para>
///
/// <para><c>&lt;param&gt;</c> and <c>&lt;paramref&gt;</c> are not matched, because the comparison is
/// against the exact tags including their closing angle bracket.</para>
/// </remarks>
public sealed class DocCommentParagraphBalanceTests
{

    private const string Open = "<para>";

    private const string Close = "</para>";

    [Fact]
    public void Every_source_file_closes_every_doc_comment_paragraph_it_opens()
    {

        string root = NativeSqlCipherTestPaths.RepositoryRoot();

        List<string> offenders = [];

        int scanned = 0;

        foreach (string path in SourceFiles(root))
        {

            scanned++;

            int open = 0;

            int close = 0;

            foreach (string line in File.ReadLines(path))
            {

                if (!line.TrimStart().StartsWith("///", StringComparison.Ordinal))
                {

                    continue;

                }

                open += Occurrences(line, Open);

                close += Occurrences(line, Close);

            }

            if (open != close)
            {

                offenders.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{Path.GetRelativePath(root, path)}: {open} <para> against {close} </para>"));

            }

        }

        // Guards the guard: a path that stopped resolving would make the loop above pass by finding
        // nothing, which is the one way a check like this fails silently.
        Assert.True(scanned > 1000, $"Only {scanned} source files were scanned; the enumeration is wrong.");

        Assert.Empty(offenders);

    }

    private static IEnumerable<string> SourceFiles(string root) =>
        new[] { "src", "tests" }
            .SelectMany(area => Directory.EnumerateFiles(
                Path.Combine(root, area),
                "*.cs",
                SearchOption.AllDirectories))
            .Where(static path => !path.Contains(
                $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .Where(static path => !path.Contains(
                $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);

    private static int Occurrences(string line, string tag)
    {

        int count = 0;

        int index = line.IndexOf(tag, StringComparison.Ordinal);

        while (index >= 0)
        {

            count++;

            index = line.IndexOf(tag, index + tag.Length, StringComparison.Ordinal);

        }

        return count;

    }

}
