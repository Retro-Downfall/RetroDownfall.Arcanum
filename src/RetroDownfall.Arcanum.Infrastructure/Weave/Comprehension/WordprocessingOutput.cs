using System.Text;

using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

/// <summary>
/// Where the text a <see cref="WordprocessingPartWalker"/> projects goes: straight to the sink, or into a bounded hold
/// while the walk does not yet know whether it will be shown.
/// </summary>
/// <remarks>
/// <para>
/// Text is held in two cases, both nestable: a drawing (whether it is hidden can be said after its graphic, so its text is
/// kept until the drawing ends and then flushed or dropped) and a note that was met before its turn in reference order.
/// Everything held counts against <see cref="MaximumBufferedCharacters"/>, the size of the output ceiling; text that would
/// exceed it is never emitted, the sink is told it was truncated, and the walk stops.
/// </para>
/// <para>
/// This class also owns the line and cell rules, so they hold identically for held text. Outside a table cell a paragraph
/// ends with a newline, and a text box or a display equation is set apart from the text around it by a newline on each
/// side, asked for lazily so that a box that projects nothing adds no blank line. Inside a table cell there are no
/// newlines: paragraphs, text boxes, display equations and the cells and rows of a nested table are separated by a single
/// space (owed only between text), and tabs and breaks fold to a space so a row stays on one line. A drawing that turns
/// out hidden restores the state it started from, so nothing it did to the line or the cell survives.
/// </para>
/// </remarks>
internal sealed class WordprocessingOutput(ISessionAttachmentDocumentTextSink sink)
{
    /// <summary>The most text held at once, across every open hold. It equals the sink's default ceiling.</summary>
    internal const int MaximumBufferedCharacters = 262_144;

    private readonly List<Hold> _holds = [];

    private State _state = State.Initial;

    private int _cellDepth;

    private long _written;

    private int _buffered;

    /// <summary>True once the sink refused text or the hold overflowed; the walk must stop.</summary>
    public bool Stopped { get; private set; }

    /// <summary>Starts again as at the start of a part: the line is empty and nothing is owed.</summary>
    public void Reset() => _state = State.Initial;

    /// <summary>
    /// Appends projected text. Outside a cell it goes out as it is (after a newline if one is owed and the line is not
    /// empty); inside a cell it goes out after the space that is owed, with tabs and newlines folded to spaces.
    /// </summary>
    public void Append(ReadOnlySpan<char> text)
    {
        if (Stopped || text.IsEmpty)
        {
            return;
        }

        if (_cellDepth > 0)
        {
            if (_state.PendingSpace)
            {
                Write(" ");

                _state.PendingSpace = false;
            }

            _state.CellHasText = true;

            _state.LineEmpty = false;

            ReadOnlySpan<char> rest = text;

            while (!rest.IsEmpty && !Stopped)
            {
                int fold = rest.IndexOfAny('\n', '\r', '\t');

                if (fold < 0)
                {
                    Write(rest);

                    return;
                }

                Write(rest[..fold]);

                Write(" ");

                rest = rest[(fold + 1)..];
            }

            return;
        }

        if (_state.Separate)
        {
            _state.Separate = false;

            if (!_state.LineEmpty && text[0] != '\n')
            {
                Write("\n");
            }
        }

        Write(text);

        _state.LineEmpty = text[^1] == '\n';
    }

    /// <summary>Ends a paragraph: a newline outside a cell, and the space owed before the next text inside one.</summary>
    public void EndParagraph()
    {
        if (_cellDepth > 0)
        {
            _state.PendingSpace |= _state.CellHasText;

            return;
        }

        Append("\n");
    }

    /// <summary>
    /// Opens a block that stands apart from the text around it (a text box, a display equation). Pass the returned mark to
    /// <see cref="EndSeparatedBlock"/>; a block that projects nothing leaves no trace.
    /// </summary>
    public BlockMark BeginSeparatedBlock()
    {
        BlockMark mark = new(_written, _state.Separate, _state.PendingSpace);

        Separate();

        return mark;
    }

    /// <summary>Closes a separated block: the text after it starts on a new line, unless the block projected nothing.</summary>
    public void EndSeparatedBlock(BlockMark mark)
    {
        if (_written == mark.Written)
        {
            _state.Separate = mark.Separate;

            _state.PendingSpace = mark.PendingSpace;

            return;
        }

        Separate();
    }

    /// <summary>Enters a table cell. The outermost cell starts a fresh run of cell text; a nested one continues its outer cell.</summary>
    public void EnterCell()
    {
        if (_cellDepth == 0)
        {
            if (_state.Separate)
            {
                _state.Separate = false;

                if (!_state.LineEmpty)
                {
                    Write("\n");

                    _state.LineEmpty = true;
                }
            }

            _state.CellHasText = false;

            _state.PendingSpace = false;
        }

        _cellDepth++;
    }

    /// <summary>Leaves a table cell; a space owed at the end of the outermost cell is dropped.</summary>
    public void ExitCell()
    {
        _cellDepth--;

        if (_cellDepth == 0)
        {
            _state.PendingSpace = false;

            _state.CellHasText = false;
        }
    }

    /// <summary>Marks the boundary between two cells of a row: a tab, or a space inside a cell (a nested table).</summary>
    public void CellBoundary()
    {
        if (_cellDepth > 0)
        {
            _state.PendingSpace |= _state.CellHasText;

            return;
        }

        Append("\t");
    }

    /// <summary>Ends a row: a newline, or a space inside a cell (a nested table).</summary>
    public void EndRow()
    {
        if (_cellDepth > 0)
        {
            _state.PendingSpace |= _state.CellHasText;

            return;
        }

        Append("\n");
    }

    /// <summary>
    /// Starts holding text. Everything appended until the matching <see cref="EndHold"/> or <see cref="TakeHeld"/> is kept,
    /// and the state to return to if it is dropped is remembered.
    /// </summary>
    public void BeginHold() => _holds.Add(new Hold(new StringBuilder(), _state));

    /// <summary>Ends the innermost hold: its text is passed on (to the enclosing hold, or the sink), or dropped with the state it changed.</summary>
    public void EndHold(bool keep)
    {
        Hold hold = _holds[^1];

        _holds.RemoveAt(_holds.Count - 1);

        int length = hold.Text.Length;

        if (!keep)
        {
            _buffered -= length;

            _written -= length;

            _state = hold.Saved;

            return;
        }

        if (Stopped)
        {
            _buffered -= length;

            return;
        }

        if (_holds.Count > 0)
        {
            _holds[^1].Text.Append(hold.Text);

            return;
        }

        _buffered -= length;

        foreach (ReadOnlyMemory<char> chunk in hold.Text.GetChunks())
        {
            if (!sink.TryAppend(chunk.Span))
            {
                Stopped = true;

                return;
            }
        }
    }

    /// <summary>
    /// Ends the innermost hold and returns its text, which stays charged against the bound until <see cref="EmitHeld"/>
    /// releases it.
    /// </summary>
    public string TakeHeld()
    {
        Hold hold = _holds[^1];

        _holds.RemoveAt(_holds.Count - 1);

        if (Stopped)
        {
            _buffered -= hold.Text.Length;

            return string.Empty;
        }

        return hold.Text.ToString();
    }

    /// <summary>Writes text taken with <see cref="TakeHeld"/> to the sink (or the enclosing hold) and releases it from the bound.</summary>
    public void EmitHeld(string text)
    {
        _buffered -= text.Length;

        Write(text);
    }

    /// <summary>Withholds text that cannot be held: the sink is told, and the walk stops.</summary>
    public void Truncate()
    {
        if (Stopped)
        {
            return;
        }

        sink.MarkTruncated();

        Stopped = true;
    }

    /// <summary>Asks for the next text to start on its own line (outside a cell) or after a space (inside one).</summary>
    private void Separate()
    {
        if (_cellDepth > 0)
        {
            _state.PendingSpace |= _state.CellHasText;
        }
        else
        {
            _state.Separate = true;
        }
    }

    private void Write(ReadOnlySpan<char> text)
    {
        if (Stopped || text.IsEmpty)
        {
            return;
        }

        _written += text.Length;

        if (_holds.Count == 0)
        {
            if (!sink.TryAppend(text))
            {
                Stopped = true;
            }

            return;
        }

        if (_buffered + text.Length > MaximumBufferedCharacters)
        {
            Truncate();

            return;
        }

        _holds[^1].Text.Append(text);

        _buffered += text.Length;
    }

    /// <summary>What <see cref="BeginSeparatedBlock"/> remembers so that a block that projects nothing can be undone.</summary>
    internal readonly record struct BlockMark(long Written, bool Separate, bool PendingSpace);

    private struct State
    {
        /// <summary>True when the last text written ended a line (or nothing has been written).</summary>
        public bool LineEmpty;

        /// <summary>True when the next text outside a cell starts on a new line.</summary>
        public bool Separate;

        /// <summary>True once the outermost cell being walked has produced text.</summary>
        public bool CellHasText;

        /// <summary>True when a space is owed before the next text of the cell.</summary>
        public bool PendingSpace;

        public static State Initial => new() { LineEmpty = true };
    }

    private sealed record Hold(StringBuilder Text, State Saved);
}
