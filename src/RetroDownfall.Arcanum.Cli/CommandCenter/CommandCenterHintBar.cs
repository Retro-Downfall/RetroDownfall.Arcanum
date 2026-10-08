namespace RetroDownfall.Arcanum.Cli.CommandCenter;

/// <summary>
/// Fits a priority-ordered list of key hints into the footer's one row.
/// </summary>
/// <remarks>
/// Cutting the joined list off at the frame edge left half a hint on screen ("… Ctrl+C cancel · C…") and
/// dropped the F1 hint, the one key that lists every other key. The bar therefore drops whole hints from
/// the end of the list, measured in terminal cells, and ends with <see cref="HelpHint"/> whenever it left
/// any out.
/// </remarks>
internal static class CommandCenterHintBar
{
    public const string Separator = " · ";

    public const string HelpHint = "F1 help";

    /// <summary>
    /// <paramref name="hints"/> joined with <see cref="Separator"/> when the whole list fits
    /// <paramref name="width"/> cells; otherwise the longest prefix that still fits with
    /// <see cref="HelpHint"/> appended. A hint is never cut in half: only on a frame narrower than
    /// <see cref="HelpHint"/> itself is that hint truncated, so the bar never overflows.
    /// </summary>
    public static string Fit(IReadOnlyList<string> hints, int width)
    {
        ArgumentNullException.ThrowIfNull(hints);

        string full = string.Join(Separator, hints);

        if (TerminalCellMetrics.MeasureWidth(full) <= width)
        {
            return full;
        }

        // The help hint is appended, so a list that already ends with it does not offer it twice.
        int candidates = hints.Count > 0 && hints[^1] == HelpHint ? hints.Count - 1 : hints.Count;

        string fitted = HelpHint;

        for (int kept = 1; kept <= candidates; kept++)
        {
            string attempt = string.Join(Separator, hints.Take(kept)) + Separator + HelpHint;

            if (TerminalCellMetrics.MeasureWidth(attempt) > width)
            {
                break;
            }

            fitted = attempt;
        }

        return TerminalCellMetrics.MeasureWidth(fitted) <= width
            ? fitted
            : TerminalCellMetrics.TruncateToCells(HelpHint, width);
    }
}
