using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Util;

namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension.Pdf;

/// <summary>
/// Joins the letters of one page into text, in the order the content stream draws them (#163 decides run order is content
/// order). It is PdfPig's content-order join with three differences, each for text a real producer writes and a reader
/// shows correctly:
/// <list type="bullet">
/// <item>
/// A letter that starts well behind where the previous one started is a new word. PdfPig only looks at gaps ahead, so a
/// float drawn before the text beside it, a right-to-left table row, a page number drawn before its title and a run drawn
/// over another were glued into one token. A small step back (kerning, an accent drawn over the letter that follows it)
/// stays inside the word. Ahead and behind are measured along the direction each glyph advances in, so upside-down,
/// mirrored and otherwise turned text is judged as it reads.
/// </item>
/// <item>
/// The Latin ligature code points U+FB00 to U+FB06 are spelled out as letters.
/// </item>
/// <item>
/// Right-to-left text that the producer wrote in visual order (every glyph further along its own direction than the one
/// before it, which for upright text is further right, and is how macOS and most other producers write Hebrew and Arabic)
/// is put back in logical order, run by run.
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

        Direction direction = Direction.Rightward;

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

            Direction own = Direction.Of(letter, direction);

            double position = own.Position(letter.StartBaseLine);

            if (value == " " && !lastWasWhitespace)
            {
                if (previous is not null && IsNewline(previous, letter))
                {
                    continue;
                }

                cells.Add(new Cell(" ", CellClass.Space, position));

                previous = letter;

                direction = own;

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
                else if (previous.Value != " " && IsWordGap(previous, previousClass, letter, letterClass, direction))
                {
                    cells.Add(new Cell(" ", CellClass.Space, position));

                    lastWasWhitespace = true;
                }
            }

            cells.Add(new Cell(spelled, letterClass, position));

            previous = letter;

            direction = own;

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

        return Math.Abs(previous.StartBaseLine.Y - letter.StartBaseLine.Y) > LineTolerance(previous, letter);
    }

    /// <summary>0.9 of the smaller whole-number point size of two letters: how far apart two baselines may be on one line.</summary>
    private static double LineTolerance(Letter previous, Letter letter)
    {
        int previousSize = (int)Math.Round(previous.PointSize);

        int letterSize = (int)Math.Round(letter.PointSize);

        int smaller = letterSize < previousSize ? letterSize : previousSize;

        return smaller * 0.9;
    }

    /// <summary>
    /// Whether a space belongs between two consecutive letters of a line. Every distance is measured along the direction the
    /// previous glyph advances in (rightwards for ordinary text; leftwards for upside-down or mirrored text), so turned text
    /// is judged as it reads. The gap ahead is PdfPig's word-space test. The step back is measured from where the previous
    /// letter started, not from where it ended: kerning and an accent drawn over the letter after it move the next letter
    /// back by less than the previous glyph's own width, while a run drawn behind the one before starts further back than
    /// any glyph is wide. Two right-to-left letters written one behind the other are in logical order, and the gap between
    /// them is measured between their facing edges. A letter that starts off a visible previous letter's baseline by more
    /// than a line allows (which page height alone does not show for turned text) starts a new word; for upright text that
    /// is the line test, already passed.
    /// </summary>
    private static bool IsWordGap(Letter previous, CellClass previousClass, Letter letter, CellClass letterClass, Direction direction)
    {
        if (!string.IsNullOrWhiteSpace(previous.Value)
            && direction.Across(previous.StartBaseLine, letter.StartBaseLine) > LineTolerance(previous, letter))
        {
            return true;
        }

        double step = direction.Along(previous.StartBaseLine, letter.StartBaseLine);

        if (previousClass == CellClass.Rtl && letterClass == CellClass.Rtl && step < 0)
        {
            return WhitespaceSizeStatistics.IsProbablyWhitespace(direction.Along(letter.EndBaseLine, previous.StartBaseLine), previous);
        }

        return WhitespaceSizeStatistics.IsProbablyWhitespace(direction.Along(previous.EndBaseLine, letter.StartBaseLine), previous)
            || WhitespaceSizeStatistics.IsProbablyWhitespace(-step, previous);
    }

    /// <summary>
    /// Spells out the Latin ligature code points U+FB00 to U+FB06 (ff, fi, fl, ffi, ffl, long s t, st). macOS producers write
    /// Helvetica's fi and fl as ligature glyphs, which would reach the model and the index as "ofﬁce". Each becomes what NFKC
    /// gives for it (the long s t becomes "st": its decomposition is a long s and a t, and the long s decomposes again to s),
    /// and nothing wider is normalized: the worker runs with invariant globalization, where general normalization is not
    /// available.
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
                'ﬅ' => "st",
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
    /// back in logical order. A run whose glyphs already step back against their own direction (leftwards, for upright
    /// text) was written in logical order and is left alone.
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
    /// Marks the separators and signs a producer draws inside a number, in a run already put in logical order, so each number
    /// is one unit when its digits are turned back. The Unicode bidirectional algorithm decides what that is (UAX #9 W2, W4,
    /// W5), and it depends on the letter before the number: a separator between two digits (1,250.00, 10:30, 2026-10-01),
    /// the Arabic decimal and thousands separators U+066B and U+066C included, is always inside. After a Hebrew letter, or with
    /// no letter before, the digits are European numbers, and a sign next to them (a currency sign, %, °, #, ±) is inside
    /// too. After an Arabic letter the digits are Arabic numbers, which take no sign, so the sign stays a neutral and the
    /// run's reversal puts it back where it was typed. (The algorithm does not keep a hyphen inside an Arabic number either,
    /// but producers differ there, so a hyphen between digits always joins them.)
    /// </summary>
    private static void JoinNumbers(Span<Cell> run)
    {
        bool afterArabic = false;

        for (int index = 0; index < run.Length; index++)
        {
            if (run[index].Class == CellClass.Rtl)
            {
                afterArabic = IsArabicLetter(run[index].Text);

                continue;
            }

            if (run[index].Class != CellClass.Neutral || run[index].Text.Length != 1)
            {
                continue;
            }

            char character = run[index].Text[0];

            bool afterDigit = index > 0 && run[index - 1].Class == CellClass.Number;

            bool beforeDigit = index + 1 < run.Length && run[index + 1].Class == CellClass.Number;

            bool separator = character is '.' or ',' or ':' or '/' or '-' or '\u066B' or '\u066C';

            bool sign = !afterArabic && IsEuropeanNumberSign(character);

            if ((separator && afterDigit && beforeDigit) || (sign && (afterDigit || beforeDigit)))
            {
                run[index].Class = CellClass.Number;
            }
        }
    }

    /// <summary>A sign a European number takes as its own (bidirectional class ET): a currency sign, %, per mille and per ten thousand (Latin and Arabic), °, #, ± and the primes.</summary>
    private static bool IsEuropeanNumberSign(char character) =>
        char.GetUnicodeCategory(character) == UnicodeCategory.CurrencySymbol
            || character is '%' or '\u2030' or '\u2031' or '\u066A' or '\u0609' or '\u060A' or '\u00B0' or '#' or '\u00B1' or '\u2032' or '\u2033' or '\u2034';

    /// <summary>Whether a right-to-left letter is of a script the bidirectional algorithm calls Arabic (class AL): Arabic, Syriac and Thaana and their presentation forms.</summary>
    private static bool IsArabicLetter(string text)
    {
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (rune.Value is >= 0x0600 and <= 0x07BF or >= 0x0860 and <= 0x08FF or >= 0xFB50 and <= 0xFDFF or >= 0xFE70 and <= 0xFEFE
                or >= 0x10D00 and <= 0x10D3F or >= 0x10EC0 and <= 0x10EFF or >= 0x10F30 and <= 0x10F6F or >= 0x1EC70 and <= 0x1EEFF)
            {
                return true;
            }
        }

        return false;
    }

    private static void RestoreLogicalOrderOfRun(Span<Cell> run)
    {
        int rising = 0;

        int falling = 0;

        double previousPosition = 0;

        bool hasPrevious = false;

        foreach (Cell cell in run)
        {
            if (cell.Class != CellClass.Rtl)
            {
                continue;
            }

            if (hasPrevious)
            {
                if (cell.Position > previousPosition)
                {
                    rising++;
                }
                else if (cell.Position < previousPosition)
                {
                    falling++;
                }
            }

            previousPosition = cell.Position;

            hasPrevious = true;
        }

        if (rising <= falling)
        {
            return;
        }

        run.Reverse();

        JoinNumbers(run);

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

    /// <summary>
    /// The unit vector a glyph advances along: from where it starts on its baseline to where it ends, which is the direction
    /// of its text rendering matrix. A glyph too narrow to show a direction keeps the one before it.
    /// </summary>
    private readonly record struct Direction(double X, double Y)
    {
        /// <summary>A thousandth of a point: an advance shorter than this shows no direction.</summary>
        private const double ShortestAdvance = 0.001;

        public static Direction Rightward { get; } = new(1, 0);

        public static Direction Of(Letter letter, Direction fallback)
        {
            double x = letter.EndBaseLine.X - letter.StartBaseLine.X;

            double y = letter.EndBaseLine.Y - letter.StartBaseLine.Y;

            double length = Math.Sqrt((x * x) + (y * y));

            return length < ShortestAdvance ? fallback : new Direction(x / length, y / length);
        }

        /// <summary>Where a point lies along this direction, so that positions further ahead are larger.</summary>
        public double Position(PdfPoint point) => (point.X * X) + (point.Y * Y);

        /// <summary>How far <paramref name="to"/> lies ahead of <paramref name="from"/> in this direction (negative when behind).</summary>
        public double Along(PdfPoint from, PdfPoint to) => ((to.X - from.X) * X) + ((to.Y - from.Y) * Y);

        /// <summary>How far <paramref name="to"/> lies from the line through <paramref name="from"/> in this direction.</summary>
        public double Across(PdfPoint from, PdfPoint to) => Math.Abs(((to.X - from.X) * Y) - ((to.Y - from.Y) * X));
    }

    /// <summary>The text one letter (or one gap) contributes, with where it was drawn along its glyph's direction.</summary>
    private struct Cell(string text, CellClass kind, double position)
    {
        public string Text { get; } = text;

        public CellClass Class { get; set; } = kind;

        public double Position { get; } = position;
    }
}
