using System.Text.RegularExpressions;

using RetroDownfall.Arcanum.Tests.NativeSqlCipher;

namespace RetroDownfall.Arcanum.Tests.Build;

/// <summary>
/// Only <c>README.md</c> and <c>docs/Arcanum.OATH.md</c> may name a tracker issue. Every other
/// reference document has to describe the constraint itself, because a reader outside the tracker
/// cannot resolve <c>#55</c> into anything.
/// </summary>
/// <remarks>
/// <para>An inventory assertion rather than a behavior test: the failure it prevents is a new sentence,
/// written while the work item is still fresh in the author's head, that becomes unreadable the moment
/// the tracker is no longer at hand. It found exactly that — four descriptions of one clamp record that
/// explained themselves purely as "outside issue #55 scope", so the record said what work it was not
/// part of instead of what the value guarantees.</para>
/// <para>The document set mirrors <see cref="RetiredServerNamespaceTests"/>: documents whose vocabulary
/// is a contract, plus the two structured inventories that are read the same way. Dated review
/// snapshots and the plan and specification archive under <c>docs/superpowers</c> are historical
/// records of a single run and are deliberately left alone.</para>
/// <para>The pattern accepts the tracker spellings <c>issue #55</c>, <c>issue-55</c>, and
/// <c>issue 55</c>, plus a bare <c>#55</c> token when whitespace places it in prose. It deliberately
/// does not treat every hash and digit sequence as a tracker reference: Markdown URL fragments,
/// repository references such as <c>dotnet/runtime#119380</c>, and ordinary heading markers remain
/// valid document structure rather than prose dependencies.</para>
/// </remarks>
public sealed class DocumentationIssueReferenceTests
{

    /// <summary>Documents that must stand on their own, so neither exempt document appears here.</summary>
    private static readonly string[] GovernedDocuments =
    [
        "docs/Arcanum.DESIGN.md",

        "docs/Arcanum.API.md",

        "docs/Arcanum.Command.Reference.md",

        "docs/Arcanum.Design.Human.md",

        "docs/Arcanum.DEBUGGING.Human.md",

        "docs/Arcanum.CHAT-LOOP.md",

        "docs/ArcanumOATH.Human.md",

        "docs/Compendium.README.md",

        "docs/Arcanum.ConstraintInventory.json",

        "docs/Arcanum.CommandMap.json",
    ];

    private static readonly Regex TrackerIssueReference = new(
        @"(?:\bissues?(?:\s*#\s*|[- ])\d+|(?<=\s)#\d+\b)",
        RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(5));

    [Theory]
    [InlineData("The later work is tracked by #245.", true)]
    [InlineData("The later work is tracked by\t#250.", true)]
    [InlineData("This is issue #55 scope.", true)]
    [InlineData("See [runtime note](https://github.com/dotnet/runtime/issues/820#issuecomment-1).", false)]
    [InlineData("See [section](#820-details).", false)]
    [InlineData("The upstream fix is dotnet/runtime#119380.", false)]
    [InlineData("## 10.20 Covenant lifecycle", false)]
    [InlineData("# 244 Typed lifecycle", false)]
    public void Tracker_reference_predicate_rejects_prose_and_accepts_non_tracker_hashes(
        string line,
        bool expected)
    {

        Assert.Equal(expected, TrackerIssueReference.IsMatch(line));

    }

    [Fact]
    public void No_governed_document_explains_itself_by_naming_a_tracker_issue()
    {

        string root = NativeSqlCipherTestPaths.RepositoryRoot();

        List<string> offenders = [];

        foreach (string relative in GovernedDocuments)
        {

            string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

            Assert.True(File.Exists(path), $"{relative} is missing; the inventory names a document that does not exist.");

            string[] lines = File.ReadAllLines(path);

            for (int index = 0; index < lines.Length; index++)
            {

                Match match = TrackerIssueReference.Match(lines[index]);

                if (match.Success)
                {

                    offenders.Add($"{relative}:{index + 1} names {match.Value}");

                }

            }

        }

        Assert.Empty(offenders);

    }

}
