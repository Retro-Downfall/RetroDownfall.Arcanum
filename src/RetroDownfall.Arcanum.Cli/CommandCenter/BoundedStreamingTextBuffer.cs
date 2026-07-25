using System.Text;

namespace RetroDownfall.Arcanum.Cli.CommandCenter;

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
            _ = _text.Append(value.AsSpan(0, Math.Min(available, value.Length)));
        }

        _ = _text.Append(_truncationMarker);
        _truncated = true;
    }

    public string Snapshot() => _text.ToString();
}
