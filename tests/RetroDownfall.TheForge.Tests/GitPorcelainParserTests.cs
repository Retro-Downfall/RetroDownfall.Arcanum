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
    public void Parse_OctalEscapedPath_DecodesUtf8Bytes()
    {

        // core.quotePath defaults to true, so git C-quotes every non-ASCII path as three-digit octal
        // escapes, one per UTF-8 byte.
        IReadOnlyList<GitPorcelainEntry> entries = GitPorcelainParser.Parse("?? \"caf\\303\\251.txt\"\n");

        GitPorcelainEntry entry = Assert.Single(entries);

        Assert.Equal("café.txt", entry.Path);

    }

    [Fact]
    public void Parse_OctalEscapedPath_DecodesMultiByteRuns()
    {

        IReadOnlyList<GitPorcelainEntry> entries =
            GitPorcelainParser.Parse(" M \"\\346\\274\\242\\345\\255\\227/\\360\\237\\232\\200.md\"\n");

        GitPorcelainEntry entry = Assert.Single(entries);

        Assert.Equal("漢字/🚀.md", entry.Path);

    }

    [Fact]
    public void Parse_OctalEscapedRename_DecodesBothPaths()
    {

        IReadOnlyList<GitPorcelainEntry> entries =
            GitPorcelainParser.Parse("R  \"caf\\303\\251.txt\" -> \"th\\303\\251.txt\"\n");

        GitPorcelainEntry entry = Assert.Single(entries);

        Assert.Equal("thé.txt", entry.Path);

        Assert.Equal("café.txt", entry.OriginalPath);

    }

    [Fact]
    public void Parse_QuotedPath_UnescapesRemainingCEscapes()
    {

        IReadOnlyList<GitPorcelainEntry> entries =
            GitPorcelainParser.Parse("?? \"tab\\there\\\\back\\\"quote\\a.txt\"\n");

        GitPorcelainEntry entry = Assert.Single(entries);

        Assert.Equal("tab\there\\back\"quote\a.txt", entry.Path);

    }

    [Fact]
    public void Parse_UnquotedPathWithSurroundingSpaces_PreservesThem()
    {

        // git only C-quotes a path that needs escaping; a leading or trailing space does not, so
        // "notes .txt " arrives unquoted and trimming it produces a path that does not exist.
        IReadOnlyList<GitPorcelainEntry> entries = GitPorcelainParser.Parse(" M notes .txt \n");

        GitPorcelainEntry entry = Assert.Single(entries);

        Assert.Equal("notes .txt ", entry.Path);

    }

    [Fact]
    public void Parse_ModifiedPathContainingArrowText_IsNotReadAsARename()
    {

        // "a -> b.txt" is a legal filename and needs no quoting. Only an R/C status means rename.
        IReadOnlyList<GitPorcelainEntry> entries = GitPorcelainParser.Parse("M  a -> b.txt\n");

        GitPorcelainEntry entry = Assert.Single(entries);

        Assert.Equal("a -> b.txt", entry.Path);

        Assert.Null(entry.OriginalPath);

    }

    [Fact]
    public void Parse_RenameWithEscapedQuoteInTheOriginalPath_SplitsOnTheRealArrow()
    {

        // The \" inside the quoted original must not close the quote; if it does, the scanner treats
        // the arrow inside the filename as the separator and both paths come out wrong.
        IReadOnlyList<GitPorcelainEntry> entries =
            GitPorcelainParser.Parse("R  \"we\\\"ird -> name.txt\" -> \"new.txt\"\n");

        GitPorcelainEntry entry = Assert.Single(entries);

        Assert.Equal("new.txt", entry.Path);

        Assert.Equal("we\"ird -> name.txt", entry.OriginalPath);

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
