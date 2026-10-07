using System.Globalization;
using System.Text;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// Makes repository-authored text safe to show an operator who is deciding whether to trust it.
/// </summary>
/// <remarks>
/// <para>A workspace <c>mcp.json</c> is written by whoever controls the repository, and the operator reads
/// it to decide whether its commands may run. So the text has to show everything and hide nothing. Every
/// run of whitespace shows as one space, so padding cannot push the part of a command line that matters out
/// of view. A character that cannot be seen, or that moves the cursor, retitles the window, writes the
/// clipboard or reorders what follows it (a control character, a zero-width or direction-changing format
/// character, an unassigned or private-use code point, and the few spaces that are not white space such as
/// the Hangul filler) is never printed as itself and never dropped: it appears as <c>&lt;U+XXXX&gt;</c>, so
/// two names that differ only by an invisible character do not look the same. A field longer than
/// <see cref="MaxDisplayChars"/> is cut with a count of what was left out, and the result says so, because
/// a preview that leaves text out cannot be the basis for approving it.</para>
/// <para>An argument that is empty, or contains a space or a double quote, is shown in double quotes (with
/// the quote escaped), so <c>-c "a b"</c> and <c>-c a b</c> do not read alike. Whitespace at the end of an
/// argument is kept as one space inside those quotes, so <c>"node "</c> and <c>node</c> differ too.</para>
/// </remarks>
internal static class McpTrustPreviewText
{
    /// <summary>
    /// The most characters one field (a name, command, argument, URL, directory, variable name or path) shows,
    /// after whitespace is collapsed and unsafe characters are written as escapes. A real command line is far
    /// shorter; anything longer is refused rather than approved on partial text.
    /// </summary>
    internal const int MaxDisplayChars = 4096;

    /// <summary>
    /// Code points that are not white space and not format characters, yet render as nothing or as a blank
    /// cell: the combining grapheme joiner, the Hangul fillers, the Khmer inherent vowels, the Mongolian
    /// free variation selectors and the Braille blank.
    /// </summary>
    private static readonly HashSet<int> BlankLookalikes =
    [
        0x034F,
        0x115F,
        0x1160,
        0x17B4,
        0x17B5,
        0x180B,
        0x180C,
        0x180D,
        0x2800,
        0x3164,
        0xFFA0,
    ];

    /// <summary>
    /// The text safe to print, with whitespace collapsed and a bounded length, and whether any of it was left
    /// out.
    /// </summary>
    /// <param name="value">The authored text.</param>
    /// <param name="truncated">True when text past <see cref="MaxDisplayChars"/> was replaced by a count.</param>
    internal static string Display(
        string value,
        out bool truncated)
    {
        StringBuilder builder = new(Math.Min(value.Length, MaxDisplayChars));

        bool separatorPending = false;

        int shownUnits = 0;

        int hidden = 0;

        foreach (Rune rune in value.EnumerateRunes())
        {
            int units = rune.Utf16SequenceLength;

            if (Rune.IsWhiteSpace(rune))
            {
                separatorPending = true;

                shownUnits += units;

                continue;
            }

            string piece = IsSafeToPrint(rune)
                ? rune.ToString()
                : $"<U+{rune.Value:X4}>";

            if (builder.Length + piece.Length + (separatorPending ? 1 : 0) > MaxDisplayChars)
            {
                hidden = value.Length - shownUnits;

                break;
            }

            if (separatorPending)
            {
                builder.Append(' ');

                separatorPending = false;
            }

            builder.Append(piece);

            shownUnits += units;
        }

        truncated = hidden > 0;

        if (truncated)
        {
            builder.Append(CultureInfo.InvariantCulture, $" [{hidden} more characters not shown]");
        }

        return builder.ToString();
    }

    /// <summary>
    /// <paramref name="value"/> as <see cref="Display(string, out bool)"/> shows it, in double quotes when it
    /// is empty, blank, or holds a space or a double quote, so argument boundaries stay visible.
    /// </summary>
    /// <param name="value">One command-line argument as authored.</param>
    /// <param name="truncated">True when the argument was too long to show in full.</param>
    internal static string DisplayArgument(
        string value,
        out bool truncated)
    {
        string shown = Display(value, out truncated);

        if (value.Length == 0)
        {
            return "\"\"";
        }

        // Whitespace-only text collapses to nothing, which would read as no argument at all.
        if (shown.Length == 0)
        {
            return "\" \"";
        }

        // Display drops whitespace at the end of a field, which is harmless for a name but not for an
        // argument: "--config " and "--config" are different arguments, so the trailing run stays as one
        // space and the quoting below then shows it. A cut field already ends with its hidden count.
        if (!truncated && char.IsWhiteSpace(value[^1]))
        {
            shown += " ";
        }

        return shown.Contains(' ', StringComparison.Ordinal) || shown.Contains('"', StringComparison.Ordinal)
            ? $"\"{shown.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : shown;
    }

    private static bool IsSafeToPrint(Rune rune)
    {
        switch (Rune.GetUnicodeCategory(rune))
        {
            case UnicodeCategory.Control:
            case UnicodeCategory.Format:
            case UnicodeCategory.Surrogate:
            case UnicodeCategory.PrivateUse:
            case UnicodeCategory.OtherNotAssigned:
            case UnicodeCategory.LineSeparator:
            case UnicodeCategory.ParagraphSeparator:
                return false;

            default:
                return !BlankLookalikes.Contains(rune.Value);
        }
    }
}
