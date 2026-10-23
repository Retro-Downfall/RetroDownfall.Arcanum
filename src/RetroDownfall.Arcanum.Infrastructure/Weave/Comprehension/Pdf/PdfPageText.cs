using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Util;

namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension.Pdf;

/// <summary>
/// Joins the letters of one page into text, in the order the content stream draws them (#163 decides run order is content
/// order). It is PdfPig's content-order join with three differences, each for text a real producer writes and a reader
/// shows correctly:
/// <list type="bullet">
/// <item>
/// A letter that starts well left of where the previous one started is a new word. PdfPig only looks at gaps to the right,
/// so a float drawn before the text beside it, a right-to-left table row, a page number drawn before its title and a run
/// drawn over another were glued into one token. A small step back (kerning, an accent drawn over the letter that follows
/// it) stays inside the word.
/// </item>
/// <item>
/// The Latin ligature code points U+FB00 to U+FB06 are spelled out as letters.
/// </item>
/// <item>
/// Right-to-left text that the producer wrote in visual order (every glyph further right than the one before it, which is
/// how macOS and most other producers write Hebrew and Arabic) is put back in logical order, run by run.
/// </item>
/// </list>
/// The join is one pass over the letters followed by one pass over each line.
/// </summary>
internal static class PdfPageText
{
    public static string Build(IReadOnlyList<Letter> letters)
    {
        List<Cell> cells = new(letters.Count);

        Letter? previous = null;

        CellClass previousClass = CellClass.Neutral;

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

            CellClass letterClass = Classify(spelled);

            if (value == " " && !lastWasWhitespace)
            {
                if (previous is not null && IsNewline(previous, letter))
                {
                    continue;
                }

                cells.Add(new Cell(" ", CellClass.Space, letter.StartBaseLine.X));

                previous = letter;

                previousClass = CellClass.Space;

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
                        cells.RemoveAt(cells.Count - 1);
                    }

                    cells.Add(new Cell("\n", CellClass.Newline, 0));

                    lastWasWhitespace = true;
                }
                else if (previous.Value != " " && IsWordGap(previous, previousClass, letter, letterClass))
                {
                    cells.Add(new Cell(" ", CellClass.Space, letter.StartBaseLine.X));

                    lastWasWhitespace = true;
                }
            }

            cells.Add(new Cell(spelled, letterClass, letter.StartBaseLine.X));

            previous = letter;

            previousClass = letterClass;

            if (!string.IsNullOrWhiteSpace(value))
            {
                previousNonWhitespace = letter;
            }
        }

        Span<Cell> all = CollectionsMarshal.AsSpan(cells);

        RestoreLogicalOrder(all);

        StringBuilder text = new(all.Length);

        foreach (Cell cell in all)
        {
            text.Append(cell.Text);
        }

        return text.ToString();
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

    /// <summary>
    /// Whether a space belongs between two consecutive letters of a line. The gap to the right is PdfPig's word-space test.
    /// The step back is measured from where the previous letter started, not from where it ended: kerning and an accent
    /// drawn over the letter after it move the next letter back by less than the previous glyph's own width, while a run
    /// drawn to the left of the one before starts further back than any glyph is wide. Two right-to-left letters written
    /// one to the left of the other are in logical order, and the gap between them is measured between their facing edges.
    /// </summary>
    private static bool IsWordGap(Letter previous, CellClass previousClass, Letter letter, CellClass letterClass)
    {
        if (previousClass == CellClass.Rtl && letterClass == CellClass.Rtl && letter.StartBaseLine.X < previous.StartBaseLine.X)
        {
            return WhitespaceSizeStatistics.IsProbablyWhitespace(previous.StartBaseLine.X - letter.EndBaseLine.X, previous);
        }

        return WhitespaceSizeStatistics.IsProbablyWhitespace(letter.StartBaseLine.X - previous.EndBaseLine.X, previous)
            || WhitespaceSizeStatistics.IsProbablyWhitespace(previous.StartBaseLine.X - letter.StartBaseLine.X, previous);
    }

    /// <summary>
    /// Spells out the Latin ligature code points U+FB00 to U+FB06 (ff, fi, fl, ffi, ffl, long s t, st). macOS producers write
    /// Helvetica's fi and fl as ligature glyphs, which would reach the model and the index as "ofﬁce". This is the
    /// compatibility decomposition of those seven code points and nothing wider: the worker runs with invariant globalization,
    /// where general normalization is not available.
    /// </summary>
    private static string ExpandLigatures(string value)
    {
        if (value.AsSpan().IndexOfAnyInRange('ﬀ', 'ﬆ') < 0)
        {
            return value;
        }

        StringBuilder spelled = new(value.Length + 3);

        foreach (char character in value)
        {
            string? letters = character switch
            {
                'ﬀ' => "ff",
                'ﬁ' => "fi",
                'ﬂ' => "fl",
                'ﬃ' => "ffi",
                'ﬄ' => "ffl",
                'ﬅ' => "ſt",
                'ﬆ' => "st",
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

    /// <summary>
    /// PDF is a visual format: most producers write a right-to-left run with its glyphs advancing left to right across the
    /// page, so the text that comes out of the content stream is each run backwards. This puts the characters of those runs
    /// back in logical order. A run whose glyphs already step leftwards was written in logical order and is left alone.
    /// A line of right-to-left text only is one run, so its punctuation and the order of its words come back too; in a line
    /// that also has left-to-right letters each right-to-left run is reversed where it stands and the order of the runs is
    /// kept. Numbers and left-to-right runs inside a run keep their own order.
    /// </summary>
    private static void RestoreLogicalOrder(Span<Cell> cells)
    {
        int lineStart = 0;

        for (int index = 0; index <= cells.Length; index++)
        {
            if (index == cells.Length || cells[index].Class == CellClass.Newline)
            {
                RestoreLogicalOrderOfLine(cells[lineStart..index]);

                lineStart = index + 1;
            }
        }
    }

    private static void RestoreLogicalOrderOfLine(Span<Cell> line)
    {
        bool hasRightToLeft = false;

        bool hasLeftToRight = false;

        foreach (Cell cell in line)
        {
            hasRightToLeft |= cell.Class == CellClass.Rtl;

            hasLeftToRight |= cell.Class == CellClass.Ltr;
        }

        if (!hasRightToLeft)
        {
            return;
        }

        JoinNumbers(line);

        if (!hasLeftToRight)
        {
            int start = 0;

            while (start < line.Length && line[start].Class == CellClass.Space)
            {
                start++;
            }

            int end = line.Length;

            while (end > start && line[end - 1].Class == CellClass.Space)
            {
                end--;
            }

            RestoreLogicalOrderOfRun(line[start..end]);

            return;
        }

        int position = 0;

        while (position < line.Length)
        {
            if (line[position].Class != CellClass.Rtl)
            {
                position++;

                continue;
            }

            int last = position;

            int next = position + 1;

            while (next < line.Length && line[next].Class != CellClass.Ltr)
            {
                if (line[next].Class == CellClass.Rtl)
                {
                    last = next;
                }

                next++;
            }

            RestoreLogicalOrderOfRun(line[position..(last + 1)]);

            position = next;
        }
    }

    /// <summary>
    /// A separator between two digits (1,250.00, 10:30, 2026-10-01) and a currency or percent sign next to a digit belong to
    /// the number, so a number is one unit when a run is turned round.
    /// </summary>
    private static void JoinNumbers(Span<Cell> line)
    {
        for (int index = 0; index < line.Length; index++)
        {
            if (line[index].Class != CellClass.Neutral || line[index].Text.Length != 1)
            {
                continue;
            }

            char character = line[index].Text[0];

            bool afterDigit = index > 0 && line[index - 1].Class == CellClass.Number;

            bool beforeDigit = index + 1 < line.Length && line[index + 1].Class == CellClass.Number;

            bool separator = character is '.' or ',' or ':' or '/' or '-';

            bool sign = char.GetUnicodeCategory(character) == UnicodeCategory.CurrencySymbol || character is '%' or '‰';

            if ((separator && afterDigit && beforeDigit) || (sign && (afterDigit || beforeDigit)))
            {
                line[index].Class = CellClass.Number;
            }
        }
    }

    private static void RestoreLogicalOrderOfRun(Span<Cell> run)
    {
        int rising = 0;

        int falling = 0;

        double previousX = 0;

        bool hasPrevious = false;

        foreach (Cell cell in run)
        {
            if (cell.Class != CellClass.Rtl)
            {
                continue;
            }

            if (hasPrevious)
            {
                if (cell.X > previousX)
                {
                    rising++;
                }
                else if (cell.X < previousX)
                {
                    falling++;
                }
            }

            previousX = cell.X;

            hasPrevious = true;
        }

        if (rising <= falling)
        {
            return;
        }

        run.Reverse();

        int position = 0;

        while (position < run.Length)
        {
            if (run[position].Class != CellClass.Number)
            {
                position++;

                continue;
            }

            int end = position + 1;

            while (end < run.Length && run[end].Class == CellClass.Number)
            {
                end++;
            }

            run[position..end].Reverse();

            position = end;
        }
    }

    private static CellClass Classify(string text)
    {
        bool rightToLeft = false;

        bool leftToRight = false;

        bool digit = false;

        bool visible = false;

        foreach (Rune rune in text.EnumerateRunes())
        {
            UnicodeCategory category = Rune.GetUnicodeCategory(rune);

            visible |= !Rune.IsWhiteSpace(rune);

            if (category == UnicodeCategory.DecimalDigitNumber)
            {
                digit = true;
            }
            else if (IsRightToLeftBlock(rune.Value))
            {
                rightToLeft |= IsLetter(category) || IsMark(category);
            }
            else
            {
                leftToRight |= IsLetter(category);
            }
        }

        if (rightToLeft)
        {
            return CellClass.Rtl;
        }

        if (leftToRight)
        {
            return CellClass.Ltr;
        }

        if (digit)
        {
            return CellClass.Number;
        }

        return visible ? CellClass.Neutral : CellClass.Space;
    }

    /// <summary>Hebrew, Arabic, Syriac, Thaana, N'Ko, Samaritan and Mandaic, the Hebrew and Arabic presentation forms, and the right-to-left scripts of the supplementary planes.</summary>
    private static bool IsRightToLeftBlock(int codePoint) =>
        codePoint is >= 0x0590 and <= 0x08FF
            or >= 0xFB1D and <= 0xFDFF
            or >= 0xFE70 and <= 0xFEFE
            or >= 0x10800 and <= 0x10FFF
            or >= 0x1E800 and <= 0x1EFFF;

    private static bool IsLetter(UnicodeCategory category) =>
        category is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter;

    private static bool IsMark(UnicodeCategory category) =>
        category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

    private enum CellClass
    {
        /// <summary>Punctuation or a symbol.</summary>
        Neutral,

        /// <summary>A space the producer drew or a gap the join found.</summary>
        Space,

        /// <summary>A line break.</summary>
        Newline,

        /// <summary>A decimal digit, or a separator or sign that belongs to a number.</summary>
        Number,

        /// <summary>A letter of a script written left to right.</summary>
        Ltr,

        /// <summary>A letter or mark of a script written right to left.</summary>
        Rtl,
    }

    /// <summary>The text one letter (or one gap) contributes, with the position it was drawn at.</summary>
    private struct Cell(string text, CellClass kind, double x)
    {
        public string Text { get; } = text;

        public CellClass Class { get; set; } = kind;

        public double X { get; } = x;
    }
}
