namespace RetroDownfall.Arcanum.Cli.CommandCenter;

/// <summary>Pure overlay frame sizing for Command Center modals.</summary>
internal static class OverlayLayout
{
    public const int MinHeight = 5;

    public const int DefaultMaxWidth = 60;

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

    public static int MeasureWidth(int terminalCols, int maxWidth = DefaultMaxWidth) =>
        Math.Min(Math.Max(0, terminalCols - 4), maxWidth);
}
