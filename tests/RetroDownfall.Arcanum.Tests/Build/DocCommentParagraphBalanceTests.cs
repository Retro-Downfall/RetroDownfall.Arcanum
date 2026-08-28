using System.Globalization;

using RetroDownfall.Arcanum.Tests.NativeSqlCipher;

namespace RetroDownfall.Arcanum.Tests.Build;

/// <summary>
/// Every doc comment in the tree closes every paragraph it opens.
/// </summary>
/// <remarks>
/// <para>Nothing else in the build checks this. No project sets <c>GenerateDocumentationFile</c>, so
/// Roslyn never parses a doc comment's XML, and a dropped <c>&lt;/para&gt;</c> compiles clean with zero
/// warnings while every following paragraph silently nests inside the one before it. That is not
/// cosmetic where a remark's paragraphs exist to keep two results apart - a reader is handed one
/// paragraph saying both things, which is precisely how a remark stops being able to correct itself.</para>
///
/// <para>It exists because the failure recurred rather than because it was imagined: a remark rewritten
/// to separate a shipped-tree mutation result from a stripped-tree one lost its close in the rewrite,
/// and three review rounds passed over the file without anything reporting it.</para>
///
/// <para>A text count rather than an XML parse, deliberately. The question is whether the two tags
/// balance, and counting them is the whole of it - parsing would need every other tag in the comment to
/// be well formed too, which is a different and much larger claim. Only <c>///</c> lines are counted, so
/// a string literal containing the text can never make this fail; today no such literal exists, and
/// this keeps that from becoming load-bearing.</para>
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
