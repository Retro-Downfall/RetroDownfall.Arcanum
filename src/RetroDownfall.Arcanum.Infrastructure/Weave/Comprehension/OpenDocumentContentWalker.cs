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
/// cells, embedded objects, every frame representation after the first, the cached numbering labels a consumer
/// regenerates (<c>text:number</c>, <c>text:note-citation</c>), elements of OpenDocument's own metadata namespaces,
/// foreign markup outside paragraph content, and anything whose paragraph, text or shape style resolves to hidden. A
/// foreign element inside a paragraph's own content is transparent: its children are processed under the same rules,
/// as a consumer does (a LibreOffice content control shows its value).
/// </para>
/// <para>
/// Notes (<c>text:note</c>) are taken out of the paragraph flow whole: their bodies are kept and written by
/// <see cref="EmitNotes"/> after everything else, footnotes then endnotes, each class in document order. A table row
/// is its non-empty cells joined by tabs and ended by a newline, a cell being its paragraphs joined by single spaces;
/// inside a spreadsheet cell a table flattens into the cell's text.
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

    private readonly List<PendingNote> _notes = [];

    private readonly Stack<StringBuilder> _spareBuilders = new();

    private StringBuilder _paragraph = new();

    private StringBuilder _capture = new();

    private StringBuilder _row = new();

    private Mode _mode;

    private int _paragraphLevel;

    private bool _ignoreLeadingSpace;

    private bool _hiddenParagraph;

    private bool _overflow;

    private bool _endedWithNestedParagraph;

    private bool _inlineContent;

    private bool _inRow;

    private bool _rowTruncated;

    private int _cellIndex;

    private int _lastTextCell;

    private int _noteCharacters;

    private bool _notesTruncated;

    private enum Mode
    {
        /// <summary>Paragraphs go to the sink, each ended by a newline.</summary>
        Sink,

        /// <summary>Paragraphs are captured and joined by single spaces; tabs and line breaks become spaces (one spreadsheet cell).</summary>
        Cell,

        /// <summary>Paragraphs are captured, each ended by a newline (shapes floating over a sheet).</summary>
        Lines,
    }

    private readonly record struct PendingNote(bool Endnote, string? Text);

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
    /// single spaces when <paramref name="cell"/> is set, otherwise one per line. When the walk found no text and
    /// <paramref name="fallback"/> is given, the fallback (its whitespace collapsed) is the result.
    /// </summary>
    public string Capture(bool cell, string? fallback = null)
    {
        _mode = cell ? Mode.Cell : Mode.Lines;

        _capture.Clear();

        CaptureTruncated = false;

        WalkElement();

        if (_capture.Length == 0 && fallback is not null)
        {
            AppendCapture(CollapseWhitespace(fallback));
        }

        return FinishCapture();
    }

    /// <summary>
    /// Walks a spreadsheet cell that shows <paramref name="value"/> (then moves past it) and returns that value followed
    /// by the text of any shapes anchored in the cell. The cell's own text content is the producer's cache of what the
    /// value displays, so it is skipped.
    /// </summary>
    public string CaptureValueCell(string? value)
    {
        _mode = Mode.Cell;

        _capture.Clear();

        CaptureTruncated = false;

        if (value is not null)
        {
            AppendCapture(value);
        }

        WalkCellShapes();

        return FinishCapture();
    }

    private string FinishCapture()
    {
        _mode = Mode.Sink;

        if (CaptureTruncated)
        {
            DropDanglingHighSurrogate(_capture);
        }

        return _capture.ToString();
    }

    /// <summary>Collapses every run of space, tab, carriage return and line feed to one space and trims the result.</summary>
    internal static string CollapseWhitespace(string value)
    {
        StringBuilder collapsed = new(value.Length);

        foreach (char current in value)
        {
            if (current is ' ' or '\t' or '\r' or '\n')
            {
                if (collapsed.Length > 0 && collapsed[^1] != ' ')
                {
                    collapsed.Append(' ');
                }

                continue;
            }

            collapsed.Append(current);
        }

        return collapsed.ToString().TrimEnd(' ');
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

    /// <summary>
    /// Projects the children of the value cell the reader is on, then moves past its end: everything but the cell's own
    /// text content (paragraphs, lists, sections), which is the producer's cache of the displayed value.
    /// </summary>
    private void WalkCellShapes()
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

                case XmlNodeType.Element when _reader.NamespaceURI == OpenDocumentNames.Text:
                    xml.SkipElement();

                    break;

                case XmlNodeType.Element:
                    ProcessElement();

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

        bool wasInline = _inlineContent;

        // Only a text element, or a foreign element a paragraph's content let through, keeps its children inline.
        _inlineContent = wasInline && (_reader.NamespaceURI == OpenDocumentNames.Text || !IsOpenDocumentNamespace(_reader.NamespaceURI));

        DispatchElement();

        _inlineContent = wasInline;
    }

    private void DispatchElement()
    {
        if (_reader.NamespaceURI == OpenDocumentNames.Text)
        {
            switch (_reader.LocalName)
            {
                case "p" or "h":
                    ProcessParagraph();

                    return;

                case "note":
                    ProcessNote();

                    return;

                case "span" or "a" or "meta" or "meta-field" or "ruby-base" or "ruby-text"
                    when styles.IsHidden(OpenDocumentStyleTable.Switch.Text, OpenDocumentStyleTable.TextFamily, _reader.GetAttribute("style-name", OpenDocumentNames.Text)):
                    SkipHiddenInline();

                    return;

                case "hidden-paragraph" when _paragraphLevel > 0:
                    NoteHiddenParagraphField();

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

        if (_reader.NamespaceURI == OpenDocumentNames.Draw && _reader.LocalName == "frame")
        {
            ProcessFrame();

            return;
        }

        if (_reader.NamespaceURI == OpenDocumentNames.Table && _mode != Mode.Cell)
        {
            if (_reader.LocalName == "table-row" && !_inRow)
            {
                ProcessTableRow();

                return;
            }

            if (_reader.LocalName == "table-cell" && _inRow)
            {
                ProcessTableCell();

                return;
            }
        }

        WalkElement();
    }

    /// <summary>
    /// Projects the row the reader is on (then moves past it) as its non-empty cells joined by tabs, ended by a newline.
    /// Inside a spreadsheet cell the whole table is flattened to the cell's text instead, so a row can never split it.
    /// </summary>
    private void ProcessTableRow()
    {
        _inRow = true;

        _row.Clear();

        _cellIndex = 0;

        _lastTextCell = -1;

        _rowTruncated = false;

        WalkElement();

        _inRow = false;

        if (Stopped || _row.Length == 0)
        {
            return;
        }

        if (_paragraphLevel > 0)
        {
            AppendRowToParagraph();
        }
        else
        {
            EmitRow();
        }
    }

    /// <summary>
    /// Captures the cell the reader is on (then moves past it) the way a spreadsheet cell is captured: its paragraphs
    /// joined by single spaces and its tabs and line breaks turned into spaces. An empty cell keeps its place in the
    /// row, so the cells after it stay in their columns.
    /// </summary>
    private void ProcessTableCell()
    {
        string text = CaptureNested(Mode.Cell, out bool truncated);

        if (truncated)
        {
            _rowTruncated = true;
        }

        if (text.Length > 0)
        {
            AppendRowRun('\t', _lastTextCell < 0 ? _cellIndex : _cellIndex - _lastTextCell);

            AppendRow(text);

            _lastTextCell = _cellIndex;
        }

        _cellIndex++;
    }

    private void AppendRowRun(char value, int count)
    {
        for (int index = 0; index < count; index++)
        {
            if (_row.Length >= MaximumBufferedCharacters)
            {
                _rowTruncated = true;

                return;
            }

            _row.Append(value);
        }
    }

    private void AppendRow(ReadOnlySpan<char> text)
    {
        int room = MaximumBufferedCharacters - _row.Length;

        if (text.Length > room)
        {
            _rowTruncated = true;

            text = text[..room];
        }

        _row.Append(text);
    }

    private void EmitRow()
    {
        if (_rowTruncated)
        {
            DropDanglingHighSurrogate(_row);
        }

        if (_mode == Mode.Sink)
        {
            foreach (ReadOnlyMemory<char> chunk in _row.GetChunks())
            {
                if (!Emit(chunk.Span))
                {
                    return;
                }
            }

            if (_rowTruncated)
            {
                Truncate();
            }
            else
            {
                Emit("\n");
            }

            return;
        }

        foreach (ReadOnlyMemory<char> chunk in _row.GetChunks())
        {
            AppendCapture(chunk.Span);
        }

        if (_rowTruncated)
        {
            CaptureTruncated = true;
        }
        else
        {
            AppendCapture("\n");
        }
    }

    /// <summary>A row inside a paragraph's text box joins that paragraph's buffer on a line of its own, like a nested paragraph.</summary>
    private void AppendRowToParagraph()
    {
        EnsureLineStart();

        foreach (ReadOnlyMemory<char> chunk in _row.GetChunks())
        {
            foreach (char current in chunk.Span)
            {
                Append(current);
            }
        }

        if (_rowTruncated)
        {
            _overflow = true;
        }

        Append(Terminator);

        _endedWithNestedParagraph = true;

        _ignoreLeadingSpace = true;
    }

    /// <summary>
    /// Takes the note the reader is on (then moves past it) out of the paragraph flow: its body is kept to follow the
    /// document text and its citation, a label the consumer numbers itself, is dropped.
    /// </summary>
    private void ProcessNote()
    {
        bool endnote = _reader.GetAttribute("note-class", OpenDocumentNames.Text) == "endnote";

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

                case XmlNodeType.Element when _reader.NamespaceURI == OpenDocumentNames.Text && _reader.LocalName == "note-body":
                    RetainNote(endnote);

                    break;

                case XmlNodeType.Element:
                    xml.SkipElement();

                    break;

                default:
                    xml.Read();

                    break;
            }
        }
    }

    private void RetainNote(bool endnote)
    {
        // The place is reserved before the body is walked, so a note inside this one follows it.
        int slot = _notes.Count;

        _notes.Add(default);

        string text = CaptureNested(Mode.Lines, out bool truncated);

        if (truncated)
        {
            _notesTruncated = true;
        }

        if (text.Length == 0)
        {
            return;
        }

        if (_noteCharacters + text.Length > MaximumBufferedCharacters)
        {
            _notesTruncated = true;

            return;
        }

        _noteCharacters += text.Length;

        _notes[slot] = new PendingNote(endnote, text);
    }

    private void DropNotes(int from)
    {
        for (int index = from; index < _notes.Count; index++)
        {
            _noteCharacters -= _notes[index].Text?.Length ?? 0;
        }

        _notes.RemoveRange(from, _notes.Count - from);
    }

    /// <summary>
    /// Writes the notes taken out of the text: footnotes first, then endnotes, each class in document order, every note as
    /// its own paragraph or paragraphs. Call it once, after the last of the document has been walked.
    /// </summary>
    public void EmitNotes()
    {
        foreach (bool endnotes in (ReadOnlySpan<bool>)[false, true])
        {
            foreach (PendingNote note in _notes)
            {
                if (note.Endnote == endnotes && note.Text is { Length: > 0 } text && !Emit(text))
                {
                    return;
                }
            }
        }

        if (_notesTruncated && !Stopped)
        {
            Truncate();
        }
    }

    /// <summary>
    /// Walks the element the reader is on (then moves past it) in a paragraph context of its own and returns what it
    /// captured, paragraphs joined by single spaces for <see cref="Mode.Cell"/> and one per line for
    /// <see cref="Mode.Lines"/>. The context the walk began in is put back exactly, so a table cell or note body can be
    /// walked in the middle of a paragraph.
    /// </summary>
    private string CaptureNested(Mode mode, out bool truncated)
    {
        StringBuilder outerParagraph = _paragraph;

        StringBuilder outerCapture = _capture;

        StringBuilder outerRow = _row;

        Mode outerMode = _mode;

        int outerLevel = _paragraphLevel;

        int outerCellIndex = _cellIndex;

        int outerLastTextCell = _lastTextCell;

        bool outerIgnore = _ignoreLeadingSpace;

        bool outerHidden = _hiddenParagraph;

        bool outerOverflow = _overflow;

        bool outerEnded = _endedWithNestedParagraph;

        bool outerInline = _inlineContent;

        bool outerInRow = _inRow;

        bool outerRowTruncated = _rowTruncated;

        bool outerTruncated = CaptureTruncated;

        _paragraph = RentBuilder();

        _capture = RentBuilder();

        _row = RentBuilder();

        _mode = mode;

        _paragraphLevel = 0;

        _ignoreLeadingSpace = false;

        _hiddenParagraph = false;

        _overflow = false;

        _endedWithNestedParagraph = false;

        _inlineContent = false;

        _inRow = false;

        _rowTruncated = false;

        CaptureTruncated = false;

        WalkElement();

        if (CaptureTruncated)
        {
            DropDanglingHighSurrogate(_capture);
        }

        string text = _capture.ToString();

        truncated = CaptureTruncated;

        ReturnBuilder(_paragraph);

        ReturnBuilder(_capture);

        ReturnBuilder(_row);

        _paragraph = outerParagraph;

        _capture = outerCapture;

        _row = outerRow;

        _mode = outerMode;

        _paragraphLevel = outerLevel;

        _cellIndex = outerCellIndex;

        _lastTextCell = outerLastTextCell;

        _ignoreLeadingSpace = outerIgnore;

        _hiddenParagraph = outerHidden;

        _overflow = outerOverflow;

        _endedWithNestedParagraph = outerEnded;

        _inlineContent = outerInline;

        _inRow = outerInRow;

        _rowTruncated = outerRowTruncated;

        CaptureTruncated = outerTruncated;

        return text;
    }

    private StringBuilder RentBuilder() => _spareBuilders.TryPop(out StringBuilder? builder) ? builder.Clear() : new StringBuilder();

    private void ReturnBuilder(StringBuilder builder)
    {
        if (builder.Capacity <= 4096)
        {
            _spareBuilders.Push(builder);
        }
    }

    /// <summary>
    /// A frame's content children (text box, table, image, object and the like) are alternative representations of one
    /// object, and a consumer renders only the first it can. Text is projected only when that first one is a text box
    /// or a table; every later representation, and anything that is not a content child, is handled as usual or skipped.
    /// Characters directly inside a frame belong to no paragraph and are never projected.
    /// </summary>
    private void ProcessFrame()
    {
        if (_reader.IsEmptyElement)
        {
            xml.Read();

            return;
        }

        xml.Read();

        bool representationSeen = false;

        while (!Stopped && !_reader.EOF)
        {
            switch (_reader.NodeType)
            {
                case XmlNodeType.EndElement:
                    xml.Read();

                    return;

                case XmlNodeType.Element when IsFrameRepresentation():
                    if (!representationSeen && IsTextBearingRepresentation())
                    {
                        ProcessElement();
                    }
                    else
                    {
                        xml.SkipElement();
                    }

                    representationSeen = true;

                    break;

                case XmlNodeType.Element:
                    ProcessElement();

                    break;

                default:
                    xml.Read();

                    break;
            }
        }
    }

    private bool IsFrameRepresentation() =>
        _reader.NamespaceURI == OpenDocumentNames.Draw
            ? _reader.LocalName is "text-box" or "image" or "object" or "object-ole" or "applet" or "plugin" or "floating-frame"
            : _reader.NamespaceURI == OpenDocumentNames.Table && _reader.LocalName == "table";

    private bool IsTextBearingRepresentation() =>
        _reader.NamespaceURI == OpenDocumentNames.Table || _reader.LocalName == "text-box";

    private void NoteHiddenParagraphField()
    {
        if (_reader.GetAttribute("is-hidden", OpenDocumentNames.Text) != "false")
        {
            _hiddenParagraph = true;
        }
    }

    /// <summary>
    /// Skips an inline element whose text is hidden, but still honours a <c>text:hidden-paragraph</c> field inside it:
    /// Writer decides whether a paragraph is hidden from the field alone, not from whether the characters that carry
    /// the field are hidden too. The search goes through inline text elements only; a paragraph, note or frame inside
    /// has its own paragraph and its own fields.
    /// </summary>
    private void SkipHiddenInline()
    {
        if (_paragraphLevel == 0 || _reader.IsEmptyElement)
        {
            xml.SkipElement();

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

                case XmlNodeType.Element when _reader.NamespaceURI == OpenDocumentNames.Text && _reader.LocalName == "hidden-paragraph":
                    NoteHiddenParagraphField();

                    xml.SkipElement();

                    break;

                case XmlNodeType.Element when _reader.NamespaceURI == OpenDocumentNames.Text && _reader.LocalName is not ("p" or "h" or "note" or "list" or "section"):
                    SkipHiddenInline();

                    break;

                case XmlNodeType.Element:
                    xml.SkipElement();

                    break;

                default:
                    xml.Read();

                    break;
            }
        }
    }

    private bool IsExcluded()
    {
        string name = _reader.LocalName;

        switch (_reader.NamespaceURI)
        {
            case OpenDocumentNames.Text:
                return name is "tracked-changes" or "hidden-text" or "annotation" or "script" or "note-citation" or "number"
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
                // OpenDocument's own metadata, script and form namespaces carry nothing a reader sees. A foreign element
                // (another namespace, or none) is skipped with its content too, except inside a paragraph's own content,
                // where a consumer processes what an element it does not know contains (ODF 1.2, section 3.17) unless the
                // element says not to; a block-level foreign element is ignored, so text inside it is hidden text.
                return IsOpenDocumentNamespace(_reader.NamespaceURI)
                    || !_inlineContent
                    || _reader.GetAttribute("process-content", OpenDocumentNames.Office) == "false";
        }
    }

    private static bool IsOpenDocumentNamespace(string uri) =>
        uri.StartsWith("urn:oasis:names:tc:opendocument:xmlns:", StringComparison.Ordinal)
        || uri is "http://purl.org/dc/elements/1.1/" or "http://www.w3.org/1998/Math/MathML" or "http://www.w3.org/2002/xforms" or "http://www.w3.org/2001/xml-events";

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

        int notesBefore = _notes.Count;

        if (!outermost)
        {
            EnsureLineStart();
        }

        _ignoreLeadingSpace = true;

        _paragraphLevel++;

        bool wasInline = _inlineContent;

        _inlineContent = true;

        WalkElement();

        _inlineContent = wasInline;

        _paragraphLevel--;

        bool hidden = _hiddenParagraph;

        _hiddenParagraph = savedHidden;

        if (hidden)
        {
            // A note anchored in a paragraph a field hides is hidden with it.
            DropNotes(notesBefore);
        }

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
