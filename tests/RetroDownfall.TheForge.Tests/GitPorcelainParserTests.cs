using RetroDownfall.TheForge.Ux.Services.Git;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public class GitPorcelainParserTests
{

    [Fact]
    public void Parse_Empty_ReturnsEmpty()
    {

        Assert.Empty(GitPorcelainParser.Parse(null));

        Assert.Empty(GitPorcelainParser.Parse(string.Empty));

        Assert.Empty(GitPorcelainParser.Parse("   \n  "));

    }

    [Fact]
    public void Parse_StagedAndUnstaged_SplitsStatuses()
    {

        string porcelain =
            """
            M  staged.txt
             M unstaged.txt
            MM both.txt
            ?? untracked.txt
            A  added.txt
            """;

        IReadOnlyList<GitPorcelainEntry> entries = GitPorcelainParser.Parse(porcelain);

        Assert.Equal(5, entries.Count);

        GitPorcelainEntry staged = Assert.Single(entries, e => e.Path == "staged.txt");

        Assert.True(staged.HasStagedChange);

        Assert.False(staged.HasUnstagedChange);

        GitPorcelainEntry unstaged = Assert.Single(entries, e => e.Path == "unstaged.txt");

        Assert.False(unstaged.HasStagedChange);

        Assert.True(unstaged.HasUnstagedChange);

        GitPorcelainEntry both = Assert.Single(entries, e => e.Path == "both.txt");

        Assert.True(both.HasStagedChange);

        Assert.True(both.HasUnstagedChange);

        GitPorcelainEntry untracked = Assert.Single(entries, e => e.Path == "untracked.txt");

        Assert.True(untracked.IsUntracked);

        Assert.True(untracked.HasUnstagedChange);

        Assert.False(untracked.HasStagedChange);

        GitPorcelainEntry added = Assert.Single(entries, e => e.Path == "added.txt");

        Assert.Equal('A', added.IndexStatus);

        Assert.True(added.HasStagedChange);

    }

    [Fact]
    public void Parse_Rename_CapturesOriginalPath()
    {

        IReadOnlyList<GitPorcelainEntry> entries = GitPorcelainParser.Parse("R  old.txt -> new.txt\n");

        GitPorcelainEntry entry = Assert.Single(entries);

        Assert.Equal('R', entry.IndexStatus);

        Assert.Equal("new.txt", entry.Path);

        Assert.Equal("old.txt", entry.OriginalPath);

        Assert.Equal("old.txt → new.txt", entry.DisplayPath);

    }

    [Fact]
    public void Parse_QuotedPath_Unescapes()
    {

        IReadOnlyList<GitPorcelainEntry> entries = GitPorcelainParser.Parse("?? \"file with space.txt\"\n");

        GitPorcelainEntry entry = Assert.Single(entries);

        Assert.Equal("file with space.txt", entry.Path);

    }

    [Fact]
    public void Parse_QuotedRename_UnescapesBoth()
    {

        IReadOnlyList<GitPorcelainEntry> entries =
            GitPorcelainParser.Parse("R  \"old name.txt\" -> \"new name.txt\"\n");

        GitPorcelainEntry entry = Assert.Single(entries);

        Assert.Equal("new name.txt", entry.Path);

        Assert.Equal("old name.txt", entry.OriginalPath);

    }

}
