using System.Text;
using RetroDownfall.Arcanum.Cli.CommandCenter;

namespace RetroDownfall.Arcanum.Cli.UX;

/// <summary>
/// Makes untrusted text safe to hand to a terminal. Model output, tool output and fetched content are
/// attacker-influenced, and a terminal acts on control characters instead of displaying them: an OSC 52
/// sequence writes the clipboard, an OSC 0 changes the window title, and cursor-motion sequences redraw
/// what is already on screen.
/// </summary>
/// <remarks>
/// Whole escape sequences are dropped when an ESC introduces them, so ANSI-coloured tool output reads as
/// plain text rather than leaving <c>[31m</c> debris behind. Text that merely looks like a sequence but
/// has no ESC in front of it is ordinary text and is never touched. Every remaining C0 control, DEL and
/// the 8-bit C1 range are dropped one at a time. Text that needs none of this keeps its string instance.
/// </remarks>
internal static class TerminalTextSanitizer
{
    private const char Escape = '\u001b';

    private const char Bell = '\u0007';

    /// <summary>
    /// A string sequence (OSC, DCS, SOS, PM, APC) that has not ended by this many characters is treated
    /// as a lone ESC, so a missing terminator cannot swallow an unbounded amount of legitimate text.
    /// </summary>
    private const int MaxStringSequenceChars = 4096;

    /// <summary>
    /// Sanitizes multi-line text for a transcript or panel: newlines are kept, tabs expand to the next
    /// tab stop, and every other control character (and every complete escape sequence) is dropped.
    /// </summary>
    public static string SanitizeBlock(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        if (!NeedsBlockSanitizing(text))
        {
            return text;
        }

        string withoutSequences = StripEscapeSequences(text);
        StringBuilder sanitized = new(withoutSequences.Length);

        // Running column rather than re-measuring the buffer at every tab, so a tab-indented payload
        // stays linear. A newline restarts the column.
        int column = 0;
        foreach (Rune rune in withoutSequences.EnumerateRunes())
        {
            int value = rune.Value;
            if (value == '\n')
            {
                _ = sanitized.Append('\n');
                column = 0;
                continue;
            }

            if (value == '\t')
            {
                int pad = TerminalCellMetrics.TabStop - (column % TerminalCellMetrics.TabStop);
                _ = sanitized.Append(' ', pad);
                column += pad;
                continue;
            }

            if (IsDroppedControl(value))
            {
                continue;
            }

            if (rune.IsAscii)
            {
                _ = sanitized.Append((char)value);
                column++;
                continue;
            }

            string glyph = rune.ToString();
            _ = sanitized.Append(glyph);
            column += TerminalCellMetrics.MeasureGraphemeWidth(glyph, column);
        }

        return sanitized.ToString();
    }

    /// <summary>
    /// Sanitizes text for a single-line sink (a list row, a header, a prompt): the block rules, with
    /// every line break flattened to a space because the sink cannot take one. Text that needs none of
    /// this keeps its string instance.
    /// </summary>
    public static string SanitizeLine(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (!ContainsDroppedControl(text))
        {
            return text;
        }

        string withoutSequences = StripEscapeSequences(text);
        StringBuilder sanitized = new(withoutSequences.Length);
        int column = 0;
        foreach (Rune rune in withoutSequences.EnumerateRunes())
        {
            int value = rune.Value;
            if (value == '\t')
            {
                int pad = TerminalCellMetrics.TabStop - (column % TerminalCellMetrics.TabStop);
                _ = sanitized.Append(' ', pad);
                column += pad;
                continue;
            }

            if (value is '\r' or '\n')
            {
                _ = sanitized.Append(' ');
                column++;
                continue;
            }

            if (IsDroppedControl(value))
            {
                continue;
            }

            if (rune.IsAscii)
            {
                _ = sanitized.Append((char)value);
                column++;
                continue;
            }

            string glyph = rune.ToString();
            _ = sanitized.Append(glyph);
            column += TerminalCellMetrics.MeasureGraphemeWidth(glyph, column);
        }

        return sanitized.ToString();
    }

    /// <summary>
    /// Removes every complete ESC-introduced sequence (CSI, OSC and the other string sequences, and
    /// two-character escapes) and leaves everything else, including other control characters, for the
    /// caller. Returns <paramref name="text"/> itself when it contains no ESC.
    /// </summary>
    public static string StripEscapeSequences(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        int first = text.IndexOf(Escape, StringComparison.Ordinal);
        if (first < 0)
        {
            return text;
        }

        StringBuilder kept = new(text.Length);
        _ = kept.Append(text, 0, first);

        int index = first;
        while (index < text.Length)
        {
            if (text[index] != Escape)
            {
                _ = kept.Append(text[index]);
                index++;
                continue;
            }

            index = SkipEscapeSequence(text, index);
        }

        return kept.ToString();
    }

    /// <summary>True for a character that is dropped outright: C0 controls, DEL and the 8-bit C1 range.</summary>
    public static bool IsDroppedControl(int value) =>
        value < 0x20 || value == 0x7F || value is >= 0x80 and <= 0x9F;

    private static bool ContainsDroppedControl(string text)
    {
        foreach (char c in text)
        {
            if (IsDroppedControl(c))
            {
                return true;
            }
        }

        return false;
    }

    private static bool NeedsBlockSanitizing(string text)
    {
        foreach (char c in text)
        {
            if (c == '\n')
            {
                continue;
            }

            if (IsDroppedControl(c))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns the index just past the escape sequence that starts at <paramref name="escapeIndex"/>.</summary>
    private static int SkipEscapeSequence(string text, int escapeIndex)
    {
        int next = escapeIndex + 1;
        if (next >= text.Length)
        {
            return text.Length;
        }

        char introducer = text[next];
        switch (introducer)
        {
            case '[':
                return SkipControlSequence(text, next + 1);

            case ']':
            case 'P':
            case 'X':
            case '^':
            case '_':
                return SkipStringSequence(text, escapeIndex, next + 1);

            default:
                // Two-character and charset-style escapes: ESC, any intermediates, one final byte.
                int index = next;
                while (index < text.Length && text[index] is >= ' ' and <= '/')
                {
                    index++;
                }

                return index < text.Length && text[index] is >= '0' and <= '~'
                    ? index + 1
                    : index;
        }
    }

    /// <summary>CSI: parameter bytes, intermediate bytes, then one final byte.</summary>
    private static int SkipControlSequence(string text, int index)
    {
        while (index < text.Length && text[index] is >= '0' and <= '?')
        {
            index++;
        }

        while (index < text.Length && text[index] is >= ' ' and <= '/')
        {
            index++;
        }

        return index < text.Length && text[index] is >= '@' and <= '~'
            ? index + 1
            : index;
    }

    /// <summary>
    /// OSC, DCS, SOS, PM and APC run to BEL or to ST (ESC followed by a backslash). A newline or the
    /// length cap ends the attempt without consuming anything beyond the introducer, so an unterminated
    /// sequence never swallows the text that follows it.
    /// </summary>
    private static int SkipStringSequence(string text, int escapeIndex, int payloadStart)
    {
        int limit = Math.Min(text.Length, payloadStart + MaxStringSequenceChars);
        for (int index = payloadStart; index < limit; index++)
        {
            char c = text[index];
            if (c == Bell)
            {
                return index + 1;
            }

            if (c == Escape)
            {
                return index + 1 < text.Length && text[index + 1] == '\\'
                    ? index + 2
                    : payloadStart;
            }

            if (c == '\n')
            {
                return payloadStart;
            }
        }

        return payloadStart;
    }
}
