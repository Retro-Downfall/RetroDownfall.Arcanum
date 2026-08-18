using System.Text;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace RetroDownfall.Arcanum.Cli.UX;

internal sealed class CliStreamContent
{
    public const int DefaultMaxReasoningChars = 64 * 1024;

    public const string ReasoningTruncationMarker = "\n… [reasoning truncated]";

    private readonly StringBuilder _answer = new();
    private readonly StringBuilder _reasoning = new();
    private readonly int _maxReasoningChars;
    private bool _reasoningTruncated;
    private bool _answerLineBreakWritten;

    public CliStreamContent(int maxReasoningChars = DefaultMaxReasoningChars)
    {
        _maxReasoningChars = Math.Max(ReasoningTruncationMarker.Length, maxReasoningChars);
    }

    public string AnswerText => _answer.ToString();

    public int AnswerLength => _answer.Length;

    public string ReasoningText => _reasoning.ToString();

    /// <summary>
    /// Whether the answer written so far left the caret part-way along a row. The accumulated answer
    /// is byte-for-byte what the raw stream wrote to stdout, so it is also the record of where that
    /// stream left the cursor.
    /// </summary>
    public bool AnswerEndsMidLine =>
        !_answerLineBreakWritten && _answer.Length > 0 && _answer[^1] != '\n';

    /// <summary>Records a newline written to the answer stream on the answer's behalf.</summary>
    public void NoteAnswerLineBreak() => _answerLineBreakWritten = true;

    public void AppendAnswer(string? text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            _ = _answer.Append(text);
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

        AppendBoundedReasoning(reasoning.Text);
        return true;
    }

    public string DrainReasoning()
    {
        string text = _reasoning.ToString();
        _reasoning.Clear();
        _reasoningTruncated = false;
        return text;
    }

    private void AppendBoundedReasoning(string text)
    {
        if (_reasoningTruncated)
        {
            return;
        }

        if (_reasoning.Length + text.Length <= _maxReasoningChars)
        {
            _ = _reasoning.Append(text);
            return;
        }

        int contentLimit = _maxReasoningChars - ReasoningTruncationMarker.Length;
        if (_reasoning.Length > contentLimit)
        {
            _reasoning.Length = contentLimit;
        }

        int available = contentLimit - _reasoning.Length;
        if (available > 0)
        {
            _ = _reasoning.Append(text.AsSpan(0, Utf8Truncation.SafeCharSliceLength(text, available)));
        }

        // Both cuts above land on a raw UTF-16 code unit, which can fall between the halves of a
        // surrogate pair. Drop an orphaned high surrogate so the astral-plane glyph is dropped whole
        // rather than rendering as a replacement character before the marker (DESIGN §16.7).
        if (_reasoning.Length > 0 && char.IsHighSurrogate(_reasoning[^1]))
        {
            _reasoning.Length--;
        }

        _ = _reasoning.Append(ReasoningTruncationMarker);
        _reasoningTruncated = true;
    }
}

internal static class EphemeralReasoningRenderer
{
    private const string Header = "Reasoning (ephemeral)";

    public static IRenderable Build(string text, IThemePalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);

        return new Panel(new Markup(palette.MutedMarkup(Markup.Escape(text ?? string.Empty))))
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

        // The panel is a full-width box that Spectre lays out from column 0. The answer stream writes
        // raw tokens with no newline between them, so a flush that lands part-way through an answer
        // paints the box from the column the last token ended on and wraps its own border. The break
        // that fixes it belongs to the terminal only: writing it into a redirected stdout would put a
        // newline the model never produced into the payload.
        bool sharesTerminal = answerSharesTerminal
            ?? (!Console.IsOutputRedirected && !Console.IsErrorRedirected);

        if (sharesTerminal && content.AnswerEndsMidLine)
        {
            (answerStream ?? Console.Out).WriteLine();
            content.NoteAnswerLineBreak();
        }

        console.Write(Build(reasoning, palette));
        return true;
    }
}
