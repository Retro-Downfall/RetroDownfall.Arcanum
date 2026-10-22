using System.Xml;

namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

/// <summary>
/// The hiding verdict of a Word styles part: which character, paragraph and table styles hide their text (through their
/// own <c>w:rPr</c>, a <c>basedOn</c> chain or, for a table style, any conditional format) and whether the document
/// defaults do.
/// </summary>
/// <remarks>
/// <para>
/// A style's verdict is the nearest <c>vanish</c> or <c>specVanish</c> declaration along its <c>basedOn</c> chain, so a
/// derived style can switch a hidden base back on. Only <c>w:style</c> children that are <c>w:rPr</c> count: property
/// containers such as <c>w:pPr</c> (whose <c>w:rPr</c> formats the paragraph mark) are skipped. Which cells a table
/// style's conditional formats (<c>w:tblStylePr</c>) reach is not modelled, so a conditional <c>vanish</c> anywhere in the
/// chain hides the whole table. Numbering styles are not read.
/// </para>
/// <para>
/// Retention is bounded: at most <see cref="MaximumStyles"/> styles of all kinds and <see cref="MaximumRetainedCharacters"/>
/// characters of identifiers, and every chain is walked to its end under <see cref="MaximumChainHops"/>, so a cycle or an
/// overlong chain is refused whether or not the document uses it. A repeated style identifier is refused too, because
/// which definition wins is not specified. Table styles have their own identifier space, as in Word.
/// </para>
/// </remarks>
internal sealed class WordprocessingStyleSheet
{
    internal const int MaximumStyles = 8192;

    internal const int MaximumRetainedCharacters = 262_144;

    internal const int MaximumChainHops = 32;

    private readonly Dictionary<string, StyleDefinition> _styles;

    private readonly Dictionary<string, StyleDefinition> _tableStyles;

    private readonly bool _defaultParagraphHidden;

    private readonly bool _defaultCharacterHidden;

    private readonly bool _defaultTableHidden;

    private WordprocessingStyleSheet(
        Dictionary<string, StyleDefinition> styles,
        Dictionary<string, StyleDefinition> tableStyles,
        bool documentDefaultsHidden,
        bool defaultParagraphHidden,
        bool defaultCharacterHidden,
        bool defaultTableHidden)
    {
        _styles = styles;

        _tableStyles = tableStyles;

        DocumentDefaultsHidden = documentDefaultsHidden;

        _defaultParagraphHidden = defaultParagraphHidden;

        _defaultCharacterHidden = defaultCharacterHidden;

        _defaultTableHidden = defaultTableHidden;
    }

    /// <summary>The sheet of a document with no styles part: nothing is hidden by style.</summary>
    public static WordprocessingStyleSheet Empty { get; } = new([], [], false, false, false, false);

    /// <summary>True when <c>w:docDefaults/w:rPrDefault/w:rPr</c> hides text.</summary>
    public bool DocumentDefaultsHidden { get; }

    /// <summary>
    /// Whether a paragraph style hides its text. A null or undefined identifier means the default paragraph style, as in
    /// Word.
    /// </summary>
    public bool IsParagraphStyleHidden(string? styleId) =>
        styleId is not null && _styles.TryGetValue(styleId, out StyleDefinition? style) ? style.Hidden : _defaultParagraphHidden;

    /// <summary>
    /// Whether a character style hides its text. A null or undefined identifier means the default character style, as in
    /// Word.
    /// </summary>
    public bool IsCharacterStyleHidden(string? styleId) =>
        styleId is not null && _styles.TryGetValue(styleId, out StyleDefinition? style) ? style.Hidden : _defaultCharacterHidden;

    /// <summary>
    /// Whether a table style hides the text of its table. A null or undefined identifier means the default table style, as
    /// in Word.
    /// </summary>
    public bool IsTableStyleHidden(string? styleId) =>
        styleId is not null && _tableStyles.TryGetValue(styleId, out StyleDefinition? style) ? style.Hidden : _defaultTableHidden;

    /// <summary>Reads a styles part. The part's root element must be <c>w:styles</c>.</summary>
    public static WordprocessingStyleSheet Read(ReadOnlyMemory<byte> part, CancellationToken cancellationToken)
    {
        using BoundedXmlReader xml = BoundedXmlReader.Create(part, cancellationToken);

        WordprocessingXml.MoveToRoot(xml, "styles");

        XmlReader reader = xml.Reader;

        Dictionary<string, StyleDefinition> styles = new(StringComparer.Ordinal);

        Dictionary<string, StyleDefinition> tableStyles = new(StringComparer.Ordinal);

        bool documentDefaultsHidden = false;

        int retainedCharacters = 0;

        if (WordprocessingXml.EnterElement(xml))
        {
            while (WordprocessingXml.NextChildElement(xml))
            {
                if (WordprocessingXml.IsWordprocessing(reader.NamespaceURI))
                {
                    if (reader.LocalName == "docDefaults")
                    {
                        documentDefaultsHidden |= ReadDocumentDefaults(xml);

                        continue;
                    }

                    if (reader.LocalName == "style")
                    {
                        ReadStyle(xml, styles, tableStyles, ref retainedCharacters);

                        continue;
                    }
                }

                xml.SkipElement();
            }
        }

        bool defaultParagraphHidden = false;

        bool defaultCharacterHidden = false;

        foreach (StyleDefinition style in styles.Values)
        {
            Resolve(style, styles);

            defaultParagraphHidden |= style.IsDefaultParagraph && style.Hidden;

            defaultCharacterHidden |= style.IsDefaultCharacter && style.Hidden;
        }

        bool defaultTableHidden = false;

        foreach (StyleDefinition style in tableStyles.Values)
        {
            Resolve(style, tableStyles);

            defaultTableHidden |= style.IsDefaultTable && style.Hidden;
        }

        return new WordprocessingStyleSheet(styles, tableStyles, documentDefaultsHidden, defaultParagraphHidden, defaultCharacterHidden, defaultTableHidden);
    }

    private static bool ReadDocumentDefaults(BoundedXmlReader xml)
    {
        XmlReader reader = xml.Reader;

        bool hidden = false;

        if (WordprocessingXml.EnterElement(xml))
        {
            while (WordprocessingXml.NextChildElement(xml))
            {
                if (WordprocessingXml.IsWordprocessing(reader.NamespaceURI) && reader.LocalName == "rPrDefault")
                {
                    hidden |= ReadRunPropertiesIn(xml);

                    continue;
                }

                xml.SkipElement();
            }
        }

        return hidden;
    }

    /// <summary>Reads a container (<c>w:rPrDefault</c> or <c>w:tblStylePr</c>) and returns whether its <c>w:rPr</c> hides.</summary>
    private static bool ReadRunPropertiesIn(BoundedXmlReader xml)
    {
        XmlReader reader = xml.Reader;

        bool hidden = false;

        if (WordprocessingXml.EnterElement(xml))
        {
            while (WordprocessingXml.NextChildElement(xml))
            {
                if (WordprocessingXml.IsWordprocessing(reader.NamespaceURI) && reader.LocalName == "rPr")
                {
                    hidden |= WordprocessingXml.ReadRunProperties(xml, Empty).Direct == true;

                    continue;
                }

                xml.SkipElement();
            }
        }

        return hidden;
    }

    private static void ReadStyle(
        BoundedXmlReader xml,
        Dictionary<string, StyleDefinition> styles,
        Dictionary<string, StyleDefinition> tableStyles,
        ref int retainedCharacters)
    {
        XmlReader reader = xml.Reader;

        string namespaceUri = reader.NamespaceURI;

        string? type = reader.GetAttribute("type", namespaceUri);

        string? id = reader.GetAttribute("styleId", namespaceUri);

        bool isDefault = reader.GetAttribute("default", namespaceUri) is "1" or "true" or "on";

        bool isParagraph = type is null or "paragraph";

        bool isCharacter = type == "character";

        bool isTable = type == "table";

        if (id is null || !(isParagraph || isCharacter || isTable))
        {
            xml.SkipElement();

            return;
        }

        string? basedOn = null;

        bool? vanish = null;

        bool conditionalHidden = false;

        if (WordprocessingXml.EnterElement(xml))
        {
            while (WordprocessingXml.NextChildElement(xml))
            {
                if (WordprocessingXml.IsWordprocessing(reader.NamespaceURI))
                {
                    if (reader.LocalName == "basedOn")
                    {
                        if (basedOn is not null)
                        {
                            throw new DocumentPackageRejectedException(DocumentPackageRejection.FormatMismatch);
                        }

                        basedOn = reader.GetAttribute("val", reader.NamespaceURI) ?? string.Empty;

                        xml.SkipElement();

                        continue;
                    }

                    if (reader.LocalName == "rPr")
                    {
                        bool? declared = WordprocessingXml.ReadRunProperties(xml, Empty).Direct;

                        vanish = declared == true || vanish == true ? true : declared ?? vanish;

                        continue;
                    }

                    if (isTable && reader.LocalName == "tblStylePr")
                    {
                        conditionalHidden |= ReadRunPropertiesIn(xml);

                        continue;
                    }
                }

                xml.SkipElement();
            }
        }

        Dictionary<string, StyleDefinition> kind = isTable ? tableStyles : styles;

        if (kind.ContainsKey(id))
        {
            throw new DocumentPackageRejectedException(DocumentPackageRejection.FormatMismatch);
        }

        retainedCharacters += id.Length + (basedOn?.Length ?? 0);

        if (styles.Count + tableStyles.Count >= MaximumStyles || retainedCharacters > MaximumRetainedCharacters)
        {
            throw new DocumentPackageRejectedException(DocumentPackageRejection.StructureBounds);
        }

        kind.Add(id, new StyleDefinition(basedOn, vanish, conditionalHidden, isParagraph && isDefault, isCharacter && isDefault, isTable && isDefault));
    }

    /// <summary>
    /// Follows the <c>basedOn</c> chain: the verdict is the nearest vanish declaration, and a conditional format that hides
    /// anywhere in the chain counts as well. The whole chain is always walked, so refusing a cycle or an overlong chain
    /// never depends on where a declaration sits.
    /// </summary>
    private static void Resolve(StyleDefinition style, Dictionary<string, StyleDefinition> styles)
    {
        bool? declared = null;

        bool conditionalHidden = false;

        StyleDefinition current = style;

        int hops = 0;

        while (true)
        {
            declared ??= current.Vanish;

            conditionalHidden |= current.OwnConditionalHidden;

            if (current.BasedOn is null || !styles.TryGetValue(current.BasedOn, out StyleDefinition? parent))
            {
                break;
            }

            if (++hops > MaximumChainHops)
            {
                throw new DocumentPackageRejectedException(DocumentPackageRejection.StructureBounds);
            }

            current = parent;
        }

        style.Hidden = declared == true || conditionalHidden;
    }

    private sealed class StyleDefinition(
        string? basedOn,
        bool? vanish,
        bool ownConditionalHidden,
        bool isDefaultParagraph,
        bool isDefaultCharacter,
        bool isDefaultTable)
    {
        public string? BasedOn { get; } = basedOn;

        public bool? Vanish { get; } = vanish;

        public bool OwnConditionalHidden { get; } = ownConditionalHidden;

        public bool IsDefaultParagraph { get; } = isDefaultParagraph;

        public bool IsDefaultCharacter { get; } = isDefaultCharacter;

        public bool IsDefaultTable { get; } = isDefaultTable;

        /// <summary>The verdict after following the <c>basedOn</c> chain.</summary>
        public bool Hidden { get; set; }
    }
}
