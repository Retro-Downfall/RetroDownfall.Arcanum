using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.Commands.Tower;

/// <summary>
/// Shared rendering for spell/prompt `execute` commands: writes the assistant response text to
/// stdout, and (when present) a themed tool-call summary to stderr so piping stdout stays clean.
/// The response text on stdout is payload and stays byte-exact; the tool-call summary is a terminal
/// sink for model-authored names and arguments, so both are control-stripped before markup.
/// </summary>
internal static class ExecuteResultRendering
{
    public static async Task WriteExecuteResultAsync(PromptResponseDto response, IThemePalette themePalette)
    {
        await Console.Out.WriteLineAsync(response.Text).ConfigureAwait(false);

        if (response.ToolCalls is not { Count: > 0 } toolCalls)
        {
            return;
        }

        IAnsiConsole stderrConsole = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) });

        stderrConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape("Tool calls during execution:")));

        Table table = new();

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Tool")));

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Arguments")));

        foreach (PromptToolCall call in toolCalls)
        {
            const int maxArgsPreviewChars = 200;

            // Stripped before the cut, so the cut can neither leave the tail of a sequence behind as
            // text nor count characters the terminal never shows.
            string arguments = TerminalTextSanitizer.SanitizeLine(call.ArgumentsJson);

            string argsPreview = arguments.Length > maxArgsPreviewChars
                ? arguments[..Utf8Truncation.SafeCharSliceLength(arguments, maxArgsPreviewChars)] + "\u2026"
                : arguments;

            table.AddRow(
                new Markup(themePalette.TextMarkup(Markup.Escape(TerminalTextSanitizer.SanitizeLine(call.Name)))),
                new Markup(themePalette.MutedMarkup(Markup.Escape(argsPreview))));
        }

        stderrConsole.Write(table);
    }
}
