using System.Globalization;
using System.Text;

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

    /// <summary>Minimum transcript/sessions body height (rows) that must remain visible.</summary>
    public const int MinBodyHeight = 3;

    /// <summary>Reserved column when content exceeds the visible content rows (vertical scrollbar).</summary>
    public const int ScrollbarReservation = 1;

    public const int TabStop = 8;

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
            int w = MeasureGraphemeWidth(element, col);
            if (w <= 0)
            {
                continue;
            }

            if (col > 0 && col + w > width)
            {
                rows++;
                col = 0;
                w = MeasureGraphemeWidth(element, col);
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

    private static int MeasureGraphemeWidth(string grapheme, int column)
    {
        if (string.IsNullOrEmpty(grapheme))
        {
            return 0;
        }

        if (grapheme == "\t")
        {
            int stop = TabStop - (column % TabStop);
            return stop <= 0 ? TabStop : stop;
        }

        int width = 0;
        foreach (Rune rune in grapheme.EnumerateRunes())
        {
            width += RuneCellWidth(rune);
        }

        // ZWJ / emoji sequences: if any wide rune present, treat the cluster as at least 2 when it looks emoji-like.
        if (width < 2 && LooksLikeEmojiCluster(grapheme))
        {
            return 2;
        }

        return Math.Max(0, width);
    }

    private static bool LooksLikeEmojiCluster(string grapheme)
    {
        foreach (Rune rune in grapheme.EnumerateRunes())
        {
            int v = rune.Value;
            if (v is >= 0x1F300 and <= 0x1FAFF)
            {
                return true;
            }

            if (v is 0x200D or >= 0xFE00 and <= 0xFE0F)
            {
                return true;
            }
        }

        return false;
    }

    private static int RuneCellWidth(Rune rune)
    {
        if (Rune.IsControl(rune) || rune.Value == 0)
        {
            return 0;
        }

        UnicodeCategory category = Rune.GetUnicodeCategory(rune);
        if (category is UnicodeCategory.NonSpacingMark
            or UnicodeCategory.EnclosingMark
            or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.Format)
        {
            return 0;
        }

        int v = rune.Value;
        if (IsWideCodePoint(v))
        {
            return 2;
        }

        return 1;
    }

    /// <summary>Best-effort East Asian Wide / Fullwidth / emoji ranges used by common terminals.</summary>
    private static bool IsWideCodePoint(int v) =>
        v is >= 0x1100 and <= 0x115F
        || v is 0x2329 or 0x232A
        || v is >= 0x2E80 and <= 0xA4CF
        || v is >= 0xAC00 and <= 0xD7A3
        || v is >= 0xF900 and <= 0xFAFF
        || v is >= 0xFE10 and <= 0xFE19
        || v is >= 0xFE30 and <= 0xFE6F
        || v is >= 0xFF00 and <= 0xFF60
        || v is >= 0xFFE0 and <= 0xFFE6
        || v is >= 0x1F300 and <= 0x1FAFF
        || v is >= 0x20000 and <= 0x3FFFD;
}

/// <summary>Result of <see cref="ComposerLayout.Measure"/>.</summary>
internal readonly record struct ComposerLayoutResult(
    int ContentRows,
    int InputHeight,
    int EffectiveMaxContentRows,
    bool ReserveScrollbar,
    int WrapWidth);
