using System.Text.RegularExpressions;

using RetroDownfall.Arcanum.Tests.NativeSqlCipher;

namespace RetroDownfall.Arcanum.Tests.Build;

/// <summary>
/// Only <c>README.md</c>, <c>docs/Arcanum.Engineering.md</c>, and <c>docs/Arcanum.OATH.md</c> may
/// name a tracker issue -- the first two carry the running Covenant status, which is a record of
/// tracker work by construction. Every other
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
/// <para>The pattern is anchored on the word <c>issue</c> and accepts the tracker spellings
/// <c>issue #55</c>, <c>issue-55</c>, and <c>issue 55</c>. A bare <c>#</c> and digits is how every
/// Markdown section anchor in these documents is spelled, so matching that instead would flag the
/// cross-references the documents are supposed to carry.</para>
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
        @"\bissues?(?:\s*#\s*|[- ])\d+",
        RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(5));

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
