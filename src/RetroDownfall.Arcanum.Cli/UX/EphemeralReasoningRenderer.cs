using RetroDownfall.Arcanum.Core.Intelligence.Models;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace RetroDownfall.Arcanum.Cli.UX;

internal sealed class CliStreamContent
{
    public const int DefaultMaxReasoningChars = 64 * 1024;

    public const string ReasoningTruncationMarker = "\n… [reasoning truncated]";

    private readonly BoundedStreamingTextBuffer _reasoning;

    private long _answerLength;

    private char _lastAnswerChar;

    private bool _answerLineBreakWritten;

    public CliStreamContent(int maxReasoningChars = DefaultMaxReasoningChars)
    {
        _reasoning = new BoundedStreamingTextBuffer(maxReasoningChars, ReasoningTruncationMarker);
    }

    /// <summary>
    /// How many characters of answer have streamed so far. The text itself is not kept: it has already
    /// been written to stdout token by token, so holding a second copy for the whole turn would make a
    /// long answer cost its own length in memory for no reader.
    /// </summary>
    public long AnswerLength => _answerLength;

    public string ReasoningText => _reasoning.Snapshot();

    /// <summary>
    /// Whether the answer written so far left the caret part-way along a row. The last character the raw
    /// stream wrote to stdout is all that record needs.
    /// </summary>
    public bool AnswerEndsMidLine =>
        !_answerLineBreakWritten && _answerLength > 0 && _lastAnswerChar != '\n';

    /// <summary>Records a newline written to the answer stream on the answer's behalf.</summary>
    public void NoteAnswerLineBreak() => _answerLineBreakWritten = true;

    public void AppendAnswer(string? text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            _answerLength += text.Length;
            _lastAnswerChar = text[^1];
            _answerLineBreakWritten = false;
        }
    }

    public bool AppendReasoning(IntelligenceEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (evt.Type != IntelligenceEventType.Reasoning
            || evt.Reasoning is not { Text.Length: > 0 } reasoning)
        {
            return false;
        }

        _reasoning.Append(reasoning.Text);
        return true;
    }

    public string DrainReasoning() => _reasoning.Drain();
}

internal static class EphemeralReasoningRenderer
{
    private const string Header = "Reasoning (ephemeral)";

    public static IRenderable Build(string text, IThemePalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);

        // Reasoning is model output and the panel goes to a terminal, which acts on control characters
        // instead of showing them. The panel is presentation — it renders on stderr, and the raw answer
        // stream on stdout is never routed through here — so it is stripped wherever it is drawn.
        string safeText = TerminalTextSanitizer.SanitizeBlock(text);

        return new Panel(new Markup(palette.MutedMarkup(Markup.Escape(safeText))))
        {
            Header = new PanelHeader(palette.MutedMarkup(Markup.Escape(Header))),
            Border = BoxBorder.Rounded,
            BorderStyle = palette.MutedStyle(),
            Padding = new Padding(1, 0, 1, 0),
            Expand = true,
        };
    }

    public static bool Flush(
        IAnsiConsole console,
        CliStreamContent content,
        IThemePalette palette,
        TextWriter? answerStream = null,
        bool? answerSharesTerminal = null)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(content);

        string reasoning = content.DrainReasoning();
        if (string.IsNullOrEmpty(reasoning))
        {
            return false;
        }

        // The panel is a full-width box that Spectre lays out from column 0, so a flush that lands
        // part-way through an answer paints it from the column the last token ended on and wraps its
        // own border. Every other diagnostic the streaming loop writes shares that hazard, so both go
        // through the one guard and its once-only accounting.
        _ = CliStreamDiagnostic.EnsureColumnZero(content, answerStream, answerSharesTerminal);

        console.Write(Build(reasoning, palette));
        return true;
    }
}
