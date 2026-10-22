using System.Text;

namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

/// <summary>
/// Folds the characters that would break the projection's shape: it writes one line per row and one tab per boundary
/// between cells, so a cell's text (and a sheet's name) must not carry a line break, a tab or any control character. Each
/// such character becomes one space: the C0 controls and DEL, the whole C1 block (U+0080 to U+009F, which holds the next
/// line U+0085), and the line and paragraph separators U+2028 and U+2029. A carriage return directly followed by a line
/// feed is one break and becomes one space.
/// </summary>
internal static class SpreadsheetTextFolding
{
    /// <summary>
    /// Folds <paramref name="text"/>, the next piece of one text. <paramref name="afterCarriageReturn"/> carries a carriage
    /// return across pieces so a line feed that opens the next piece completes the pair; it is updated for the next piece.
    /// </summary>
    public static string Fold(string text, ref bool afterCarriageReturn)
    {
        int first = 0;

        while (first < text.Length && !NeedsFolding(text[first]))
        {
            first++;
        }

        if (first == text.Length)
        {
            afterCarriageReturn = false;

            return text;
        }

        StringBuilder folded = new(text.Length);

        folded.Append(text, 0, first);

        bool previousCarriageReturn = first == 0 && afterCarriageReturn;

        for (int index = first; index < text.Length; index++)
        {
            char current = text[index];

            if (!NeedsFolding(current))
            {
                folded.Append(current);

                previousCarriageReturn = false;

                continue;
            }

            if (current == '\n' && previousCarriageReturn)
            {
                previousCarriageReturn = false;

                continue;
            }

            folded.Append(' ');

            previousCarriageReturn = current == '\r';
        }

        afterCarriageReturn = previousCarriageReturn;

        return folded.ToString();
    }

    /// <summary>Folds a whole text, such as a sheet name.</summary>
    public static string Fold(string text)
    {
        bool afterCarriageReturn = false;

        return Fold(text, ref afterCarriageReturn);
    }

    private static bool NeedsFolding(char current) => char.IsControl(current) || current is '\u2028' or '\u2029';
}
