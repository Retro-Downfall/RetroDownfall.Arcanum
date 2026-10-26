using System.Globalization;
using System.Text;
using RetroDownfall.Arcanum.Core.Primitives;
using Terminal.Gui.Text;

namespace RetroDownfall.Arcanum.Cli.CommandCenter;

/// <summary>
/// Terminal display-cell measurement shared by every Command Center pane.
/// A .NET string is UTF-16, so <see cref="string.Length"/> counts code units, not the columns a
/// terminal paints. Match the pinned Terminal.Gui renderer's rune widths and cap each grapheme at
/// two cells; combining marks occupy none, while tabs use explicit column-dependent stops.
/// Wrapping and truncation walk whole graphemes rather than splitting UTF-16 surrogate pairs.
/// This compatibility policy does not guarantee every terminal/font renders the same way.
/// </summary>
internal static class TerminalCellMetrics
{
    public const int TabStop = 8;

    /// <summary>
    /// True when every char is printable ASCII, so cell width equals <see cref="string.Length"/>.
    /// Lets the overwhelmingly common transcript line skip the grapheme walk entirely.
    /// </summary>
    public static bool IsSimpleNarrow(ReadOnlySpan<char> text)
    {
        foreach (char c in text)
        {
            if (c is < ' ' or > '~')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Total display cells occupied by <paramref name="text"/> starting at column 0.</summary>
    public static int MeasureWidth(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        if (IsSimpleNarrow(text))
        {
            return text.Length;
        }

        int column = 0;
        int index = 0;
        while (index < text.Length)
        {
            ReadOnlySpan<char> remaining = text.AsSpan(index);
            int clusterLength = Math.Max(1, StringInfo.GetNextTextElementLength(remaining));
            column += GraphemeWidth(remaining[..clusterLength], column);
            index += clusterLength;
        }

        return column;
    }

    /// <summary>Display cells of one grapheme cluster painted at <paramref name="column"/>.</summary>
    public static int MeasureGraphemeWidth(string? grapheme, int column) =>
        string.IsNullOrEmpty(grapheme) ? 0 : GraphemeWidth(grapheme.AsSpan(), column);

    /// <summary>Allocation-free display-cell measurement of one already-segmented grapheme.</summary>
    public static int MeasureGraphemeWidth(ReadOnlySpan<char> grapheme, int column) =>
        GraphemeWidth(grapheme, column);

    /// <summary>
    /// Largest grapheme-aligned slice of <paramref name="text"/> beginning at <paramref name="start"/>
    /// whose display width fits <paramref name="maxCells"/>. When <paramref name="forceProgress"/> is
    /// true a single grapheme wider than the whole budget is still consumed, so wrapping loops always
    /// advance instead of spinning forever on one wide glyph.
    /// </summary>
    public static CellSlice TakeCells(string text, int start, int maxCells, bool forceProgress = true)
    {
        ArgumentNullException.ThrowIfNull(text);
        int from = Math.Clamp(start, 0, text.Length);
        if (from >= text.Length)
        {
            return new CellSlice(text.Length, 0, ReachedEnd: true);
        }

        int budget = Math.Max(1, maxCells);
        int index = from;
        int cells = 0;
        while (index < text.Length)
        {
            ReadOnlySpan<char> remaining = text.AsSpan(index);
            int clusterLength = Math.Max(1, StringInfo.GetNextTextElementLength(remaining));
            int width = GraphemeWidth(remaining[..clusterLength], cells);
            if (cells + width > budget)
            {
                if (index == from && forceProgress)
                {
                    index += clusterLength;
                    cells += width;
                }

                break;
            }

            index += clusterLength;
            cells += width;
        }

        return new CellSlice(index, cells, index >= text.Length);
    }

    /// <summary>
    /// Truncates <paramref name="text"/> to at most <paramref name="maxCells"/> display cells on a
    /// grapheme boundary. Returns the input unchanged when it already fits.
    /// </summary>
    public static string TruncateToCells(string? text, int maxCells)
    {
        if (string.IsNullOrEmpty(text) || maxCells <= 0)
        {
            return string.Empty;
        }

        CellSlice slice = TakeCells(text, 0, maxCells, forceProgress: false);
        if (slice.ReachedEnd)
        {
            return text;
        }

        // The grapheme walk cannot land mid-pair; the shared guard keeps that invariant explicit.
        int end = Utf8Truncation.SafeCharSliceLength(text, slice.EndIndex);
        return end <= 0 ? string.Empty : text[..end];
    }

    private static int GraphemeWidth(ReadOnlySpan<char> grapheme, int column)
    {
        if (grapheme.Length == 0)
        {
            return 0;
        }

        if (grapheme.Length == 1 && grapheme[0] == '\t')
        {
            int stop = TabStop - (column % TabStop);
            return stop <= 0 ? TabStop : stop;
        }

        // Match the pinned renderer's GetColumns policy: sum nonnegative rune widths, capped per
        // complete grapheme. This is Terminal.Gui compatibility, not a universal terminal/font claim.
        int width = 0;
        foreach (Rune rune in grapheme.EnumerateRunes())
        {
            width += Math.Max(0, rune.GetColumns());

            if (width >= 2)
            {
                return 2;
            }
        }

        return width;
    }
}

/// <summary>Result of <see cref="TerminalCellMetrics.TakeCells"/>.</summary>
internal readonly record struct CellSlice(int EndIndex, int Cells, bool ReachedEnd);
