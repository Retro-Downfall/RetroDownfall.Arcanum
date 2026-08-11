using System.Collections;

using RetroDownfall.Arcanum.Cli.CommandCenter;

using Terminal.Gui.Drawing;

namespace RetroDownfall.Arcanum.Tests.Cli.CommandCenter;

/// <summary>
/// <c>ComposerHasText</c> is evaluated on every mapped key event and again per layout pass, so it
/// must answer from the live cells rather than concatenating the whole composer buffer. An operator
/// who pastes a large log then keeps typing pays that cost per keystroke otherwise.
/// </summary>
public sealed class ComposerContentScanTests
{

    /// <summary>An <see cref="IList"/> that records how many lines the scan actually looked at.</summary>
    private sealed class CountingLines(IList inner) : IList
    {

        public int Reads { get; private set; }

        public object? this[int index]
        {
            get
            {
                Reads++;

                return inner[index];
            }

            set => inner[index] = value;
        }

        public int Count => inner.Count;

        public bool IsFixedSize => inner.IsFixedSize;

        public bool IsReadOnly => inner.IsReadOnly;

        public bool IsSynchronized => inner.IsSynchronized;

        public object SyncRoot => inner.SyncRoot;

        public int Add(object? value) => inner.Add(value);

        public void Clear() => inner.Clear();

        public bool Contains(object? value) => inner.Contains(value);

        public void CopyTo(Array array, int index) => inner.CopyTo(array, index);

        public IEnumerator GetEnumerator()
        {

            for (int i = 0; i < Count; i++)
            {

                yield return this[i];

            }

        }

        public int IndexOf(object? value) => inner.IndexOf(value);

        public void Insert(int index, object? value) => inner.Insert(index, value);

        public void Remove(object? value) => inner.Remove(value);

        public void RemoveAt(int index) => inner.RemoveAt(index);

    }

    private static List<Cell> CellLine(string text)
    {

        List<Cell> cells = [];

        foreach (char c in text)
        {

            cells.Add(new Cell { Grapheme = c.ToString() });

        }

        return cells;

    }

    [Fact]
    public void A_leading_non_whitespace_cell_stops_the_scan()
    {

        ArrayList lines = [CellLine("hello")];

        for (int i = 0; i < 5_000; i++)
        {

            lines.Add(CellLine(new string('x', 200)));

        }

        CountingLines counting = new(lines);

        Assert.True(CommandCenterWindow.ComposerLinesHaveContent(counting));

        Assert.Equal(1, counting.Reads);

    }

    [Fact]
    public void An_all_whitespace_buffer_has_no_content()
    {

        ArrayList lines = [CellLine("   "), CellLine("\t"), CellLine(string.Empty)];

        Assert.False(CommandCenterWindow.ComposerLinesHaveContent(lines));

    }

    [Fact]
    public void Content_after_blank_lines_is_still_found()
    {

        ArrayList lines = [CellLine(string.Empty), CellLine("  "), CellLine(" ok ")];

        Assert.True(CommandCenterWindow.ComposerLinesHaveContent(lines));

    }

    [Fact]
    public void String_backed_lines_are_scanned_too()
    {

        ArrayList lines = ["   ", "  text  "];

        Assert.True(CommandCenterWindow.ComposerLinesHaveContent(lines));

        Assert.False(CommandCenterWindow.ComposerLinesHaveContent(new ArrayList { "  ", "\t" }));

    }

    [Fact]
    public void An_empty_buffer_has_no_content()
    {

        Assert.False(CommandCenterWindow.ComposerLinesHaveContent(new ArrayList()));

    }

}
