using System.Globalization;

namespace RetroDownfall.Arcanum.Cli.CommandCenter;

/// <summary>
/// Pure composer height measurement: cell-width soft-wrap, clamp 1..10, effective max from terminal rows.
/// Prefer Terminal.Gui TextView wrapped-line counts when available; this is the fallback / testable path.
/// </summary>
internal static class ComposerLayout
{
    public const int MinContentRows = 1;

    public const int MaxContentRows = 10;

    /// <summary>Top + bottom border chrome for the bordered composer pane.</summary>
    public const int BorderOverhead = 2;

    /// <summary>
    /// Minimum combined height for Transcript + Incantations bordered panes (3 + 3).
    /// </summary>
    public const int MinBodyHeight = 6;

    public const int MinTranscriptHeight = 3;

    public const int MinIncantationsHeight = 3;

    /// <summary>Reserved column when content exceeds the visible content rows (vertical scrollbar).</summary>
    public const int ScrollbarReservation = 1;

    public const int TabStop = TerminalCellMetrics.TabStop;

    /// <summary>
    /// Computes content rows and total pane height for the composer.
    /// </summary>
    /// <param name="text">Composer buffer (newlines preserved; CRLF normalized only for counting).</param>
    /// <param name="viewportWidth">TextView content viewport width after borders (before scrollbar reservation).</param>
    /// <param name="terminalRows">Full terminal row count.</param>
    /// <param name="headerHeight">Header pane height including borders.</param>
    /// <param name="footerHeight">Footer height.</param>
    /// <param name="wrappedRowCount">
    /// Optional precomputed wrapped row count (e.g. from TextView). When null, counts via cell-width wrap.
    /// </param>
    public static ComposerLayoutResult Measure(
        string? text,
        int viewportWidth,
        int terminalRows,
        int headerHeight,
        int footerHeight,
        int? wrappedRowCount = null)
    {
        int effectiveMax = EffectiveMaxContentRows(terminalRows, headerHeight, footerHeight);
        int widthForWrap = Math.Max(1, viewportWidth);
        int rawRows = wrappedRowCount ?? CountWrappedRows(text, widthForWrap);
        // If content will scroll, re-measure with scrollbar reservation so wrap matches the final viewport.
        bool needsScrollbar = rawRows > effectiveMax;
        if (needsScrollbar && viewportWidth > ScrollbarReservation)
        {
            widthForWrap = viewportWidth - ScrollbarReservation;
            rawRows = wrappedRowCount ?? CountWrappedRows(text, widthForWrap);
            needsScrollbar = rawRows > effectiveMax;
        }

        int contentRows = Math.Clamp(rawRows, MinContentRows, effectiveMax);
        int inputHeight = contentRows + BorderOverhead;
        return new ComposerLayoutResult(contentRows, inputHeight, effectiveMax, needsScrollbar, widthForWrap);
    }

    public static int EffectiveMaxContentRows(int terminalRows, int headerHeight, int footerHeight)
    {
        int rows = Math.Max(terminalRows, 1);
        int header = Math.Max(headerHeight, 0);
        int footer = Math.Max(footerHeight, 0);
        int availableForComposerContent = rows - header - footer - MinBodyHeight - BorderOverhead;
        return Math.Clamp(availableForComposerContent, MinContentRows, MaxContentRows);
    }

    /// <summary>
    /// Counts visual terminal rows for <paramref name="text"/> at <paramref name="viewportWidth"/> cell columns.
    /// Does not mutate stored text; CRLF is treated as a single hard break for counting only.
    /// </summary>
    public static int CountWrappedRows(string? text, int viewportWidth)
    {
        int width = Math.Max(1, viewportWidth);
        if (string.IsNullOrEmpty(text))
        {
            return 1;
        }

        // Trailing newline yields an extra blank visual row (empty last segment).
        int total = 0;
        int index = 0;
        while (index <= text.Length)
        {
            int lineEnd = index;
            while (lineEnd < text.Length)
            {
                char c = text[lineEnd];
                if (c == '\n')
                {
                    break;
                }

                if (c == '\r')
                {
                    break;
                }

                lineEnd++;
            }

            ReadOnlySpan<char> segment = text.AsSpan(index, lineEnd - index);
            total += CountSoftWrappedRows(segment, width);

            if (lineEnd >= text.Length)
            {
                break;
            }

            // Consume CR, LF, or CRLF as one hard break.
            if (text[lineEnd] == '\r')
            {
                lineEnd++;
                if (lineEnd < text.Length && text[lineEnd] == '\n')
                {
                    lineEnd++;
                }
            }
            else if (text[lineEnd] == '\n')
            {
                lineEnd++;
            }

            index = lineEnd;
            if (index == text.Length)
            {
                // Trailing hard break → blank row.
                total++;
                break;
            }
        }

        return Math.Max(1, total);
    }

    /// <summary>Total cell width of <paramref name="text"/> starting at column 0.</summary>
    public static int MeasureCellWidth(string? text) => TerminalCellMetrics.MeasureWidth(text);

    /// <summary>Cell width of one grapheme cluster at <paramref name="column"/>.</summary>
    public static int MeasureGraphemeCellWidth(string grapheme, int column) =>
        TerminalCellMetrics.MeasureGraphemeWidth(grapheme, column);

    private static int CountSoftWrappedRows(ReadOnlySpan<char> line, int width)
    {
        if (line.Length == 0)
        {
            return 1;
        }

        int rows = 1;
        int col = 0;
        var enumerator = StringInfo.GetTextElementEnumerator(line.ToString());
        while (enumerator.MoveNext())
        {
            string element = enumerator.GetTextElement();
            int w = TerminalCellMetrics.MeasureGraphemeWidth(element, col);
            if (w <= 0)
            {
                continue;
            }

            if (col > 0 && col + w > width)
            {
                rows++;
                col = 0;
                w = TerminalCellMetrics.MeasureGraphemeWidth(element, col);
            }

            while (w > width)
            {
                rows++;
                w -= width;
            }

            col += w;
            if (col == width)
            {
                rows++;
                col = 0;
            }
        }

        // Exact fill at end-of-line must not leave an empty trailing visual row.
        if (col == 0 && rows > 1)
        {
            rows--;
        }

        return Math.Max(1, rows);
    }

}

/// <summary>Result of <see cref="ComposerLayout.Measure"/>.</summary>
internal readonly record struct ComposerLayoutResult(
    int ContentRows,
    int InputHeight,
    int EffectiveMaxContentRows,
    bool ReserveScrollbar,
    int WrapWidth);
