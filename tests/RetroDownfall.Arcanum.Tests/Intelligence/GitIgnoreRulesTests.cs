using System.Diagnostics;

using RetroDownfall.Arcanum.Api.Intelligence;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class GitIgnoreRulesTests
{
    [Theory]
    [InlineData("*.log", "a.log", false, true)]
    [InlineData("*.log", "src/deep/b.log", false, true)]
    [InlineData("*.log", "a.txt", false, null)]
    [InlineData("build/", "build", true, true)]
    [InlineData("build/", "build", false, null)]
    [InlineData("build/", "src/build", true, true)]
    [InlineData("/dist", "dist", true, true)]
    [InlineData("/dist", "src/dist", true, null)]
    [InlineData("docs/*.md", "docs/a.md", false, true)]
    [InlineData("docs/*.md", "docs/sub/a.md", false, null)]
    [InlineData("docs/*.md", "x/docs/a.md", false, null)]
    [InlineData("**/cache", "cache", true, true)]
    [InlineData("**/cache", "a/b/cache", true, true)]
    [InlineData("logs/**", "logs/a.txt", false, true)]
    [InlineData("logs/**", "logs/a/b.txt", false, true)]
    [InlineData("logs/**", "logs", true, null)]
    [InlineData("a/**/b", "a/b", false, true)]
    [InlineData("a/**/b", "a/x/b", false, true)]
    [InlineData("a/**/b", "a/x/y/b", false, true)]
    [InlineData("a/**/b", "b", false, null)]
    [InlineData("file?.txt", "file1.txt", false, true)]
    [InlineData("file?.txt", "file12.txt", false, null)]
    [InlineData("[ab].txt", "a.txt", false, true)]
    [InlineData("[ab].txt", "c.txt", false, null)]
    [InlineData("[!ab].txt", "c.txt", false, true)]
    [InlineData("[a-c].txt", "b.txt", false, true)]
    [InlineData("[a-c].txt", "d.txt", false, null)]
    [InlineData("a**b", "axxb", false, true)]
    [InlineData("\\#hash", "#hash", false, true)]
    [InlineData("# comment", "# comment", false, null)]
    [InlineData("\\!bang", "!bang", false, true)]
    [InlineData("trailing\\ ", "trailing ", false, true)]
    [InlineData("name   ", "name", false, true)]
    [InlineData("*", "anything", false, true)]
    [InlineData("*.log\r", "a.log", false, true)]
    public void A_single_rule_matches_the_way_git_matches_it(
        string rule,
        string relativePath,
        bool isDirectory,
        bool? expected)
    {
        GitIgnoreRules rules = GitIgnoreRules.Parse(rule);

        Assert.Equal(expected, rules.Evaluate(relativePath, isDirectory));
    }

    [Fact]
    public void A_later_rule_overrides_an_earlier_one_and_a_negation_re_includes()
    {
        GitIgnoreRules reincluded = GitIgnoreRules.Parse("*.log\n!keep.log\n");

        Assert.Equal(false, reincluded.Evaluate("keep.log", isDirectory: false));

        Assert.Equal(true, reincluded.Evaluate("other.log", isDirectory: false));

        GitIgnoreRules reexcluded = GitIgnoreRules.Parse("!keep.log\n*.log\n");

        Assert.Equal(true, reexcluded.Evaluate("keep.log", isDirectory: false));
    }

    [Fact]
    public void Blank_lines_and_comments_contribute_no_rules()
    {
        GitIgnoreRules rules = GitIgnoreRules.Parse("\n   \n# only a comment\n\r\n");

        Assert.True(rules.IsEmpty);

        Assert.Null(rules.Evaluate("anything", isDirectory: false));
    }

    [Fact]
    public void Rules_beyond_the_cap_are_dropped()
    {
        string text = string.Join(
            '\n',
            Enumerable.Range(0, GitIgnoreRules.MaxRules + 50).Select(static i => $"file-{i}.tmp"));

        GitIgnoreRules rules = GitIgnoreRules.Parse(text);

        Assert.Equal(true, rules.Evaluate("file-0.tmp", isDirectory: false));

        Assert.Equal(true, rules.Evaluate($"file-{GitIgnoreRules.MaxRules - 1}.tmp", isDirectory: false));

        Assert.Null(rules.Evaluate($"file-{GitIgnoreRules.MaxRules}.tmp", isDirectory: false));
    }

    [Fact]
    public void A_pattern_with_many_recursive_wildcards_is_not_searched_exponentially()
    {
        GitIgnoreRules rules = GitIgnoreRules.Parse("a/**/b/**/c/**/d/**/e/**/f/**/g\n");

        Assert.Equal(true, rules.Evaluate("a/x/b/x/c/x/d/x/e/x/f/x/g", isDirectory: false));

        // No segment named g closes the path, so every way of spreading the wildcards fails.
        string deep = "a/" + string.Join('/', Enumerable.Repeat("b/c/d/e/f", 40)) + "/h";

        Stopwatch clock = Stopwatch.StartNew();

        Assert.Null(rules.Evaluate(deep, isDirectory: false));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"Evaluation took {clock.Elapsed}.");
    }
}
