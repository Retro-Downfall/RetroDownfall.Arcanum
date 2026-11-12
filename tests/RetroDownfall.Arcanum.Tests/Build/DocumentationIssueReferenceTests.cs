using System.Text.RegularExpressions;

using RetroDownfall.Arcanum.Tests.NativeSqlCipher;

namespace RetroDownfall.Arcanum.Tests.Build;

/// <summary>
/// Maintained documentation describes product contracts and verification without depending on
/// issue identifiers, tracker links, or delivery bookkeeping.
/// </summary>
public sealed class DocumentationIssueReferenceTests
{
    private static IEnumerable<string> GovernedDocuments(string root)
    {
        string[] fixedDocuments =
        [
            "README.md",

            "AGENTS.md",

            "tests/RetroDownfall.Arcanum.Tests/TestData/Comprehension/Pdf/README.md",

            "tests/RetroDownfall.Arcanum.Tests/TestData/Contracts/Arcanum.ConstraintInventory.json",

            "tests/RetroDownfall.Arcanum.Tests/TestData/Contracts/Arcanum.CommandMap.json",
        ];

        return fixedDocuments.Concat(Directory.EnumerateFiles(Path.Combine(root, "docs"), "*.md")
            .Select(path => Path.GetRelativePath(root, path)));
    }

    private static readonly Regex TrackerIssueReference = new(
        @"(?:\bissues?(?:\s*#\s*|[- ])\d+|(?:^|(?<=[\s\[]|(?<!\])\())#\d+\b|\bgithub\.com/[^/\s)]+/[^/\s)]+/(?:issues|pulls?)(?=$|[/?#\s)])|\bhttps?://[^\s)]+/(?:issues|pull|merge_requests)/\d+\b|\bhttps?://[^\s)]+/browse/[a-z][a-z0-9]+-\d+\b|(?<![\w./])[\w.-]+/(?![\w.-]+\.md#)[\w.-]+#\d+\b)",
        RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(5));

    [Theory]
    [InlineData("#157 completed", true)]
    [InlineData("See [section](docs/Arcanum.API.md#821-the-proving-grounds).", false)]
    [InlineData("See [section](../README.md#100).", false)]
    [InlineData("The later work is tracked by #245.", true)]
    [InlineData("The later work is tracked by\t#250.", true)]
    [InlineData("This is issue #55 scope.", true)]
    [InlineData("The later work is tracked separately (#100).", true)]
    [InlineData("The later work is tracked separately [#100].", true)]
    [InlineData("See [runtime note](https://github.com/dotnet/runtime/issues/820#issuecomment-1).", true)]
    [InlineData("See [change](https://github.com/example/project/pull/42).", true)]
    [InlineData("See [section](#820-details).", false)]
    [InlineData("See [section](#100).", false)]
    [InlineData("The upstream fix is dotnet/runtime#119380.", true)]
    [InlineData("## 10.20 Covenant lifecycle", false)]
    [InlineData("# 244 Typed lifecycle", false)]
    public void Tracker_reference_predicate_rejects_prose_and_accepts_non_tracker_hashes(
        string line,
        bool expected)
    {
        Assert.Equal(expected, TrackerIssueReference.IsMatch(line));
    }

    [Theory]
    [InlineData("## Coming soon\n[The Guild](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/issues/143)", true)]
    [InlineData("## Coming soon\n[The Guild](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/issues/143) and #144", true)]
    [InlineData("## Coming soon\n[#143](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/issues/143)", true)]
    [InlineData("## Coming soon\n[143](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/issues/143)", true)]
    [InlineData("## Coming soon\n[**#143**](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/issues/143)", true)]
    [InlineData("## Coming soon\n[work](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/pull/143)", true)]
    [InlineData("## Coming soon\n[work](https://github.com/other/project/issues/143)", true)]
    [InlineData("## Coming soon\n[backlog](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/issues?q=is%3Aissue)", true)]
    [InlineData("## Coming soon\n[changes](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/pulls)", true)]
    [InlineData("## Coming soon\n[work](https://gitlab.com/example/project/-/issues/143)", true)]
    [InlineData("## Coming soon\n[work](https://tracker.example/browse/TEAM-143)", true)]
    [InlineData("## Coming soon\n[work](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/issues/143/extra)", true)]
    [InlineData("## Coming soon\n### Details\n[work](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/issues/143)", true)]
    [InlineData("## Coming soon\n## Install\n[work](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/issues/143)", true)]
    [InlineData("## Coming soon\n`[work](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/issues/143)`", true)]
    [InlineData("## Coming soon\n```\n[work](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/issues/143)\n```", true)]
    [InlineData("## Coming soon\n# Other\n[work](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/issues/143)", true)]
    [InlineData("## Other\n[work](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/issues/143)", true)]
    [InlineData("## Coming soon\n[work](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/issues/143)", true)]
    [InlineData("## Coming soon later\n[work](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/issues/143)", true)]
    public void Tracker_references_are_forbidden_in_all_document_sections(string document, bool expected)
    {
        Assert.Equal(expected, FindTrackerReferences(document.Split('\n')).Any());
    }

    private static IEnumerable<(int Line, string Reference)> FindTrackerReferences(string[] lines)
    {
        for (int index = 0; index < lines.Length; index++)
        {
            Match found = TrackerIssueReference.Match(lines[index]);

            if (found.Success) yield return (index + 1, found.Value);
        }
    }

    [Fact]
    public void No_governed_document_explains_itself_by_naming_a_tracker_issue()
    {
        string root = NativeSqlCipherTestPaths.RepositoryRoot();

        List<string> offenders = [];

        foreach (string relative in GovernedDocuments(root))
        {
            string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

            Assert.True(File.Exists(path), $"{relative} is missing; the inventory names a document that does not exist.");

            foreach (var (line, reference) in FindTrackerReferences(File.ReadAllLines(path)))
                offenders.Add($"{relative}:{line} names {reference}");
        }

        Assert.Empty(offenders);
    }
}
