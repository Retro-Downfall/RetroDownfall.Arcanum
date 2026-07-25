using System.Text;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
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

    public CliStreamContent(int maxReasoningChars = DefaultMaxReasoningChars)
    {
        _maxReasoningChars = Math.Max(ReasoningTruncationMarker.Length, maxReasoningChars);
    }

    public string AnswerText => _answer.ToString();

    public int AnswerLength => _answer.Length;

    public string ReasoningText => _reasoning.ToString();

    public void AppendAnswer(string? text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            _ = _answer.Append(text);
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
            _ = _reasoning.Append(text.AsSpan(0, Math.Min(available, text.Length)));
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
        IThemePalette palette)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(content);

        string reasoning = content.DrainReasoning();
        if (string.IsNullOrEmpty(reasoning))
        {
            return false;
        }

        console.Write(Build(reasoning, palette));
        return true;
    }
}
