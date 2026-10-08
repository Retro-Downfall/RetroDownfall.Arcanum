using System.Text;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Cli.UX;

/// <summary>
/// Accumulates streamed text up to a fixed number of characters, then appends a marker and drops
/// everything after it. The cut is surrogate-safe, so a capped stream never ends in half a glyph. Shared
/// by the Command Center transcript and the CLI's reasoning panel, which previously each carried their
/// own copy of this logic.
/// </summary>
internal sealed class BoundedStreamingTextBuffer
{
    private readonly int _maxChars;

    private readonly string _truncationMarker;
    private readonly StringBuilder _text = new();
    private bool _truncated;

    public BoundedStreamingTextBuffer(int maxChars, string truncationMarker)
    {
        ArgumentException.ThrowIfNullOrEmpty(truncationMarker);
        _maxChars = Math.Max(truncationMarker.Length, maxChars);
        _truncationMarker = truncationMarker;
    }

    public void Append(string? value)
    {
        if (_truncated || string.IsNullOrEmpty(value))
        {
            return;
        }

        if (_text.Length + value.Length <= _maxChars)
        {
            _ = _text.Append(value);
            return;
        }

        int contentLimit = _maxChars - _truncationMarker.Length;
        if (_text.Length > contentLimit)
        {
            _text.Length = contentLimit;
        }

        int available = contentLimit - _text.Length;
        if (available > 0)
        {
            _ = _text.Append(value.AsSpan(0, Utf8Truncation.SafeCharSliceLength(value, available)));
        }

        // Both cuts above land on a raw UTF-16 code unit, which can fall between the halves of a
        // surrogate pair. Drop an orphaned high surrogate so the astral-plane glyph is dropped
        // whole rather than rendering as a replacement character before the marker (DESIGN §16.7).
        if (_text.Length > 0 && char.IsHighSurrogate(_text[_text.Length - 1]))
        {
            _text.Length--;
        }

        _ = _text.Append(_truncationMarker);
        _truncated = true;
    }

    /// <summary>Characters currently held, including the truncation marker once it has been appended.</summary>
    public int Length => _text.Length;

    public string Snapshot() => _text.ToString();

    /// <summary>Returns the text held so far and starts over, so the buffer can bound the next batch.</summary>
    public string Drain()
    {
        string text = _text.ToString();
        _ = _text.Clear();
        _truncated = false;
        return text;
    }
}
