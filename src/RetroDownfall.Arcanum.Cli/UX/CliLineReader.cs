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

    /// <summary>The caller's token fired while the read was waiting for a keystroke.</summary>
    Cancelled,

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
/// The console a line read draws on and takes keystrokes from. Erasing needs the caret geometry —
/// a backspace cannot cross a wrap boundary — and the read loop needs to wait for a keystroke
/// without blocking past its cancellation, so both sit behind this seam and can be driven by a fake.
/// </summary>
internal interface ICliLineTerminal
{

    /// <summary>Width in columns, or 0 when the console cannot report one.</summary>
    int Width { get; }

    /// <summary>Column the caret occupies, or -1 when the console cannot report one.</summary>
    int CursorLeft { get; }

    /// <summary>Whether cursor-motion escape sequences reach the terminal.</summary>
    bool SupportsAnsi { get; }

    /// <summary>Whether a keystroke can be taken without blocking.</summary>
    bool KeyAvailable { get; }

    ConsoleKeyInfo ReadKey();

    void Write(string text);

    void WriteLine();

}

/// <summary>
/// The process console. Every geometry question a detached or redirected console cannot answer
/// degrades to the "unknown" value rather than throwing, because a line read that cannot measure the
/// terminal must still read the line.
/// </summary>
internal sealed class SystemCliLineTerminal : ICliLineTerminal
{

    public static readonly SystemCliLineTerminal Instance = new();

    private SystemCliLineTerminal()
    {
    }

    public int Width => TryRead(static () => Console.WindowWidth, 0);

    public int CursorLeft => TryRead(static () => Console.CursorLeft, -1);

    public bool SupportsAnsi => TryRead(static () => AnsiConsole.Profile.Capabilities.Ansi, false);

    /// <summary>
    /// A console that cannot answer is reported ready, so the wait degrades to the blocking
    /// <see cref="Console.ReadKey(bool)"/> it has always been instead of spinning on a poll that
    /// throws.
    /// </summary>
    public bool KeyAvailable => TryRead(static () => Console.KeyAvailable, true);

    public ConsoleKeyInfo ReadKey() => Console.ReadKey(intercept: true);

    public void Write(string text) => Console.Write(text);

    public void WriteLine() => Console.WriteLine();

    private static T TryRead<T>(Func<T> read, T fallback)
    {

        try
        {

            return read();

        }
        catch (Exception exception) when (
            exception is IOException or PlatformNotSupportedException or InvalidOperationException)
        {

            return fallback;

        }

    }

}

/// <summary>
/// Reads a single line. Ctrl+C is captured as a keystroke (not as SIGINT) for the duration of the
/// read, so the caller — not the process-termination handler — decides what an interrupt means.
/// </summary>
internal static class CliLineReader
{

    private const char Escape = '\u001b';

    /// <summary>
    /// How long the wait sleeps between polls when no keystroke is ready. Short enough that a
    /// dismissed prompt gives the console back well inside the caller's abandonment grace.
    /// </summary>
    private static readonly TimeSpan KeyPollInterval = TimeSpan.FromMilliseconds(25);

    /// <summary>
    /// Reads one submitted line. Every other way a read can end is raised rather than flattened to
    /// <c>null</c>: an interrupt and an end-of-input are operator decisions, and a caller that cannot
    /// tell them apart from an empty answer reports "no answer was provided" for a deliberate Ctrl+C.
    /// Callers that need the outcome without the exception should use <see cref="Read"/>.
    /// </summary>
    public static string? ReadLine(
        string promptMarkup,
        bool allowEmpty,
        CancellationToken cancellationToken = default)
    {

        CliLineReadResult result = Read(promptMarkup, allowEmpty, cancellationToken);

        return TranslateToLine(result, cancellationToken);

    }

    /// <summary>
    /// Applies <see cref="ReadLine"/>'s mapping from a read outcome onto the <c>string?</c> contract.
    /// End-of-input raises the same <see cref="InvalidOperationException"/> the redirected branch
    /// raises when its stream ends, and both cancellation shapes raise
    /// <see cref="OperationCanceledException"/>, so only a submitted line ever returns.
    /// </summary>
    internal static string? TranslateToLine(CliLineReadResult result, CancellationToken cancellationToken)
    {

        return result.Outcome switch
        {
            CliLineReadOutcome.Submitted => result.Line,
            CliLineReadOutcome.EndOfInput => throw new InvalidOperationException("Console input ended."),
            CliLineReadOutcome.Cancelled => throw new OperationCanceledException(cancellationToken),
            _ => throw new OperationCanceledException("The console read was interrupted by the operator."),
        };

    }

    public static CliLineReadResult Read(
        string promptMarkup,
        bool allowEmpty,
        CancellationToken cancellationToken = default)
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

        ICliLineTerminal terminal = SystemCliLineTerminal.Instance;

        // Taken after the prompt is painted: the composed line starts where the prompt left the
        // caret, and every erase is measured from that column.
        int originColumn = terminal.CursorLeft;

        bool controlCAsInputRestored = TryTreatControlCAsInput(true);

        try
        {
            return ReadInteractive(terminal, allowEmpty, originColumn, cancellationToken);
        }
        finally
        {
            if (controlCAsInputRestored)
            {
                _ = TryTreatControlCAsInput(false);
            }
        }

    }

    internal static CliLineReadResult ReadInteractive(
        ICliLineTerminal terminal,
        bool allowEmpty,
        int originColumn,
        CancellationToken cancellationToken)
    {

        StringBuilder sb = new();

        while (true)
        {

            if (!TryWaitForKey(terminal, cancellationToken))
            {

                terminal.WriteLine();

                return new CliLineReadResult(CliLineReadOutcome.Cancelled, null, sb.Length > 0);

            }

            ConsoleKeyInfo key = terminal.ReadKey();

            if (key.Key == ConsoleKey.C && (key.Modifiers & ConsoleModifiers.Control) != 0)
            {

                bool hadPendingText = sb.Length > 0;

                _ = ClearLine(sb, terminal, originColumn);

                terminal.WriteLine();

                return new CliLineReadResult(CliLineReadOutcome.Interrupted, null, hadPendingText);

            }

            if (key.Key == ConsoleKey.D && (key.Modifiers & ConsoleModifiers.Control) != 0)
            {

                if (sb.Length > 0)
                {

                    continue;

                }

                terminal.WriteLine();

                return new CliLineReadResult(CliLineReadOutcome.EndOfInput, null, false);

            }

            if (key.Key == ConsoleKey.Enter)
            {

                terminal.WriteLine();

                string line = sb.ToString();

                if (!allowEmpty && string.IsNullOrWhiteSpace(line))
                {

                    continue;

                }

                return new CliLineReadResult(CliLineReadOutcome.Submitted, line, false);

            }

            if (key.Key == ConsoleKey.Backspace)
            {

                _ = EraseLastCharacter(sb, terminal, originColumn);

                continue;

            }

            if (key.Key == ConsoleKey.U && (key.Modifiers & ConsoleModifiers.Control) != 0)
            {

                _ = ClearLine(sb, terminal, originColumn);

                continue;

            }

            if (key.Key == ConsoleKey.W && (key.Modifiers & ConsoleModifiers.Control) != 0)
            {

                _ = DeleteLastWord(sb, terminal, originColumn);

                continue;

            }

            if (char.IsControl(key.KeyChar))
            {

                continue;

            }

            _ = sb.Append(key.KeyChar);

            terminal.Write(key.KeyChar.ToString());

        }

    }

    /// <summary>
    /// Waits until a keystroke is ready, or until the caller's token fires. Returns false for the
    /// latter. <see cref="Console.ReadKey(bool)"/> has no cancellable overload, so a read that
    /// blocked on it outlived the prompt it belonged to: the operator had to type into a question
    /// that no longer meant anything before the command could exit.
    /// </summary>
    private static bool TryWaitForKey(ICliLineTerminal terminal, CancellationToken cancellationToken)
    {

        while (true)
        {

            if (cancellationToken.IsCancellationRequested)
            {

                return false;

            }

            if (terminal.KeyAvailable)
            {

                return true;

            }

            Thread.Sleep(KeyPollInterval);

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
    internal static int EraseLastCharacter(StringBuilder sb, ICliLineTerminal terminal, int originColumn)
    {

        if (sb.Length == 0)
        {

            return 0;

        }

        int peek = Math.Min(2, sb.Length);

        string tail = sb.ToString(sb.Length - peek, peek);

        int removed = RemoveLastCharacter(sb);

        int keptCells = TerminalCellMetrics.MeasureWidth(sb.ToString());

        return EraseCells(
            terminal,
            originColumn,
            keptCells,
            TerminalCellMetrics.MeasureWidth(tail[^removed..]));

    }

    /// <summary>Erases the whole composed line. Returns the number of terminal columns erased.</summary>
    internal static int ClearLine(StringBuilder sb, ICliLineTerminal terminal, int originColumn) =>
        EraseTrailing(sb, 0, terminal, originColumn);

    /// <summary>Erases the trailing word. Returns the number of terminal columns erased.</summary>
    internal static int DeleteLastWord(StringBuilder sb, ICliLineTerminal terminal, int originColumn)
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

        return EraseTrailing(sb, start + 1, terminal, originColumn);

    }

    /// <summary>
    /// Drops everything from <paramref name="start"/> onward and repaints the columns that text
    /// occupied. Returns the number of terminal columns erased.
    /// </summary>
    private static int EraseTrailing(
        StringBuilder sb,
        int start,
        ICliLineTerminal terminal,
        int originColumn)
    {

        int from = Math.Clamp(start, 0, sb.Length);

        if (from >= sb.Length)
        {

            return 0;

        }

        string removed = sb.ToString(from, sb.Length - from);

        int keptCells = TerminalCellMetrics.MeasureWidth(sb.ToString(0, from));

        sb.Length = from;

        return EraseCells(
            terminal,
            originColumn,
            keptCells,
            TerminalCellMetrics.MeasureWidth(removed));

    }

    /// <summary>
    /// Blanks the <paramref name="erasedCells"/> columns that follow <paramref name="keptCells"/>
    /// columns of surviving text and leaves the caret where the erased text began. The counts must be
    /// display columns rather than UTF-16 code units: an ideograph paints two columns from one code
    /// unit, an astral letter paints one column from two, and a combining mark paints none, so
    /// erasing by code unit either strands text on the line or eats the prompt.
    ///
    /// <para>Backspace clamps at column 0 on every common terminal, so it cannot walk the caret back
    /// onto the previous visual row. Once the composed line wraps, a bare-backspace erase therefore
    /// under-erases backward and over-paints downward: the first row keeps the text the buffer no
    /// longer holds, and the blanks spill onto a row below. When the caret geometry is known and the
    /// terminal understands cursor motion, the erase moves up and clears to the end of the display
    /// instead, which is exact at any width. Otherwise it falls back to backspaces, clamped to the
    /// caret's own row so the returned column count stays honest.</para>
    /// </summary>
    private static int EraseCells(
        ICliLineTerminal terminal,
        int originColumn,
        int keptCells,
        int erasedCells)
    {

        if (erasedCells <= 0)
        {

            return 0;

        }

        int width = terminal.Width;

        if (originColumn < 0 || width <= 0)
        {

            return EraseWithBackspaces(terminal, erasedCells);

        }

        int startCell = originColumn + keptCells;

        int endCell = startCell + erasedCells;

        int targetRow = startCell / width;

        // A terminal defers the wrap until the next glyph is written, so a caret that exactly filled
        // a row is still on that row rather than at column 0 of the next one.
        int caretRow = (endCell - 1) / width;

        bool deferredWrap = endCell % width == 0;

        if (caretRow == targetRow && !deferredWrap)
        {

            return EraseWithBackspaces(terminal, erasedCells);

        }

        if (!terminal.SupportsAnsi)
        {

            return EraseWithBackspaces(terminal, Math.Min(erasedCells, endCell - (caretRow * width)));

        }

        if (caretRow > targetRow)
        {

            terminal.Write($"{Escape}[{caretRow - targetRow}A");

        }

        terminal.Write($"{Escape}[{(startCell % width) + 1}G");

        terminal.Write($"{Escape}[0J");

        return erasedCells;

    }

    private static int EraseWithBackspaces(ICliLineTerminal terminal, int cells)
    {

        if (cells <= 0)
        {

            return 0;

        }

        terminal.Write(new string('\b', cells));

        terminal.Write(new string(' ', cells));

        terminal.Write(new string('\b', cells));

        return cells;

    }

}
