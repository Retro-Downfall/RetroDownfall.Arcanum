using System.Xml;

namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

/// <summary>
/// The hiding-relevant slice of an OpenDocument package's styles: for each named style only its parent and the four
/// display switches that can hide content. Common styles come from <c>office:styles</c> and automatic styles from
/// <c>office:automatic-styles</c>; an automatic style shadows a common one of the same family and name.
/// </summary>
/// <remarks>
/// <para>
/// A style is judged by walking its parent chain (at most <see cref="MaximumParentHops"/> hops, so a cycle or an
/// over-deep chain is a <see cref="DocumentPackageRejection.StructureBounds"/> refusal) to the first style that
/// states the switch, then to the family's <c>style:default-style</c>. An explicit "visible" therefore overrides a
/// hidden parent. Each answer is cached on the style, so a reference costs one lookup after its first use.
/// </para>
/// <para>
/// Retained state obeys fixed caps: <see cref="MaximumStyles"/> styles in all and <see cref="MaximumNameLength"/>
/// characters per retained name. When a name is defined twice in one table the definitions merge and "hidden" wins,
/// so a duplicate can never reveal text a consumer that picks either definition would hide.
/// </para>
/// </remarks>
internal sealed class OpenDocumentStyleTable
{
    internal const int MaximumStyles = 65_536;

    internal const int MaximumParentHops = 32;

    internal const int MaximumNameLength = 1024;

    internal const string ParagraphFamily = "paragraph";

    internal const string TextFamily = "text";

    internal const string GraphicFamily = "graphic";

    internal const string PresentationFamily = "presentation";

    internal const string TableFamily = "table";

    internal const string DrawingPageFamily = "drawing-page";

    private const sbyte Unspecified = 0;

    private const sbyte Visible = 1;

    private const sbyte Hidden = 2;

    private const string StandardParagraphStyle = "Standard";

    private readonly Dictionary<(string Family, string Name), StyleEntry> _common = [];

    private readonly Dictionary<(string Family, string Name), StyleEntry> _automatic = [];

    private readonly Dictionary<string, StyleEntry> _defaults = [];

    private int _count;

    /// <summary>The display switches a style can state.</summary>
    internal enum Switch
    {
        /// <summary><c>style:text-properties</c> <c>text:display</c>.</summary>
        Text = 0,

        /// <summary><c>style:graphic-properties</c> <c>draw:display</c>.</summary>
        Graphic = 1,

        /// <summary><c>style:table-properties</c> <c>table:display</c>.</summary>
        Table = 2,

        /// <summary><c>style:drawing-page-properties</c> <c>presentation:visibility</c>.</summary>
        Page = 3,
    }

    /// <summary>
    /// Reads the styles of the <c>office:styles</c> (<paramref name="automatic"/> false) or
    /// <c>office:automatic-styles</c> element the reader is on, then moves past its end.
    /// </summary>
    public void ReadContainer(BoundedXmlReader xml, bool automatic)
    {
        XmlReader reader = xml.Reader;

        if (reader.IsEmptyElement)
        {
            xml.Read();

            return;
        }

        xml.Read();

        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.EndElement)
            {
                xml.Read();

                return;
            }

            if (reader.NodeType != XmlNodeType.Element)
            {
                xml.Read();

                continue;
            }

            if (reader.NamespaceURI == OpenDocumentNames.Style && reader.LocalName == "style")
            {
                ReadStyle(xml, automatic);
            }
            else if (reader.NamespaceURI == OpenDocumentNames.Style && reader.LocalName == "default-style" && !automatic)
            {
                ReadDefaultStyle(xml);
            }
            else
            {
                xml.SkipElement();
            }
        }
    }

    /// <summary>
    /// True when the style <paramref name="name"/> of <paramref name="family"/> hides content for <paramref name="hiding"/>.
    /// A missing or unknown name falls back to the family's default style (and, for paragraphs, to the common
    /// <c>Standard</c> style, which Writer applies to a paragraph that names none).
    /// </summary>
    public bool IsHidden(Switch hiding, string family, string? name)
    {
        if (name is not null && Find(family, name) is { } style)
        {
            return Resolve(style, hiding);
        }

        if (_defaults.TryGetValue(family, out StyleEntry? defaultStyle) && defaultStyle.Display[(int)hiding] == Hidden)
        {
            return true;
        }

        return family == ParagraphFamily
            && hiding == Switch.Text
            && Find(ParagraphFamily, StandardParagraphStyle) is { } standard
            && Resolve(standard, hiding);
    }

    private void ReadStyle(BoundedXmlReader xml, bool automatic)
    {
        XmlReader reader = xml.Reader;

        string? name = reader.GetAttribute("name", OpenDocumentNames.Style);

        string? family = reader.GetAttribute("family", OpenDocumentNames.Style);

        string? parent = reader.GetAttribute("parent-style-name", OpenDocumentNames.Style);

        StyleEntry entry = ReadProperties(xml, family ?? string.Empty, parent);

        if (name is null || family is null)
        {
            return;
        }

        Count();

        CheckLength(name);

        CheckLength(family);

        CheckLength(parent);

        Dictionary<(string Family, string Name), StyleEntry> table = automatic ? _automatic : _common;

        if (table.TryGetValue((family, name), out StyleEntry? existing))
        {
            existing.Merge(entry);

            return;
        }

        table[(family, name)] = entry;
    }

    private void ReadDefaultStyle(BoundedXmlReader xml)
    {
        string? family = xml.Reader.GetAttribute("family", OpenDocumentNames.Style);

        StyleEntry entry = ReadProperties(xml, family ?? string.Empty, null);

        if (family is null)
        {
            return;
        }

        Count();

        CheckLength(family);

        if (_defaults.TryGetValue(family, out StyleEntry? existing))
        {
            existing.Merge(entry);

            return;
        }

        _defaults[family] = entry;
    }

    /// <summary>Reads the property children of the style element the reader is on, then moves past its end.</summary>
    private static StyleEntry ReadProperties(BoundedXmlReader xml, string family, string? parent)
    {
        XmlReader reader = xml.Reader;

        StyleEntry entry = new(family, parent);

        if (reader.IsEmptyElement)
        {
            xml.Read();

            return entry;
        }

        xml.Read();

        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.EndElement)
            {
                xml.Read();

                return entry;
            }

            if (reader.NodeType != XmlNodeType.Element)
            {
                xml.Read();

                continue;
            }

            if (reader.NamespaceURI == OpenDocumentNames.Style)
            {
                switch (reader.LocalName)
                {
                    case "text-properties":
                        entry.State(Switch.Text, reader.GetAttribute("display", OpenDocumentNames.Text), "none", "condition", "true");

                        break;

                    case "graphic-properties":
                        entry.State(Switch.Graphic, reader.GetAttribute("display", OpenDocumentNames.Draw), "none", "printer", "always", "screen");

                        break;

                    case "table-properties":
                        entry.State(Switch.Table, reader.GetAttribute("display", OpenDocumentNames.Table), "false", null, "true");

                        break;

                    case "drawing-page-properties":
                        entry.State(Switch.Page, reader.GetAttribute("visibility", OpenDocumentNames.Presentation), "hidden", null, "visible");

                        break;
                }
            }

            xml.SkipElement();
        }

        return entry;
    }

    private StyleEntry? Find(string family, string name) =>
        _automatic.TryGetValue((family, name), out StyleEntry? automatic)
            ? automatic
            : _common.GetValueOrDefault((family, name));

    private bool Resolve(StyleEntry style, Switch hiding)
    {
        int index = (int)hiding;

        if (style.Resolved[index] != Unspecified)
        {
            return style.Resolved[index] == Hidden;
        }

        bool hidden = Walk(style, hiding);

        style.Resolved[index] = hidden ? Hidden : Visible;

        return hidden;
    }

    private bool Walk(StyleEntry style, Switch hiding)
    {
        StyleEntry? current = style;

        int hops = 0;

        while (current is not null)
        {
            sbyte stated = current.Display[(int)hiding];

            if (stated != Unspecified)
            {
                return stated == Hidden;
            }

            if (current.Parent is null)
            {
                break;
            }

            if (++hops > MaximumParentHops)
            {
                throw new DocumentPackageRejectedException(DocumentPackageRejection.StructureBounds);
            }

            current = Find(style.Family, current.Parent);
        }

        return _defaults.TryGetValue(style.Family, out StyleEntry? defaultStyle) && defaultStyle.Display[(int)hiding] == Hidden;
    }

    private void Count()
    {
        if (++_count > MaximumStyles)
        {
            throw new DocumentPackageRejectedException(DocumentPackageRejection.StructureBounds);
        }
    }

    private static void CheckLength(string? value)
    {
        if (value is { Length: > MaximumNameLength })
        {
            throw new DocumentPackageRejectedException(DocumentPackageRejection.StructureBounds);
        }
    }

    private sealed class StyleEntry(string family, string? parent)
    {
        public string Family { get; } = family;

        public string? Parent { get; private set; } = parent;

        public sbyte[] Display { get; } = new sbyte[4];

        public sbyte[] Resolved { get; } = new sbyte[4];

        /// <summary>Records a switch value: a hidden token marks it hidden, a visible token marks it visible, anything else leaves it unstated.</summary>
        public void State(Switch hiding, string? value, string hiddenToken, string? secondHiddenToken, string visibleToken, string? secondVisibleToken = null)
        {
            if (value is null)
            {
                return;
            }

            if (value == hiddenToken || value == secondHiddenToken)
            {
                Display[(int)hiding] = Hidden;
            }
            else if (value == visibleToken || value == secondVisibleToken)
            {
                Display[(int)hiding] = Visible;
            }
        }

        public void Merge(StyleEntry other)
        {
            Parent ??= other.Parent;

            for (int index = 0; index < Display.Length; index++)
            {
                if (other.Display[index] > Display[index])
                {
                    Display[index] = other.Display[index];
                }
            }
        }
    }
}
