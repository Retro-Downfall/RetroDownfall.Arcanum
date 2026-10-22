using System.Globalization;
using System.Xml;

using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

/// <summary>
/// Walks one WordprocessingML part (the document body, or a notes part) forward-only and projects the text a reader
/// sees into the sink. Elements are matched by namespace and local name; the reader is advanced only through
/// <see cref="BoundedXmlReader.Read"/> and <see cref="BoundedXmlReader.SkipElement"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every handler consumes the element it is called on, including its end tag, and leaves the reader on the node that
/// follows. Text is emitted only from <c>w:t</c> and <c>m:t</c> content and a fixed set of run characters, so character
/// data that is not text content (property values, instructions, stray text) can never reach the output. Children are
/// enumerated through <see cref="WordprocessingChildren"/>, so <c>mc:AlternateContent</c> is already resolved wherever it
/// occurs and no handler ever sees it.
/// </para>
/// <para>
/// Hiding is decided before a run's content is read: a run's <c>w:rPr</c>, a paragraph's <c>w:pPr</c>, a table row's
/// <c>w:trPr</c> and a table cell's <c>w:tcPr</c> must each precede the content they govern, and a package that places them
/// later is refused rather than emitted before its flag is known. A drawing is the one exception: its hidden flags (the
/// anchor's attribute, <c>wp:docPr</c>) may come before or after its graphic, so its text is held until the drawing ends.
/// A run is hidden by its own <c>vanish</c> (either value wins), otherwise by any of its character style, its paragraph's
/// style, its table's style or the document defaults. Word treats <c>vanish</c> as a toggle property across style levels;
/// that exclusive-or is deliberately not modelled, so this union only ever hides more.
/// </para>
/// <para>
/// A footnote or endnote reference is recorded only where the reference itself is shown (not deleted, hidden, in field
/// code or in a hidden drawing), so the notes projected after the body are exactly the ones a reader can reach.
/// </para>
/// </remarks>
internal sealed class WordprocessingPartWalker
{
    /// <summary>Complex fields nest this deep at most; one bit per level tracks whether the level is still field code.</summary>
    internal const int MaximumFieldDepth = 64;

    /// <summary>A body may show this many note references at most (repeats included).</summary>
    internal const int MaximumNoteReferences = 65_536;

    private readonly BoundedXmlReader _xml;

    private readonly XmlReader _reader;

    private readonly WordprocessingStyleSheet _styles;

    private readonly WordprocessingOutput _output;

    private readonly List<long> _noteReferences = [];

    private int _fieldDepth;

    private ulong _fieldCodeLevels;

    private bool _paragraphHidden;

    private bool _tableHidden;

    private int _mathDepth;

    private int _displayMathDepth;

    private bool _inRow;

    private int _rowCells;

    public WordprocessingPartWalker(BoundedXmlReader xml, WordprocessingStyleSheet styles, ISessionAttachmentDocumentTextSink output)
    {
        _xml = xml;

        _reader = xml.Reader;

        _styles = styles;

        _output = new WordprocessingOutput(output);

        _paragraphHidden = styles.IsParagraphStyleHidden(null);
    }

    /// <summary>True once the sink refused text or held text overflowed; the caller must stop.</summary>
    public bool Stopped => _output.Stopped;

    /// <summary>The footnotes a shown reference in the walked body reaches, in first-reference order.</summary>
    public IReadOnlyList<int> ReferencedFootnotes => DistinctReferences(endnotes: false);

    /// <summary>The endnotes a shown reference in the walked body reaches, in first-reference order.</summary>
    public IReadOnlyList<int> ReferencedEndnotes => DistinctReferences(endnotes: true);

    /// <summary>
    /// Walks the main part whose root element the reader is positioned on. Only <c>w:body</c> is the main story; the
    /// document background and anything else beside it is a page fill or furniture, never flowing text.
    /// </summary>
    public void Walk()
    {
        if (!WordprocessingXml.EnterElement(_xml))
        {
            return;
        }

        WordprocessingChildren children = new(_xml);

        while (!Stopped && children.MoveNext())
        {
            if (IsWordprocessingElement("body"))
            {
                WalkChildren();
            }
            else
            {
                _xml.SkipElement();
            }
        }
    }

    /// <summary>
    /// Walks a footnotes or endnotes part whose root element the reader is positioned on, projecting the
    /// <paramref name="noteName"/> notes whose ids are in <paramref name="referenced"/>, in that order. Separator notes
    /// are skipped, and so is every note nothing shown references (still read, so a malformed part is refused).
    /// </summary>
    /// <remarks>
    /// A note that is met in its turn streams straight to the sink. One met before its turn (the part lists notes in a
    /// different order than they were first referenced) is held, bounded, until the notes before it have been projected.
    /// </remarks>
    public void WalkNotes(string noteName, IReadOnlyList<int> referenced)
    {
        if (!WordprocessingXml.EnterElement(_xml))
        {
            return;
        }

        Dictionary<int, int> positions = new(referenced.Count);

        for (int index = 0; index < referenced.Count; index++)
        {
            positions[referenced[index]] = index;
        }

        Dictionary<int, string> held = [];

        HashSet<int> met = [];

        int next = 0;

        WordprocessingChildren children = new(_xml);

        while (!Stopped && children.MoveNext())
        {
            if (!IsWordprocessingElement(noteName)
                || _reader.GetAttribute("type", _reader.NamespaceURI) is "separator" or "continuationSeparator" or "continuationNotice"
                || !TryReadId(out int id)
                || !positions.TryGetValue(id, out int position))
            {
                _xml.SkipElement();

                continue;
            }

            if (!met.Add(id))
            {
                // Which of two notes with one id a reader would see is not specified.
                throw new DocumentPackageRejectedException(DocumentPackageRejection.FormatMismatch);
            }

            if (position == next)
            {
                WalkStory();

                next++;

                EmitReleasedNotes(referenced, held, ref next);

                continue;
            }

            _output.BeginHold();

            WalkStory();

            held[id] = _output.TakeHeld();
        }

        // Referenced notes the part lacks leave gaps; everything held behind them goes out now, in order.
        for (; !Stopped && next < referenced.Count; next++)
        {
            if (held.Remove(referenced[next], out string? text))
            {
                _output.EmitHeld(text);
            }
        }
    }

    private void EmitReleasedNotes(IReadOnlyList<int> referenced, Dictionary<int, string> held, ref int next)
    {
        while (!Stopped && next < referenced.Count && held.Remove(referenced[next], out string? text))
        {
            _output.EmitHeld(text);

            next++;
        }
    }

    private bool TryReadId(out int id) =>
        int.TryParse(_reader.GetAttribute("id", _reader.NamespaceURI), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out id);

    private List<int> DistinctReferences(bool endnotes)
    {
        long kind = endnotes ? 1 : 0;

        HashSet<int> seen = [];

        List<int> ordered = [];

        foreach (long reference in _noteReferences)
        {
            if ((reference >> 32) != kind)
            {
                continue;
            }

            int id = unchecked((int)(uint)(reference & uint.MaxValue));

            if (seen.Add(id))
            {
                ordered.Add(id);
            }
        }

        return ordered;
    }

    private void WalkChildren()
    {
        if (!WordprocessingXml.EnterElement(_xml))
        {
            return;
        }

        WordprocessingChildren children = new(_xml);

        while (!Stopped && children.MoveNext())
        {
            HandleElement();
        }
    }

    private bool IsWordprocessingElement(string localName) =>
        _reader.LocalName == localName && WordprocessingXml.IsWordprocessing(_reader.NamespaceURI);

    private void HandleElement()
    {
        string namespaceUri = _reader.NamespaceURI;

        if (WordprocessingXml.IsWordprocessing(namespaceUri))
        {
            switch (_reader.LocalName)
            {
                case "p":
                    WalkParagraph();

                    return;

                case "r":
                    WalkRun();

                    return;

                case "tbl":
                    WalkTable();

                    return;

                case "tr":
                    WalkRow();

                    return;

                case "tc":
                    WalkCell();

                    return;

                case "txbxContent":
                    WalkTextBoxContent();

                    return;

                case "del" or "moveFrom" or "delText" or "instrText" or "delInstrText" or "rPr" or "pPr" or "sectPr" or "sdtPr" or "rt":
                    _xml.SkipElement();

                    return;
            }
        }
        else if (WordprocessingXml.IsMath(namespaceUri))
        {
            // Math elements mean something only inside an equation; a stray one is not text a reader sees.
            if (_mathDepth > 0 || _reader.LocalName is "oMath" or "oMathPara")
            {
                WalkMathElement();

                return;
            }
        }
        else if (WordprocessingXml.IsWordprocessingDrawing(namespaceUri))
        {
            if (_reader.LocalName is "inline" or "anchor")
            {
                WalkDrawingObject();

                return;
            }
        }
        else if (WordprocessingXml.IsDrawingShapeContainer(namespaceUri, _reader.LocalName))
        {
            WalkDrawingShape();

            return;
        }
        else if (namespaceUri == WordprocessingXml.VectorMarkupNamespace)
        {
            string? style = _reader.GetAttribute("style");

            if (style is not null && WordprocessingXml.DeclaresHiddenShape(style))
            {
                _xml.SkipElement();

                return;
            }
        }

        // A stray mc:Choice or mc:Fallback (one outside an mc:AlternateContent), like any other element Word does not
        // understand, is not a content namespace and is skipped here.
        if (!WordprocessingXml.IsContentNamespace(namespaceUri))
        {
            _xml.SkipElement();

            return;
        }

        WalkChildren();
    }

    /// <summary>
    /// Walks a <c>w:tbl</c>. A table style that hides hides everything inside the table, nested tables and text boxes
    /// included, whatever style those carry themselves.
    /// </summary>
    private void WalkTable()
    {
        bool enclosingHidden = _tableHidden;

        _tableHidden = enclosingHidden || _styles.IsTableStyleHidden(null);

        if (WordprocessingXml.EnterElement(_xml))
        {
            bool propertiesSeen = false;

            bool contentSeen = false;

            WordprocessingChildren children = new(_xml);

            while (!Stopped && children.MoveNext())
            {
                if (IsWordprocessingElement("tblPr"))
                {
                    if (propertiesSeen || contentSeen)
                    {
                        throw new DocumentPackageRejectedException(DocumentPackageRejection.FormatMismatch);
                    }

                    propertiesSeen = true;

                    // Read first: a short-circuit on an already hidden enclosing table would leave w:tblPr unread.
                    bool ownHidden = ReadTableProperties();

                    _tableHidden = enclosingHidden || ownHidden;
                }
                else if (WordprocessingXml.IsRangeMarkup(_reader.NamespaceURI, _reader.LocalName))
                {
                    // The schema lets range markup precede w:tblPr. It is empty by definition, so it is never walked:
                    // nothing inside it may be emitted before the table's hidden flag is known.
                    _xml.SkipElement();
                }
                else
                {
                    contentSeen = true;

                    HandleElement();
                }
            }
        }

        _tableHidden = enclosingHidden;
    }

    /// <summary>
    /// Reads <c>w:tblPr</c> and returns whether the table's style hides its text. Only a direct <c>w:tblStyle</c> counts;
    /// the recorded previous properties of a tracked change are skipped.
    /// </summary>
    private bool ReadTableProperties()
    {
        bool styled = false;

        bool hidden = false;

        if (WordprocessingXml.EnterElement(_xml))
        {
            WordprocessingChildren children = new(_xml);

            while (children.MoveNext())
            {
                if (IsWordprocessingElement("tblStyle"))
                {
                    styled = true;

                    hidden |= _styles.IsTableStyleHidden(_reader.GetAttribute("val", _reader.NamespaceURI));
                }

                _xml.SkipElement();
            }
        }

        return styled ? hidden : _styles.IsTableStyleHidden(null);
    }

    /// <summary>
    /// Walks a <c>w:tr</c>: its cells joined by a tab and ended by a newline (a space inside a cell, for a nested table).
    /// A row that is tracked-deleted (<c>w:trPr/w:del</c>) is not text and leaves nothing behind. The row properties come
    /// first, as in the schema; range markup before them is excused, anything else is not.
    /// </summary>
    private void WalkRow()
    {
        bool enclosingInRow = _inRow;

        int enclosingCells = _rowCells;

        _inRow = true;

        _rowCells = 0;

        bool deleted = false;

        if (WordprocessingXml.EnterElement(_xml))
        {
            bool propertiesSeen = false;

            bool contentSeen = false;

            WordprocessingChildren children = new(_xml);

            while (!Stopped && children.MoveNext())
            {
                if (deleted || WordprocessingXml.IsRangeMarkup(_reader.NamespaceURI, _reader.LocalName) || IsWordprocessingElement("tblPrEx"))
                {
                    _xml.SkipElement();
                }
                else if (IsWordprocessingElement("trPr"))
                {
                    if (propertiesSeen || contentSeen)
                    {
                        throw new DocumentPackageRejectedException(DocumentPackageRejection.FormatMismatch);
                    }

                    propertiesSeen = true;

                    deleted = ReadTrackedDeletion("del");
                }
                else
                {
                    contentSeen = true;

                    HandleElement();
                }
            }
        }

        _inRow = enclosingInRow;

        _rowCells = enclosingCells;

        if (!deleted)
        {
            EndRowMark();
        }
    }

    /// <summary>
    /// Walks a <c>w:tc</c>. A cell that is tracked-deleted (<c>w:tcPr/w:cellDel</c>) is not text and leaves no tab behind.
    /// Outside any row a cell is only a wrapper, and its paragraphs keep their own lines.
    /// </summary>
    private void WalkCell()
    {
        bool deleted = false;

        bool begun = false;

        if (WordprocessingXml.EnterElement(_xml))
        {
            bool propertiesSeen = false;

            bool contentSeen = false;

            WordprocessingChildren children = new(_xml);

            while (!Stopped && children.MoveNext())
            {
                if (deleted)
                {
                    _xml.SkipElement();
                }
                else if (IsWordprocessingElement("tcPr"))
                {
                    if (propertiesSeen || contentSeen)
                    {
                        throw new DocumentPackageRejectedException(DocumentPackageRejection.FormatMismatch);
                    }

                    propertiesSeen = true;

                    deleted = ReadTrackedDeletion("cellDel");
                }
                else
                {
                    contentSeen = true;

                    BeginCell(ref begun);

                    HandleElement();
                }
            }
        }

        if (!deleted)
        {
            BeginCell(ref begun);
        }

        if (begun && _inRow)
        {
            _output.ExitCell();
        }
    }

    /// <summary>Starts the cell once it is known not to be deleted: the tab that separates it from the cell before it, then its text.</summary>
    private void BeginCell(ref bool begun)
    {
        if (begun)
        {
            return;
        }

        begun = true;

        if (!_inRow)
        {
            return;
        }

        if (_rowCells++ > 0 && _fieldCodeLevels == 0)
        {
            _output.CellBoundary();
        }

        _output.EnterCell();
    }

    /// <summary>
    /// Reads <c>w:trPr</c> or <c>w:tcPr</c> and returns whether it carries a direct child named
    /// <paramref name="deletionName"/> (<c>del</c> for a row, <c>cellDel</c> for a cell). The recorded previous properties
    /// of a tracked change are skipped unread.
    /// </summary>
    private bool ReadTrackedDeletion(string deletionName)
    {
        bool deleted = false;

        if (WordprocessingXml.EnterElement(_xml))
        {
            WordprocessingChildren children = new(_xml);

            while (children.MoveNext())
            {
                deleted |= IsWordprocessingElement(deletionName);

                _xml.SkipElement();
            }
        }

        return deleted;
    }

    private void WalkParagraph()
    {
        bool enclosingHidden = _paragraphHidden;

        _paragraphHidden = _styles.IsParagraphStyleHidden(null);

        if (WordprocessingXml.EnterElement(_xml))
        {
            int index = 0;

            WordprocessingChildren children = new(_xml);

            while (!Stopped && children.MoveNext())
            {
                if (IsWordprocessingElement("pPr"))
                {
                    if (index != 0)
                    {
                        throw new DocumentPackageRejectedException(DocumentPackageRejection.FormatMismatch);
                    }

                    _paragraphHidden = ReadParagraphProperties();
                }
                else
                {
                    HandleElement();
                }

                index++;
            }
        }

        _paragraphHidden = enclosingHidden;

        EndParagraphMark();
    }

    /// <summary>
    /// Reads <c>w:pPr</c> and returns whether the paragraph's style hides its text. Only a direct <c>w:pStyle</c> counts; the
    /// recorded previous properties of a tracked change, tab stops and the paragraph mark's formatting are skipped.
    /// </summary>
    private bool ReadParagraphProperties()
    {
        bool styled = false;

        bool hidden = false;

        if (WordprocessingXml.EnterElement(_xml))
        {
            WordprocessingChildren children = new(_xml);

            while (children.MoveNext())
            {
                if (IsWordprocessingElement("pStyle"))
                {
                    styled = true;

                    hidden |= _styles.IsParagraphStyleHidden(_reader.GetAttribute("val", _reader.NamespaceURI));
                }

                _xml.SkipElement();
            }
        }

        return styled ? hidden : _styles.IsParagraphStyleHidden(null);
    }

    /// <summary>
    /// Walks a <c>w:txbxContent</c>. The text box stands apart from the text around it, on a line of its own on each side;
    /// a box that projects nothing adds no line. A box is a story of its own (see <see cref="WalkStory"/>), and one anchored
    /// in a field's instruction is part of that instruction: it is not shown, and nothing in it is read.
    /// </summary>
    private void WalkTextBoxContent()
    {
        if (_fieldCodeLevels != 0)
        {
            _xml.SkipElement();

            return;
        }

        WordprocessingOutput.BlockMark mark = _output.BeginSeparatedBlock();

        WalkStory();

        _output.EndSeparatedBlock(mark);
    }

    /// <summary>
    /// Walks the content of a story that stands apart from the text around it: a text box or one note. Word keeps each story's
    /// complex fields apart (a field's begin, separate and end sit in one story), so the story starts with no field open and
    /// the fields of the story it interrupts are set aside and restored once it ends. A field character in a story can then
    /// neither end nor open a field in another, and a field left open in one never turns another story's text into code.
    /// </summary>
    private void WalkStory()
    {
        int fieldDepth = _fieldDepth;

        ulong fieldCodeLevels = _fieldCodeLevels;

        _fieldDepth = 0;

        _fieldCodeLevels = 0;

        WalkChildren();

        _fieldDepth = fieldDepth;

        _fieldCodeLevels = fieldCodeLevels;
    }

    private void WalkRun()
    {
        if (!WordprocessingXml.EnterElement(_xml))
        {
            return;
        }

        bool hidden = IsRunHidden(new WordprocessingRunFormatting(null, _styles.IsCharacterStyleHidden(null)));

        int index = 0;

        WordprocessingChildren children = new(_xml);

        while (!Stopped && children.MoveNext())
        {
            if (IsWordprocessingElement("rPr"))
            {
                if (index != 0)
                {
                    throw new DocumentPackageRejectedException(DocumentPackageRejection.FormatMismatch);
                }

                hidden = IsRunHidden(WordprocessingXml.ReadRunProperties(_xml, _styles));
            }
            else if (WordprocessingXml.IsMath(_reader.NamespaceURI) && _reader.LocalName == "rPr")
            {
                // A math run's own properties (m:rPr) come first, ahead of the Word run properties. They format; they hide nothing.
                _xml.SkipElement();

                continue;
            }
            else
            {
                HandleRunChild(hidden);
            }

            index++;
        }
    }

    /// <summary>An explicit <c>vanish</c> on the run wins; otherwise any hiding style level (or the defaults) hides it.</summary>
    private bool IsRunHidden(WordprocessingRunFormatting formatting) =>
        formatting.Direct ?? (formatting.StyleHidden || _paragraphHidden || _tableHidden || _styles.DocumentDefaultsHidden);

    private void HandleRunChild(bool hidden)
    {
        string namespaceUri = _reader.NamespaceURI;

        if (WordprocessingXml.IsWordprocessing(namespaceUri))
        {
            string localName = _reader.LocalName;

            if (localName == "fldChar")
            {
                // Field structure is tracked even in a hidden run, or the field code it delimits would surface.
                ApplyFieldCharacter();

                return;
            }

            if (!hidden)
            {
                switch (localName)
                {
                    case "t":
                        WalkText();

                        return;

                    case "tab":
                        EmitAndSkip("\t");

                        return;

                    case "br" or "cr":
                        EmitAndSkip("\n");

                        return;

                    case "noBreakHyphen":
                        EmitAndSkip("-");

                        return;

                    case "footnoteReference":
                        RecordNoteReference(endnote: false);

                        return;

                    case "endnoteReference":
                        RecordNoteReference(endnote: true);

                        return;
                }
            }
        }
        else if (_mathDepth > 0 && WordprocessingXml.IsMath(namespaceUri) && _reader.LocalName == "t")
        {
            if (hidden)
            {
                _xml.SkipElement();
            }
            else
            {
                WalkText();
            }

            return;
        }

        if (hidden)
        {
            _xml.SkipElement();

            return;
        }

        HandleElement();
    }

    /// <summary>
    /// Notes a footnote or endnote reference mark that is shown (the caller has ruled out a hidden run), and consumes it.
    /// The mark itself projects nothing. A reference in field code is not shown, and one without a numeric id reaches no note.
    /// </summary>
    private void RecordNoteReference(bool endnote)
    {
        if (_fieldCodeLevels == 0 && TryReadId(out int id))
        {
            if (_noteReferences.Count >= MaximumNoteReferences)
            {
                throw new DocumentPackageRejectedException(DocumentPackageRejection.StructureBounds);
            }

            _noteReferences.Add(((endnote ? 1L : 0L) << 32) | (uint)id);
        }

        _xml.SkipElement();
    }

    /// <summary>
    /// Tracks complex fields. Everything between a field's begin and its separate (or its end, when it has no result) is
    /// field code, whatever element carries it, so text there is never emitted.
    /// </summary>
    private void ApplyFieldCharacter()
    {
        switch (_reader.GetAttribute("fldCharType", _reader.NamespaceURI))
        {
            case "begin":
                if (_fieldDepth == MaximumFieldDepth)
                {
                    throw new DocumentPackageRejectedException(DocumentPackageRejection.StructureBounds);
                }

                _fieldCodeLevels |= 1UL << _fieldDepth;

                _fieldDepth++;

                break;

            case "separate":
                if (_fieldDepth > 0)
                {
                    _fieldCodeLevels &= ~(1UL << (_fieldDepth - 1));
                }

                break;

            case "end":
                if (_fieldDepth > 0)
                {
                    _fieldCodeLevels &= ~(1UL << (_fieldDepth - 1));

                    _fieldDepth--;
                }

                break;
        }

        _xml.SkipElement();
    }

    private void WalkText()
    {
        if (!WordprocessingXml.EnterElement(_xml))
        {
            return;
        }

        while (true)
        {
            switch (_reader.NodeType)
            {
                case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace:
                    Emit(_reader.Value);

                    if (Stopped)
                    {
                        return;
                    }

                    _xml.Read();

                    break;

                case XmlNodeType.Element:
                    _xml.SkipElement();

                    break;

                case XmlNodeType.EndElement:
                    _xml.Read();

                    return;

                case XmlNodeType.None:
                    return;

                default:
                    _xml.Read();

                    break;
            }
        }
    }

    /// <summary>
    /// Walks a <c>wp:inline</c> or <c>wp:anchor</c>. A drawing is hidden by its anchor's own <c>hidden</c> attribute (an
    /// inline has none) or by <c>wp:docPr</c>, which precedes the graphic in the schema but is not required to by this
    /// walk: the drawing's text is held until the drawing ends, and dropped (with everything it changed) if any flag hid it.
    /// Layout and extension children carry no text and are skipped like any other element that is not content.
    /// </summary>
    private void WalkDrawingObject()
    {
        bool hidden = _reader.LocalName == "anchor" && WordprocessingXml.IsTruthyFlag(_reader.GetAttribute("hidden"));

        if (hidden)
        {
            _xml.SkipElement();

            return;
        }

        if (!WordprocessingXml.EnterElement(_xml))
        {
            return;
        }

        int references = _noteReferences.Count;

        int fieldDepth = _fieldDepth;

        ulong fieldCodeLevels = _fieldCodeLevels;

        _output.BeginHold();

        WordprocessingChildren children = new(_xml);

        while (!Stopped && children.MoveNext())
        {
            if (WordprocessingXml.IsWordprocessingDrawing(_reader.NamespaceURI) && _reader.LocalName == "docPr")
            {
                hidden |= WordprocessingXml.IsTruthyFlag(_reader.GetAttribute("hidden"));

                _xml.SkipElement();
            }
            else if (hidden)
            {
                _xml.SkipElement();
            }
            else
            {
                HandleElement();
            }
        }

        _output.EndHold(keep: !hidden);

        if (hidden)
        {
            // What a hidden drawing held never happened: not its references, not the fields it opened or closed.
            _noteReferences.RemoveRange(references, _noteReferences.Count - references);

            _fieldDepth = fieldDepth;

            _fieldCodeLevels = fieldCodeLevels;
        }
    }

    /// <summary>
    /// Walks a shape or group inside a drawing. When it has a <c>cNvPr</c> that is hidden, that element is its first child
    /// (as in the schema) and everything after it is skipped; one that arrives later would be read after text may have
    /// been emitted, which the format does not allow.
    /// </summary>
    private void WalkDrawingShape()
    {
        string containerNamespace = _reader.NamespaceURI;

        if (!WordprocessingXml.EnterElement(_xml))
        {
            return;
        }

        bool hidden = false;

        int index = 0;

        WordprocessingChildren children = new(_xml);

        while (!Stopped && children.MoveNext())
        {
            if (_reader.LocalName == "cNvPr" && _reader.NamespaceURI == containerNamespace)
            {
                if (index != 0)
                {
                    throw new DocumentPackageRejectedException(DocumentPackageRejection.FormatMismatch);
                }

                hidden = WordprocessingXml.IsTruthyFlag(_reader.GetAttribute("hidden"));

                _xml.SkipElement();
            }
            else if (hidden)
            {
                _xml.SkipElement();
            }
            else
            {
                HandleElement();
            }

            index++;
        }
    }

    // ---------------------------------------------------------------- Office Math

    /// <summary>
    /// Walks one element of an equation (or an <c>m:oMath</c> / <c>m:oMathPara</c> itself). Equations project as the
    /// linear text of their <c>m:t</c> elements; property elements (anything named <c>*Pr</c>) format and hide nothing and
    /// are skipped, and the few properties that do hide part of an equation are honoured (see <see cref="WalkMathHiding"/>).
    /// </summary>
    private void WalkMathElement()
    {
        string localName = _reader.LocalName;

        switch (localName)
        {
            case "oMathPara":
                WalkDisplayMath();

                return;

            case "oMath":
                WalkEquation();

                return;

            case "r":
                WalkRun();

                return;

            case "rad" or "nary" or "phant":
                WalkMathHiding(localName);

                return;
        }

        if (localName.EndsWith("Pr", StringComparison.Ordinal))
        {
            _xml.SkipElement();

            return;
        }

        WalkChildren();
    }

    /// <summary>An <c>m:oMathPara</c>: a display equation, its own paragraph. Each equation in it stands on a line of its own (see <see cref="WalkEquation"/>).</summary>
    private void WalkDisplayMath()
    {
        _displayMathDepth++;

        WalkChildren();

        _displayMathDepth--;
    }

    /// <summary>An <c>m:oMath</c>: inline text, or, inside a display, one equation on a line of its own.</summary>
    private void WalkEquation()
    {
        bool display = _displayMathDepth > 0;

        WordprocessingOutput.BlockMark mark = display ? _output.BeginSeparatedBlock() : default;

        _mathDepth++;

        WalkChildren();

        _mathDepth--;

        if (display)
        {
            _output.EndSeparatedBlock(mark);
        }
    }

    /// <summary>
    /// Walks a radical, an n-ary operator or a phantom, whose properties can hide part of it: a radical's
    /// <c>m:degHide</c> its degree, an operator's <c>m:subHide</c> / <c>m:supHide</c> its lower and upper limits, and a
    /// phantom's <c>m:show</c> set off all of it. The properties come first, as in the schema; a package that places them
    /// later, or twice, is refused rather than emitted before the flag is known.
    /// </summary>
    private void WalkMathHiding(string name)
    {
        if (!WordprocessingXml.EnterElement(_xml))
        {
            return;
        }

        string propertiesName = name + "Pr";

        string firstPart = name == "rad" ? "deg" : "sub";

        bool hideFirst = false;

        bool hideSecond = false;

        bool hideAll = false;

        bool propertiesSeen = false;

        bool contentSeen = false;

        WordprocessingChildren children = new(_xml);

        while (!Stopped && children.MoveNext())
        {
            bool isMath = WordprocessingXml.IsMath(_reader.NamespaceURI);

            if (isMath && _reader.LocalName == propertiesName)
            {
                if (propertiesSeen || contentSeen)
                {
                    throw new DocumentPackageRejectedException(DocumentPackageRejection.FormatMismatch);
                }

                propertiesSeen = true;

                ReadMathHidingProperties(name, ref hideFirst, ref hideSecond, ref hideAll);

                continue;
            }

            contentSeen = true;

            bool hiddenPart = isMath && ((hideFirst && _reader.LocalName == firstPart) || (hideSecond && _reader.LocalName == "sup"));

            if (hideAll || hiddenPart)
            {
                _xml.SkipElement();
            }
            else
            {
                HandleElement();
            }
        }
    }

    private void ReadMathHidingProperties(string name, ref bool hideFirst, ref bool hideSecond, ref bool hideAll)
    {
        if (!WordprocessingXml.EnterElement(_xml))
        {
            return;
        }

        WordprocessingChildren children = new(_xml);

        while (children.MoveNext())
        {
            if (WordprocessingXml.IsMath(_reader.NamespaceURI))
            {
                bool on = WordprocessingXml.IsOn(_reader.GetAttribute("val", _reader.NamespaceURI));

                switch (name, _reader.LocalName)
                {
                    case ("rad", "degHide") or ("nary", "subHide"):
                        hideFirst |= on;

                        break;

                    case ("nary", "supHide"):
                        hideSecond |= on;

                        break;

                    case ("phant", "show"):
                        hideAll |= !on;

                        break;
                }
            }

            _xml.SkipElement();
        }
    }

    // ---------------------------------------------------------------- output

    /// <summary>Emits a character a run element stands for, then consumes the element unless the sink has stopped.</summary>
    private void EmitAndSkip(string text)
    {
        Emit(text);

        if (!Stopped)
        {
            _xml.SkipElement();
        }
    }

    private void Emit(ReadOnlySpan<char> text)
    {
        if (Stopped || text.IsEmpty || _fieldCodeLevels != 0)
        {
            return;
        }

        _output.Append(text);
    }

    /// <summary>A paragraph mark is code like any other character while a field's instruction is open.</summary>
    private void EndParagraphMark()
    {
        if (!Stopped && _fieldCodeLevels == 0)
        {
            _output.EndParagraph();
        }
    }

    private void EndRowMark()
    {
        if (!Stopped && _fieldCodeLevels == 0)
        {
            _output.EndRow();
        }
    }
}
