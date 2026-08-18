using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.UX;

/// <summary>
/// Writes a streaming diagnostic onto the terminal a raw answer stream is also writing to.
/// </summary>
/// <remarks>
/// Spectre lays every renderable out from column 0 at <c>Profile.Width</c>, and the answer stream
/// writes raw model tokens with no newline between them. A diagnostic emitted part-way through an
/// answer therefore paints from whatever column the last token left the caret on and wraps against a
/// margin that is not there. Most of these lines end in a newline of their own, which is why the
/// damage usually shows up one line later rather than immediately — but the misalignment is the same
/// defect the reasoning panel makes obvious, so both consult the same record of where the answer
/// left the caret and share its once-only accounting.
/// </remarks>
internal static class CliStreamDiagnostic
{

    /// <summary>
    /// Returns the answer stream to column 0 when it shares a terminal with the diagnostics and the
    /// answer left the caret part-way along a row. Returns whether a break was written.
    /// </summary>
    public static bool EnsureColumnZero(
        CliStreamContent content,
        TextWriter? answerStream = null,
        bool? answerSharesTerminal = null)
    {

        ArgumentNullException.ThrowIfNull(content);

        // The break belongs to the terminal only: writing it into a redirected stdout would put a
        // newline the model never produced into the payload.
        bool sharesTerminal = answerSharesTerminal
            ?? (!Console.IsOutputRedirected && !Console.IsErrorRedirected);

        if (!sharesTerminal || !content.AnswerEndsMidLine)
        {

            return false;

        }

        (answerStream ?? Console.Out).WriteLine();

        content.NoteAnswerLineBreak();

        return true;

    }

    /// <summary>
    /// Writes one themed diagnostic line, breaking the answer's line first when the two share a
    /// terminal.
    /// </summary>
    public static void WriteMarkupLine(
        IAnsiConsole console,
        CliStreamContent content,
        string markup,
        TextWriter? answerStream = null,
        bool? answerSharesTerminal = null)
    {

        ArgumentNullException.ThrowIfNull(console);

        _ = EnsureColumnZero(content, answerStream, answerSharesTerminal);

        console.MarkupLine(markup);

    }

}
