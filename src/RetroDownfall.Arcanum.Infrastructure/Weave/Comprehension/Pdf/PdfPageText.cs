using System.Text;

using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Util;

namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension.Pdf;

/// <summary>
/// Joins the letters of one page into text, in the order the content stream draws them (#163 decides run order is content
/// order). This is PdfPig's content-order join with one difference: a letter that starts left of the end of the previous one
/// by more than a word space is a new word, where PdfPig only looks at gaps to the right. A float drawn before the text
/// beside it, a right-to-left table row and a run drawn over another all jump backwards on one baseline, and joined
/// without a separator two words (or a price and the quantity after it) become one token. The join is one pass over the
/// letters.
/// </summary>
internal static class PdfPageText
{
    public static string Build(IReadOnlyList<Letter> letters)
    {
        StringBuilder text = new();

        Letter? previous = null;

        Letter? previousNonWhitespace = null;

        bool lastWasWhitespace = false;

        foreach (Letter letter in letters)
        {
            string value = letter.Value;

            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            string spelled = ExpandLigatures(value);

            if (value == " " && !lastWasWhitespace)
            {
                if (previous is not null && IsNewline(previous, letter))
                {
                    continue;
                }

                text.Append(' ');

                previous = letter;

                lastWasWhitespace = true;

                continue;
            }

            lastWasWhitespace = false;

            if (previous is not null && value != " ")
            {
                if (IsNewline(previousNonWhitespace, letter))
                {
                    if (previous.Value == " ")
                    {
                        text.Length--;
                    }

                    text.Append('\n');

                    lastWasWhitespace = true;
                }
                else if (previous.Value != " " && WhitespaceSizeStatistics.IsProbablyWhitespace(Math.Abs(letter.StartBaseLine.X - previous.EndBaseLine.X), previous))
                {
                    text.Append(' ');

                    lastWasWhitespace = true;
                }
            }

            text.Append(spelled);

            previous = letter;

            if (!string.IsNullOrWhiteSpace(value))
            {
                previousNonWhitespace = letter;
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// Spells out the Latin ligature code points U+FB00 to U+FB06 (ff, fi, fl, ffi, ffl, long s t, st). macOS producers write
    /// Helvetica's fi and fl as ligature glyphs, which would reach the model and the index as "of\uFB01ce". This is the
    /// compatibility decomposition of those seven code points and nothing wider: the worker runs with invariant globalization,
    /// where general normalization is not available.
    /// </summary>
    private static string ExpandLigatures(string value)
    {
        if (value.AsSpan().IndexOfAnyInRange('\uFB00', '\uFB06') < 0)
        {
            return value;
        }

        StringBuilder spelled = new(value.Length + 3);

        foreach (char character in value)
        {
            string? letters = character switch
            {
                '\uFB00' => "ff",
                '\uFB01' => "fi",
                '\uFB02' => "fl",
                '\uFB03' => "ffi",
                '\uFB04' => "ffl",
                '\uFB05' => "\u017Ft",
                '\uFB06' => "st",
                _ => null,
            };

            if (letters is null)
            {
                spelled.Append(character);
            }
            else
            {
                spelled.Append(letters);
            }
        }

        return spelled.ToString();
    }

    /// <summary>The baselines of two letters differ by more than 0.9 of the smaller whole-number point size.</summary>
    private static bool IsNewline(Letter? previous, Letter letter)
    {
        if (previous is null)
        {
            return false;
        }

        int previousSize = (int)Math.Round(previous.PointSize);

        int letterSize = (int)Math.Round(letter.PointSize);

        int smaller = letterSize < previousSize ? letterSize : previousSize;

        return Math.Abs(previous.StartBaseLine.Y - letter.StartBaseLine.Y) > smaller * 0.9;
    }
}
