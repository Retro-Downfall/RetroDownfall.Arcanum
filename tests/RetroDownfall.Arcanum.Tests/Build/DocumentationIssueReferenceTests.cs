using System.Text.RegularExpressions;

using RetroDownfall.Arcanum.Tests.NativeSqlCipher;

namespace RetroDownfall.Arcanum.Tests.Build;

/// <summary>
/// Maintained documentation avoids tracker bookkeeping; the curated README roadmap may link to
/// named issues in this repository.
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
        @"(?:\bissues?(?:\s*#\s*|[- ])\d+|(?:^|(?<=[\s\[]|(?<!\])\())#\d+\b|\bgithub\.com/[^/\s)]+/[^/\s)]+/(?:issues|pull)/\d+\b|\bhttps?://[^\s)]+/(?:issues|pull|merge_requests)/\d+\b|\bhttps?://[^\s)]+/browse/[a-z][a-z0-9]+-\d+\b|(?<![\w./])[\w.-]+/(?![\w.-]+\.md#)[\w.-]+#\d+\b)",
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
    [InlineData("README.md", "## Coming soon\n[The Guild](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/issues/143)", false)]
    [InlineData("README.md", "## Coming soon\n[The Guild](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/issues/143) and #144", true)]
    [InlineData("README.md", "## Coming soon\n[#143](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/issues/143)", true)]
    [InlineData("README.md", "## Coming soon\n[143](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/issues/143)", true)]
    [InlineData("README.md", "## Coming soon\n[**#143**](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/issues/143)", true)]
    [InlineData("README.md", "## Coming soon\n[work](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/pull/143)", true)]
    [InlineData("README.md", "## Coming soon\n[work](https://github.com/other/project/issues/143)", true)]
    [InlineData("README.md", "## Coming soon\n[work](https://gitlab.com/example/project/-/issues/143)", true)]
    [InlineData("README.md", "## Coming soon\n[work](https://tracker.example/browse/TEAM-143)", true)]
    [InlineData("README.md", "## Coming soon\n[work](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/issues/143/extra)", true)]
    [InlineData("README.md", "## Coming soon\n### Details\n[work](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/issues/143)", false)]
    [InlineData("README.md", "## Coming soon\n## Install\n[work](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/issues/143)", true)]
    [InlineData("README.md", "## Coming soon\n`[work](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/issues/143)`", true)]
    [InlineData("README.md", "## Coming soon\n```\n[work](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/issues/143)\n```", true)]
    [InlineData("README.md", "## Coming soon\n# Other\n[work](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/issues/143)", true)]
    [InlineData("README.md", "## Other\n[work](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/issues/143)", true)]
    [InlineData("docs/Arcanum.DESIGN.md", "## Coming soon\n[work](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/issues/143)", true)]
    [InlineData("README.md", "## Coming soon later\n[work](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/issues/143)", true)]
    public void Roadmap_tracker_policy_is_scoped_to_named_links(string relative, string document, bool expected)
    {
        Assert.Equal(expected, FindTrackerReferences(relative, document.Split('\n')).Any());
    }

    private static readonly Regex RoadmapIssueLink = new(
        @"\[(?<label>[^\[\]\r\n]+)\]\(https://github\.com/Retro-Downfall/RetroDownfall\.Arcanum/issues/[1-9][0-9]*\)",
        RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(5));

    private static IEnumerable<(int Line, string Reference)> FindTrackerReferences(string relative, string[] lines)
    {
        bool roadmap = false;

        char fence = '\0';

        int fenceLength = 0;

        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];

            string trimmed = line.TrimStart();

            int run = 0;

            if (trimmed.Length > 0 && trimmed[0] is '`' or '~')
                while (run < trimmed.Length && trimmed[run] == trimmed[0]) run++;

            bool fenceLine = run >= 3 && (fence == '\0' || trimmed[0] == fence && run >= fenceLength);

            if (fenceLine)
            {
                if (fence == '\0') { fence = trimmed[0]; fenceLength = run; }
                else { fence = '\0'; fenceLength = 0; }
            }
            else if (fence == '\0')
            {
                if (line == "## Coming soon") roadmap = relative == "README.md";
                else if (line.StartsWith("## ", StringComparison.Ordinal) || line.StartsWith("# ", StringComparison.Ordinal)) roadmap = false;
            }

            string checkedLine = roadmap && fence == '\0' && !fenceLine
                ? RoadmapIssueLink.Replace(line, match =>
                {
                    string label = match.Groups["label"].Value.Trim();

                    string plainLabel = label.Trim('*', '_', '~', '`', ' ').TrimStart('#').Trim();

                    if (plainLabel.All(char.IsDigit) || IsWithinCodeSpan(line, match.Index) || IsEscaped(line, match.Index)) return match.Value;

                    return match.Groups["label"].Value;
                })
                : line;

            Match found = TrackerIssueReference.Match(checkedLine);

            if (found.Success) yield return (index + 1, found.Value);
        }
    }

    private static bool IsEscaped(string line, int index)
    {
        int slashes = 0;

        while (index > 0 && line[--index] == '\\') slashes++;

        return slashes % 2 != 0;
    }

    private static bool IsWithinCodeSpan(string line, int index)
    {
        int delimiter = 0;

        for (int position = 0; position < index; position++)
        {
            if (line[position] != '`' || IsEscaped(line, position)) continue;

            int run = 1;

            while (position + run < index && line[position + run] == '`') run++;

            if (delimiter == 0) delimiter = run;
            else if (delimiter == run) delimiter = 0;

            position += run - 1;
        }

        return delimiter != 0;
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

            foreach (var (line, reference) in FindTrackerReferences(relative, File.ReadAllLines(path)))
                offenders.Add($"{relative}:{line} names {reference}");
        }

        Assert.Empty(offenders);
    }
}
