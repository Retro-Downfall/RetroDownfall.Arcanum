using RetroDownfall.TheForge.Ux.Services.Diff;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public class LineDiffTests
{

    [Fact]
    public void Compare_OversizeInput_DoesNotAllocateAQuadraticTable()
    {

        // BuildLcsTable allocates one contiguous int[left+1, right+1]; at 6,000 lines a side that is a
        // ~144 MB LOH block plus 36M inner-loop iterations, all on the Avalonia UI thread.
        string left = BuildLines("left", 6_000);

        string right = BuildLines("right", 6_000);

        long before = GC.GetAllocatedBytesForCurrentThread();

        IReadOnlyList<DiffLineItem> lines = LineDiff.Compare(left, right);

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(
            allocated < 32L * 1024 * 1024,
            $"Compare allocated {allocated:N0} bytes for a 6,000 x 6,000 line diff.");

        Assert.Equal(12_000, lines.Count);

    }

    [Fact]
    public void Compare_OversizeInput_FallsBackToWholeFileReplacement()
    {

        string left = BuildLines("left", 6_000);

        string right = BuildLines("right", 6_000);

        IReadOnlyList<DiffLineItem> lines = LineDiff.Compare(left, right);

        Assert.All(lines.Take(6_000), static line => Assert.Equal(LineDiffKind.Removed, line.Kind));

        Assert.All(lines.Skip(6_000), static line => Assert.Equal(LineDiffKind.Added, line.Kind));

        Assert.Equal("left 0", lines[0].Text);

        Assert.Equal(1, lines[0].LeftLineNumber);

        Assert.Null(lines[0].RightLineNumber);

        Assert.Equal("right 0", lines[6_000].Text);

        Assert.Null(lines[6_000].LeftLineNumber);

        Assert.Equal(1, lines[6_000].RightLineNumber);

    }

    private static string BuildLines(string prefix, int count) =>
        string.Join('\n', Enumerable.Range(0, count).Select(i => $"{prefix} {i}"));

    [Fact]
    public void Compare_Identical_AllUnchanged()
    {

        IReadOnlyList<DiffLineItem> lines = LineDiff.Compare("alpha\nbeta", "alpha\nbeta");

        Assert.Equal(2, lines.Count);

        Assert.All(lines, static line => Assert.Equal(LineDiffKind.Unchanged, line.Kind));

        Assert.Equal("alpha", lines[0].Text);

        Assert.Equal("beta", lines[1].Text);

    }

    [Fact]
    public void Compare_Add_MarksAddedLines()
    {

        IReadOnlyList<DiffLineItem> lines = LineDiff.Compare("alpha", "alpha\nbeta");

        Assert.Equal(
            [
                new DiffLineItem(LineDiffKind.Unchanged, "alpha", 1, 1),
                new DiffLineItem(LineDiffKind.Added, "beta", null, 2),
            ],
            lines);

    }

    [Fact]
    public void Compare_Remove_MarksRemovedLines()
    {

        IReadOnlyList<DiffLineItem> lines = LineDiff.Compare("alpha\nbeta", "alpha");

        Assert.Equal(
            [
                new DiffLineItem(LineDiffKind.Unchanged, "alpha", 1, 1),
                new DiffLineItem(LineDiffKind.Removed, "beta", 2, null),
            ],
            lines);

    }

    [Fact]
    public void Compare_Replace_MarksRemoveThenAdd()
    {

        IReadOnlyList<DiffLineItem> lines = LineDiff.Compare("alpha\nold", "alpha\nnew");

        Assert.Equal(
            [
                new DiffLineItem(LineDiffKind.Unchanged, "alpha", 1, 1),
                new DiffLineItem(LineDiffKind.Removed, "old", 2, null),
                new DiffLineItem(LineDiffKind.Added, "new", null, 2),
            ],
            lines);

    }

    [Fact]
    public void Compare_Empty_VsEmpty_IsEmpty()
    {

        Assert.Empty(LineDiff.Compare(null, null));

        Assert.Empty(LineDiff.Compare(string.Empty, string.Empty));

    }

    [Fact]
    public void Compare_Empty_VsContent_AllAdded()
    {

        IReadOnlyList<DiffLineItem> lines = LineDiff.Compare(string.Empty, "one\ntwo");

        Assert.Equal(
            [
                new DiffLineItem(LineDiffKind.Added, "one", null, 1),
                new DiffLineItem(LineDiffKind.Added, "two", null, 2),
            ],
            lines);

    }

    [Fact]
    public void BuildUnified_PrefixesLines()
    {

        IReadOnlyList<DiffLineItem> lines = LineDiff.Compare("a", "a\nb");

        IReadOnlyList<string> unified = LineDiff.BuildUnified(lines);

        Assert.Equal([" a", "+b"], unified);

    }

    [Fact]
    public void BuildSideBySide_AlignsRows()
    {

        IReadOnlyList<SideBySideDiffRow> rows = LineDiff.BuildSideBySide(
            LineDiff.Compare("alpha\nold", "alpha\nnew"));

        Assert.Equal(3, rows.Count);

        Assert.Equal("alpha", rows[0].LeftText);

        Assert.Equal("alpha", rows[0].RightText);

        Assert.Equal(LineDiffKind.Unchanged, rows[0].Kind);

        Assert.Equal("old", rows[1].LeftText);

        Assert.Null(rows[1].RightText);

        Assert.Equal(LineDiffKind.Removed, rows[1].Kind);

        Assert.Null(rows[2].LeftText);

        Assert.Equal("new", rows[2].RightText);

        Assert.Equal(LineDiffKind.Added, rows[2].Kind);

    }

}
