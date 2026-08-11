using System.Text;
using RetroDownfall.Arcanum.Cli.CommandCenter;
using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.UX;

/// <summary>
/// How an interactive line read ended.
/// </summary>
internal enum CliLineReadOutcome
{

    /// <summary>The operator submitted a line with Enter.</summary>
    Submitted,

    /// <summary>The operator pressed Ctrl+C.</summary>
    Interrupted,

    /// <summary>The operator pressed Ctrl+D on an empty line.</summary>
    EndOfInput,

}

/// <summary>
/// Result of a single interactive line read. <paramref name="HadPendingText"/> reports whether the
/// operator had composed any text when an interrupt arrived, which lets the caller distinguish
/// "clear the line" from "leave the REPL".
/// </summary>
internal readonly record struct CliLineReadResult(
    CliLineReadOutcome Outcome,
    string? Line,
    bool HadPendingText);

/// <summary>
/// Reads a single line. Ctrl+C is captured as a keystroke (not as SIGINT) for the duration of the
/// read, so the caller — not the process-termination handler — decides what an interrupt means.
/// </summary>
internal static class CliLineReader
{

    public static string? ReadLine(string promptMarkup, bool allowEmpty)
    {

        CliLineReadResult result = Read(promptMarkup, allowEmpty);

        return result.Outcome == CliLineReadOutcome.Submitted ? result.Line : null;

    }

    public static CliLineReadResult Read(string promptMarkup, bool allowEmpty)
    {

        AnsiConsole.Markup(promptMarkup);

        if (Console.IsInputRedirected)
        {
            while (true)
            {
                string? redirectedLine = Console.ReadLine();
                if (redirectedLine is null)
                {
                    throw new InvalidOperationException("Redirected console input ended.");
                }

                if (allowEmpty || !string.IsNullOrWhiteSpace(redirectedLine))
                {
                    return new CliLineReadResult(CliLineReadOutcome.Submitted, redirectedLine, false);
                }
            }
        }

        bool controlCAsInputRestored = TryTreatControlCAsInput(true);

        try
        {
            return ReadInteractive(allowEmpty);
        }
        finally
        {
            if (controlCAsInputRestored)
            {
                _ = TryTreatControlCAsInput(false);
            }
        }

    }

    private static CliLineReadResult ReadInteractive(bool allowEmpty)
    {

        StringBuilder sb = new();

        while (true)
        {

            ConsoleKeyInfo key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.C && (key.Modifiers & ConsoleModifiers.Control) != 0)
            {

                bool hadPendingText = sb.Length > 0;

                _ = ClearLine(sb);

                Console.WriteLine();

                return new CliLineReadResult(CliLineReadOutcome.Interrupted, null, hadPendingText);

            }

            if (key.Key == ConsoleKey.D && (key.Modifiers & ConsoleModifiers.Control) != 0)
            {

                if (sb.Length > 0)
                {

                    continue;

                }

                Console.WriteLine();

                return new CliLineReadResult(CliLineReadOutcome.EndOfInput, null, false);

            }

            if (key.Key == ConsoleKey.Enter)
            {

                Console.WriteLine();

                string line = sb.ToString();

                if (!allowEmpty && string.IsNullOrWhiteSpace(line))
                {

                    continue;

                }

                return new CliLineReadResult(CliLineReadOutcome.Submitted, line, false);

            }

            if (key.Key == ConsoleKey.Backspace)
            {

                _ = EraseLastCharacter(sb);

                continue;

            }

            if (key.Key == ConsoleKey.U && (key.Modifiers & ConsoleModifiers.Control) != 0)
            {

                _ = ClearLine(sb);

                continue;

            }

            if (key.Key == ConsoleKey.W && (key.Modifiers & ConsoleModifiers.Control) != 0)
            {

                _ = DeleteLastWord(sb);

                continue;

            }

            if (char.IsControl(key.KeyChar))
            {

                continue;

            }

            sb.Append(key.KeyChar);

            Console.Write(key.KeyChar);

        }

    }

    /// <summary>
    /// Removes the final character, treating a surrogate pair as the single character it encodes so
    /// that Backspace over a non-BMP glyph cannot strand a lone high surrogate in the buffer.
    /// Returns the number of UTF-16 code units removed.
    /// </summary>
    internal static int RemoveLastCharacter(StringBuilder sb)
    {

        if (sb.Length == 0)
        {

            return 0;

        }

        int removed = sb.Length >= 2
            && char.IsLowSurrogate(sb[^1])
            && char.IsHighSurrogate(sb[^2])
            ? 2
            : 1;

        sb.Length -= removed;

        return removed;

    }

    private static bool TryTreatControlCAsInput(bool value)
    {

        try
        {

            Console.TreatControlCAsInput = value;

            return true;

        }
        catch (Exception exception) when (
            exception is IOException or PlatformNotSupportedException or InvalidOperationException)
        {

            return false;

        }

    }

    /// <summary>
    /// Removes the final character and repaints the columns it occupied. Returns the number of
    /// terminal columns erased.
    /// </summary>
    internal static int EraseLastCharacter(StringBuilder sb)
    {

        if (sb.Length == 0)
        {

            return 0;

        }

        int peek = Math.Min(2, sb.Length);

        string tail = sb.ToString(sb.Length - peek, peek);

        int removed = RemoveLastCharacter(sb);

        return EraseCells(TerminalCellMetrics.MeasureWidth(tail[^removed..]));

    }

    /// <summary>Erases the whole composed line. Returns the number of terminal columns erased.</summary>
    internal static int ClearLine(StringBuilder sb) => EraseTrailing(sb, 0);

    /// <summary>Erases the trailing word. Returns the number of terminal columns erased.</summary>
    internal static int DeleteLastWord(StringBuilder sb)
    {

        int end = sb.Length - 1;

        while (end >= 0 && char.IsWhiteSpace(sb[end]))
        {

            end--;

        }

        int start = end;

        while (start >= 0 && !char.IsWhiteSpace(sb[start]))
        {

            start--;

        }

        return EraseTrailing(sb, start + 1);

    }

    /// <summary>
    /// Drops everything from <paramref name="start"/> onward and repaints the columns that text
    /// occupied. Returns the number of terminal columns erased.
    /// </summary>
    private static int EraseTrailing(StringBuilder sb, int start)
    {

        int from = Math.Clamp(start, 0, sb.Length);

        if (from >= sb.Length)
        {

            return 0;

        }

        string removed = sb.ToString(from, sb.Length - from);

        sb.Length = from;

        return EraseCells(TerminalCellMetrics.MeasureWidth(removed));

    }

    /// <summary>
    /// Walks the cursor back over <paramref name="cells"/> painted columns, blanks them, and returns.
    /// The count must be display columns rather than UTF-16 code units: an ideograph paints two
    /// columns from one code unit, an astral letter paints one column from two, and a combining mark
    /// paints none, so erasing by code unit either strands text on the line or eats the prompt.
    /// </summary>
    private static int EraseCells(int cells)
    {

        if (cells <= 0)
        {

            return 0;

        }

        Console.Write(new string('\b', cells));
        Console.Write(new string(' ', cells));
        Console.Write(new string('\b', cells));

        return cells;

    }

}
