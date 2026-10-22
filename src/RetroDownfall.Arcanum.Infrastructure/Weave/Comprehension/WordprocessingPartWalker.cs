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
/// follows. Text is emitted only from <c>w:t</c> content and a fixed set of run characters, so character data that is
/// not text content (property values, instructions, stray text) can never reach the output.
/// </para>
/// <para>
/// Hiding is decided before a run's content is read: a run's <c>w:rPr</c>, a paragraph's <c>w:pPr</c> and a drawing's
/// <c>wp:docPr</c> must each precede the content they govern, and a package that places them later is refused rather
/// than emitted before its flag is known. A run is hidden by its own <c>vanish</c> (either value wins), otherwise by any
/// of its character style, its paragraph's style, its table's style or the document defaults. Word treats <c>vanish</c> as a toggle
/// property across style levels; that exclusive-or is deliberately not modelled, so this union only ever hides more.
/// </para>
/// </remarks>
internal sealed class WordprocessingPartWalker
{
    /// <summary>Complex fields nest this deep at most; one bit per level tracks whether the level is still field code.</summary>
    internal const int MaximumFieldDepth = 64;

    /// <summary>An <c>mc:Choice</c> that requires more prefixes than this is treated as one Word would not take.</summary>
    internal const int MaximumRequiredPrefixes = 16;

    private readonly BoundedXmlReader _xml;

    private readonly XmlReader _reader;

    private readonly WordprocessingStyleSheet _styles;

    private readonly ISessionAttachmentDocumentTextSink _output;

    private int _fieldDepth;

    private ulong _fieldCodeLevels;

    private bool _paragraphHidden;

    private bool _tableHidden;

    public WordprocessingPartWalker(BoundedXmlReader xml, WordprocessingStyleSheet styles, ISessionAttachmentDocumentTextSink output)
    {
        _xml = xml;

        _reader = xml.Reader;

        _styles = styles;

        _output = output;

        _paragraphHidden = styles.IsParagraphStyleHidden(null);
    }

    /// <summary>True once the sink refused text; the caller must stop.</summary>
    public bool Stopped { get; private set; }

    /// <summary>Walks the part whose root element the reader is positioned on.</summary>
    public void Walk() => WalkChildren();

    /// <summary>
    /// Walks a footnotes or endnotes part whose root element the reader is positioned on. Only <paramref name="noteName"/>
    /// children are read, and separator notes (the rules between the body and its notes) are skipped.
    /// </summary>
    public void WalkNotes(string noteName)
    {
        if (!WordprocessingXml.EnterElement(_xml))
        {
            return;
        }

        while (!Stopped && WordprocessingXml.NextChildElement(_xml))
        {
            if (!IsWordprocessingElement(noteName)
                || _reader.GetAttribute("type", _reader.NamespaceURI) is "separator" or "continuationSeparator" or "continuationNotice")
            {
                _xml.SkipElement();

                continue;
            }

            WalkChildren();
        }
    }

    private void WalkChildren()
    {
        if (!WordprocessingXml.EnterElement(_xml))
        {
            return;
        }

        while (!Stopped && WordprocessingXml.NextChildElement(_xml))
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

                case "del" or "moveFrom" or "delText" or "instrText" or "delInstrText" or "rPr" or "pPr" or "sectPr" or "sdtPr" or "rt":
                    _xml.SkipElement();

                    return;
            }
        }
        else if (namespaceUri == WordprocessingXml.MarkupCompatibilityNamespace)
        {
            // A stray mc:Choice or mc:Fallback, like any other markup-compatibility element, is not a content namespace
            // and is skipped below.
            if (_reader.LocalName == "AlternateContent")
            {
                WalkAlternateContent();

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

            while (!Stopped && WordprocessingXml.NextChildElement(_xml))
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
            while (WordprocessingXml.NextChildElement(_xml))
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

    private void WalkParagraph()
    {
        bool enclosingHidden = _paragraphHidden;

        _paragraphHidden = _styles.IsParagraphStyleHidden(null);

        if (WordprocessingXml.EnterElement(_xml))
        {
            int index = 0;

            while (!Stopped && WordprocessingXml.NextChildElement(_xml))
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

        Emit("\n");
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
            while (WordprocessingXml.NextChildElement(_xml))
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

    private void WalkRun()
    {
        if (!WordprocessingXml.EnterElement(_xml))
        {
            return;
        }

        bool hidden = IsRunHidden(new WordprocessingRunFormatting(null, _styles.IsCharacterStyleHidden(null)));

        int index = 0;

        while (!Stopped && WordprocessingXml.NextChildElement(_xml))
        {
            if (IsWordprocessingElement("rPr"))
            {
                if (index != 0)
                {
                    throw new DocumentPackageRejectedException(DocumentPackageRejection.FormatMismatch);
                }

                hidden = IsRunHidden(WordprocessingXml.ReadRunProperties(_xml, _styles));
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
        if (WordprocessingXml.IsWordprocessing(_reader.NamespaceURI))
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
                }
            }
        }

        if (hidden)
        {
            _xml.SkipElement();

            return;
        }

        HandleElement();
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
    /// Projects an <c>mc:AlternateContent</c>. Only the first <c>mc:Choice</c> is ever a candidate: later choices and the
    /// <c>mc:Fallback</c> repeat the same content. It is projected only when every namespace it requires is one Word
    /// understands; otherwise Word would show the fallback instead, and neither is projected (a guess in either direction
    /// could surface text the reader never sees).
    /// </summary>
    private void WalkAlternateContent()
    {
        if (!WordprocessingXml.EnterElement(_xml))
        {
            return;
        }

        bool decided = false;

        while (!Stopped && WordprocessingXml.NextChildElement(_xml))
        {
            if (!decided
                && _reader.LocalName == "Choice"
                && _reader.NamespaceURI == WordprocessingXml.MarkupCompatibilityNamespace)
            {
                decided = true;

                if (RequiresOnlyNamespacesWordUnderstands())
                {
                    WalkChildren();

                    continue;
                }
            }

            _xml.SkipElement();
        }
    }

    /// <summary>
    /// Evaluates <c>mc:Choice/@Requires</c>: a whitespace-separated list of prefixes, each resolved through the namespace
    /// declarations in scope. A missing, empty or over-long list, an unbound prefix, or any namespace Word does not read
    /// means the choice would not be taken.
    /// </summary>
    private bool RequiresOnlyNamespacesWordUnderstands()
    {
        ReadOnlySpan<char> rest = _reader.GetAttribute("Requires");

        int prefixes = 0;

        while (true)
        {
            rest = rest.TrimStart();

            if (rest.IsEmpty)
            {
                return prefixes > 0;
            }

            int end = rest.IndexOfAny(" \t\r\n");

            ReadOnlySpan<char> prefix = end < 0 ? rest : rest[..end];

            rest = end < 0 ? default : rest[end..];

            if (++prefixes > MaximumRequiredPrefixes)
            {
                return false;
            }

            string? namespaceUri = _reader.LookupNamespace(prefix.ToString());

            if (namespaceUri is null || !WordprocessingXml.IsUnderstoodByWord(namespaceUri))
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Walks a <c>wp:inline</c> or <c>wp:anchor</c>. Its <c>wp:docPr</c> carries the hidden flag and precedes the graphic,
    /// so everything after a hidden <c>docPr</c> is skipped; a graphic that arrives before any <c>docPr</c> would be
    /// emitted before its flag is known, which the format does not allow.
    /// </summary>
    private void WalkDrawingObject()
    {
        if (!WordprocessingXml.EnterElement(_xml))
        {
            return;
        }

        bool docPrSeen = false;

        bool hidden = false;

        while (!Stopped && WordprocessingXml.NextChildElement(_xml))
        {
            if (WordprocessingXml.IsWordprocessingDrawing(_reader.NamespaceURI) && _reader.LocalName == "docPr")
            {
                docPrSeen = true;

                hidden |= WordprocessingXml.IsTruthyFlag(_reader.GetAttribute("hidden"));

                _xml.SkipElement();
            }
            else if (hidden)
            {
                _xml.SkipElement();
            }
            else if (!docPrSeen)
            {
                if (!WordprocessingXml.IsDrawingLayoutElement(_reader.NamespaceURI, _reader.LocalName))
                {
                    throw new DocumentPackageRejectedException(DocumentPackageRejection.FormatMismatch);
                }

                _xml.SkipElement();
            }
            else
            {
                HandleElement();
            }
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

        while (!Stopped && WordprocessingXml.NextChildElement(_xml))
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

        if (!_output.TryAppend(text))
        {
            Stopped = true;
        }
    }
}
