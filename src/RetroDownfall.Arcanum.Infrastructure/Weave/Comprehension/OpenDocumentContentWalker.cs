using System.Text;
using System.Xml;

using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

/// <summary>
/// Walks the children of one OpenDocument body element and projects the text of its paragraphs. Shared by the three
/// OpenDocument extractors, which differ only in how they find the elements to walk.
/// </summary>
/// <remarks>
/// <para>
/// The walker advances the XML only through <see cref="BoundedXmlReader.Read"/> and
/// <see cref="BoundedXmlReader.SkipElement"/>. Every element-processing method has one contract: it is called with the
/// reader on an element start and returns with the reader on the node after that element's end.
/// </para>
/// <para>
/// Text is buffered one outermost paragraph at a time, so a decision that depends on the whole paragraph
/// (<c>text:hidden-paragraph</c>) can be made before anything reaches the sink. A paragraph nested in another (a text
/// box or footnote anchored in a paragraph) shares the outermost paragraph's buffer and starts on its own line.
/// </para>
/// <para>
/// Hidden content (plan decision D8) is dropped by skipping its subtree: tracked changes, annotations, hidden text,
/// hidden or conditional sections, index templates, script fields, alternative text, forms, speaker notes, covered
/// cells, embedded objects, and anything whose paragraph, text or shape style resolves to hidden.
/// </para>
/// </remarks>
internal sealed class OpenDocumentContentWalker(
    BoundedXmlReader xml,
    OpenDocumentStyleTable styles,
    ISessionAttachmentDocumentTextSink output)
{
    /// <summary>
    /// The most characters buffered for one outermost paragraph (or one captured cell). It equals the default sink
    /// ceiling: text beyond it could never be emitted, so it is dropped and the output is marked truncated.
    /// </summary>
    internal const int MaximumBufferedCharacters = 262_144;

    private const int MaximumSpaceRun = 64;

    private readonly XmlReader _reader = xml.Reader;

    private readonly StringBuilder _paragraph = new();

    private readonly StringBuilder _capture = new();

    private Mode _mode;

    private int _paragraphLevel;

    private bool _ignoreLeadingSpace;

    private bool _hiddenParagraph;

    private bool _overflow;

    private bool _endedWithNestedParagraph;

    private enum Mode
    {
        /// <summary>Paragraphs go to the sink, each ended by a newline.</summary>
        Sink,

        /// <summary>Paragraphs are captured and joined by single spaces; tabs and line breaks become spaces (one spreadsheet cell).</summary>
        Cell,

        /// <summary>Paragraphs are captured, each ended by a newline (shapes floating over a sheet).</summary>
        Lines,
    }

    /// <summary>True once the sink refused more text or retention overflowed; every walk unwinds immediately.</summary>
    public bool Stopped { get; private set; }

    /// <summary>True when the last <see cref="Capture"/> had to drop text because its buffer was full.</summary>
    public bool CaptureTruncated { get; private set; }

    private char Terminator => _mode == Mode.Cell ? ' ' : '\n';

    /// <summary>Writes <paramref name="text"/> to the sink; false (and <see cref="Stopped"/>) when the sink refuses it.</summary>
    public bool Emit(ReadOnlySpan<char> text)
    {
        if (Stopped)
        {
            return false;
        }

        if (output.TryAppend(text))
        {
            return true;
        }

        Stopped = true;

        return false;
    }

    /// <summary>Reports that text was withheld and stops the walk.</summary>
    public void Truncate()
    {
        output.MarkTruncated();

        Stopped = true;
    }

    /// <summary>
    /// Walks the element the reader is on (then moves past it) and returns its paragraphs as one string: joined by
    /// single spaces when <paramref name="cell"/> is set, otherwise one per line.
    /// </summary>
    public string Capture(bool cell)
    {
        _mode = cell ? Mode.Cell : Mode.Lines;

        _capture.Clear();

        CaptureTruncated = false;

        WalkElement();

        _mode = Mode.Sink;

        if (CaptureTruncated)
        {
            DropDanglingHighSurrogate(_capture);
        }

        return _capture.ToString();
    }

    private static void DropDanglingHighSurrogate(StringBuilder text)
    {
        if (text.Length > 0 && char.IsHighSurrogate(text[^1]))
        {
            text.Length--;
        }
    }

    /// <summary>Projects the children of the element the reader is on, then moves past its end.</summary>
    public void WalkElement()
    {
        if (_reader.IsEmptyElement)
        {
            xml.Read();

            return;
        }

        xml.Read();

        while (!Stopped && !_reader.EOF)
        {
            switch (_reader.NodeType)
            {
                case XmlNodeType.EndElement:
                    xml.Read();

                    return;

                case XmlNodeType.Element:
                    ProcessElement();

                    break;

                case XmlNodeType.Text or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace or XmlNodeType.CDATA:
                    if (_paragraphLevel > 0)
                    {
                        AppendText(_reader.Value);
                    }

                    xml.Read();

                    break;

                default:
                    xml.Read();

                    break;
            }
        }
    }

    private void ProcessElement()
    {
        if (IsExcluded())
        {
            xml.SkipElement();

            return;
        }

        if (_reader.NamespaceURI == OpenDocumentNames.Text)
        {
            switch (_reader.LocalName)
            {
                case "p" or "h":
                    ProcessParagraph();

                    return;

                case "span" or "a" or "meta" or "meta-field" or "ruby-base" or "ruby-text"
                    when styles.IsHidden(OpenDocumentStyleTable.Switch.Text, OpenDocumentStyleTable.TextFamily, _reader.GetAttribute("style-name", OpenDocumentNames.Text)):
                    xml.SkipElement();

                    return;

                case "hidden-paragraph" when _paragraphLevel > 0:
                    if (_reader.GetAttribute("is-hidden", OpenDocumentNames.Text) != "false")
                    {
                        _hiddenParagraph = true;
                    }

                    xml.SkipElement();

                    return;

                case "s" when _paragraphLevel > 0:
                    AppendRun(' ', (int)OpenDocumentNames.ParseCount(_reader.GetAttribute("c", OpenDocumentNames.Text), MaximumSpaceRun));

                    _ignoreLeadingSpace = false;

                    xml.SkipElement();

                    return;

                case "tab" when _paragraphLevel > 0:
                    Append(_mode == Mode.Cell ? ' ' : '\t');

                    _ignoreLeadingSpace = false;

                    xml.SkipElement();

                    return;

                case "line-break" when _paragraphLevel > 0:
                    Append(_mode == Mode.Cell ? ' ' : '\n');

                    _ignoreLeadingSpace = false;

                    xml.SkipElement();

                    return;
            }
        }

        WalkElement();
    }

    private bool IsExcluded()
    {
        string name = _reader.LocalName;

        switch (_reader.NamespaceURI)
        {
            case OpenDocumentNames.Text:
                return name is "tracked-changes" or "hidden-text" or "annotation" or "script"
                    || name.EndsWith("-source", StringComparison.Ordinal)
                    || _reader.GetAttribute("display", OpenDocumentNames.Text) is "none" or "condition";

            case OpenDocumentNames.Office:
                return name is "annotation" or "forms";

            case OpenDocumentNames.Svg:
                return name is "title" or "desc";

            case OpenDocumentNames.Presentation:
                return name == "notes";

            case OpenDocumentNames.Table:
                return name is "covered-table-cell" or "title" or "desc"
                    || (name == "table-cell" && IsHiddenByStyle(OpenDocumentStyleTable.Switch.Text, OpenDocumentStyleTable.TableCellFamily, _reader.GetAttribute("style-name", OpenDocumentNames.Table)));

            case OpenDocumentNames.Draw:
                return name is "image" or "object" or "object-ole" or "plugin" or "applet" or "floating-frame"
                    || IsHiddenShape();

            default:
                return false;
        }
    }

    private bool IsHiddenShape()
    {
        if (_reader.GetAttribute("display", OpenDocumentNames.Draw) is "none" or "printer")
        {
            return true;
        }

        return styles.IsLayerHidden(_reader.GetAttribute("layer", OpenDocumentNames.Draw))
            || IsHiddenByStyle(OpenDocumentStyleTable.Switch.Graphic, OpenDocumentStyleTable.GraphicFamily, _reader.GetAttribute("style-name", OpenDocumentNames.Draw))
            || IsHiddenByStyle(OpenDocumentStyleTable.Switch.Graphic, OpenDocumentStyleTable.PresentationFamily, _reader.GetAttribute("style-name", OpenDocumentNames.Presentation))
            || IsHiddenByStyle(OpenDocumentStyleTable.Switch.Text, OpenDocumentStyleTable.ParagraphFamily, _reader.GetAttribute("text-style-name", OpenDocumentNames.Draw));
    }

    private bool IsHiddenByStyle(OpenDocumentStyleTable.Switch hiding, string family, string? name) =>
        name is not null && styles.IsHidden(hiding, family, name);

    private void ProcessParagraph()
    {
        if (styles.IsHidden(OpenDocumentStyleTable.Switch.Text, OpenDocumentStyleTable.ParagraphFamily, _reader.GetAttribute("style-name", OpenDocumentNames.Text)))
        {
            xml.SkipElement();

            return;
        }

        bool outermost = _paragraphLevel == 0;

        bool savedHidden = _hiddenParagraph;

        int before = _paragraph.Length;

        if (!outermost)
        {
            EnsureLineStart();
        }

        _ignoreLeadingSpace = true;

        _paragraphLevel++;

        WalkElement();

        _paragraphLevel--;

        bool hidden = _hiddenParagraph;

        _hiddenParagraph = savedHidden;

        if (Stopped)
        {
            return;
        }

        if (outermost)
        {
            if (!hidden)
            {
                Flush();

                if (_overflow)
                {
                    TruncateOrMark();
                }
            }

            _paragraph.Clear();

            _overflow = false;

            _endedWithNestedParagraph = false;

            return;
        }

        _ignoreLeadingSpace = true;

        if (hidden)
        {
            _paragraph.Length = Math.Min(before, _paragraph.Length);

            return;
        }

        Append(Terminator);

        _endedWithNestedParagraph = true;
    }

    private void EnsureLineStart()
    {
        if (_paragraph.Length > 0 && _paragraph[^1] != Terminator)
        {
            Append(Terminator);
        }
    }

    private void TruncateOrMark()
    {
        if (_mode == Mode.Sink)
        {
            Truncate();

            return;
        }

        CaptureTruncated = true;
    }

    private void Flush()
    {
        switch (_mode)
        {
            case Mode.Sink:
                FlushToSink();

                break;

            case Mode.Cell:
                FlushToCell();

                break;

            default:
                FlushToLines();

                break;
        }
    }

    private void FlushToSink()
    {
        if (_overflow)
        {
            // The paragraph was cut at the retention bound, possibly between the halves of a surrogate pair.
            DropDanglingHighSurrogate(_paragraph);
        }

        foreach (ReadOnlyMemory<char> chunk in _paragraph.GetChunks())
        {
            if (!Emit(chunk.Span))
            {
                return;
            }
        }

        if (!_endedWithNestedParagraph && !_overflow)
        {
            Emit("\n");
        }
    }

    private void FlushToCell()
    {
        ReadOnlySpan<char> trimmed = _paragraph.ToString().AsSpan().Trim(' ');

        if (trimmed.IsEmpty)
        {
            return;
        }

        if (_capture.Length > 0)
        {
            AppendCapture(" ");
        }

        AppendCapture(trimmed);
    }

    private void FlushToLines()
    {
        foreach (ReadOnlyMemory<char> chunk in _paragraph.GetChunks())
        {
            AppendCapture(chunk.Span);
        }

        if (!_endedWithNestedParagraph)
        {
            AppendCapture("\n");
        }
    }

    private void AppendCapture(ReadOnlySpan<char> text)
    {
        int room = MaximumBufferedCharacters - _capture.Length;

        if (text.Length > room)
        {
            CaptureTruncated = true;

            text = text[..room];
        }

        _capture.Append(text);
    }

    private void AppendText(string value)
    {
        foreach (char current in value)
        {
            if (current is ' ' or '\t' or '\r' or '\n')
            {
                if (!_ignoreLeadingSpace)
                {
                    Append(' ');
                }

                _ignoreLeadingSpace = true;

                continue;
            }

            Append(current);

            _ignoreLeadingSpace = false;
        }
    }

    private void AppendRun(char value, int count)
    {
        for (int index = 0; index < count; index++)
        {
            Append(value);
        }
    }

    private void Append(char value)
    {
        if (_paragraph.Length >= MaximumBufferedCharacters)
        {
            _overflow = true;

            return;
        }

        _paragraph.Append(value);

        _endedWithNestedParagraph = false;
    }
}
