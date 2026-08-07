namespace RetroDownfall.Arcanum.Cli.CommandCenter;

/// <summary>Pure overlay frame sizing for Command Center modals.</summary>
internal static class OverlayLayout
{
    public const int MinHeight = 5;

    public const int DefaultMaxWidth = 60;

    /// <summary>Hard ceiling so a huge terminal does not spawn a wall-wide modal.</summary>
    public const int AbsoluteMaxWidth = 120;

    /// <summary>Max wrapped rows kept for a single long argument preview line.</summary>
    public const int MaxWrappedPreviewRows = 4;

    /// <summary>
    /// Content-fit height: border chrome + optional filter row + list lines, clamped to the body.
    /// Avoids half-screen confirm dialogs when only a handful of lines are shown.
    /// </summary>
    public static int MeasureHeight(
        int contentRows,
        bool showFilter,
        int bodyHeight,
        int terminalRows,
        int headerHeight)
    {
        int filterRows = showFilter ? 1 : 0;
        int needed = 2 + filterRows + Math.Max(1, contentRows);
        int maxByBody = Math.Max(MinHeight, bodyHeight);
        int maxByTerminal = Math.Max(MinHeight, terminalRows - headerHeight - 2);
        int maxHeight = Math.Min(maxByBody, maxByTerminal);
        return Math.Clamp(needed, MinHeight, maxHeight);
    }

    /// <summary>
    /// Width grows with content (up to <see cref="AbsoluteMaxWidth"/>) but never past the terminal.
    /// </summary>
    public static int MeasureWidth(int terminalCols, int contentColumns = 0, int maxWidth = AbsoluteMaxWidth)
    {
        int terminalBudget = Math.Max(0, terminalCols - 4);
        int wanted = Math.Max(DefaultMaxWidth, contentColumns + 2);
        int capped = Math.Min(wanted, maxWidth);
        return Math.Min(terminalBudget, capped);
    }

    /// <summary>
    /// Wraps long lines to <paramref name="innerWidth"/> display cells; truncates a single logical line
    /// to <see cref="MaxWrappedPreviewRows"/> wrapped rows with an ellipsis on the last row.
    /// Slices land on grapheme boundaries, so a wide glyph is never split and never overflows the frame.
    /// </summary>
    public static List<string> WrapLines(IReadOnlyList<string> lines, int innerWidth)
    {
        ArgumentNullException.ThrowIfNull(lines);
        int width = Math.Max(8, innerWidth);
        List<string> result = new(lines.Count);

        foreach (string line in lines)
        {
            if (string.IsNullOrEmpty(line))
            {
                result.Add(string.Empty);
                continue;
            }

            if (TerminalCellMetrics.MeasureWidth(line) <= width)
            {
                result.Add(line);
                continue;
            }

            int offset = 0;
            int rows = 0;
            while (offset < line.Length && rows < MaxWrappedPreviewRows)
            {
                CellSlice chunk = TerminalCellMetrics.TakeCells(line, offset, width);
                bool lastAllowed = rows == MaxWrappedPreviewRows - 1;
                if (lastAllowed && !chunk.ReachedEnd)
                {
                    CellSlice head = TerminalCellMetrics.TakeCells(line, offset, Math.Max(1, width - 1));
                    result.Add(line[offset..head.EndIndex] + "…");
                    break;
                }

                result.Add(line[offset..chunk.EndIndex]);
                offset = chunk.EndIndex;
                rows++;
            }
        }

        return result;
    }
}
