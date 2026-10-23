using System.Xml;

namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

/// <summary>
/// The hiding-relevant slice of an OpenDocument package's styles: for each named style only its parent and the four
/// display switches that can hide content. Common styles come from <c>office:styles</c> and automatic styles from
/// <c>office:automatic-styles</c>. They are two name spaces: an element's own style name is looked up among the
/// automatic styles first and then among the common ones, but <c>style:parent-style-name</c> (which ODF says can
/// never name an automatic style) and the common <c>Standard</c> paragraph style are looked up among the common
/// styles only, so an automatic style that shares a name with its parent neither stands in for it nor is its own parent.
/// </summary>
/// <remarks>
/// <para>
/// A style definition is judged by walking its parent chain through the common styles (at most
/// <see cref="MaximumParentHops"/> hops, so a cycle or an over-deep chain is a
/// <see cref="DocumentPackageRejection.StructureBounds"/> refusal) to the first style that states the switch, then to
/// the family's <c>style:default-style</c>. An explicit "visible" therefore overrides a hidden parent. Each answer is
/// cached on the style, so a reference costs one lookup after its first use.
/// </para>
/// <para>
/// When a name is defined twice in one table every definition is kept and judged on its own, and "hidden" wins: the
/// style is hidden when any definition, or the parent chain of any definition (a parent defined twice being judged the
/// same way), resolves hidden, so a duplicate can never reveal text a consumer that picks either definition would hide.
/// </para>
/// <para>
/// A style may carry <c>style:map</c> children, which make it a conditional style: in the context a condition names, a
/// consumer formats the paragraph (or cell) with the style named by <c>style:apply-style-name</c> instead. The
/// conditions are not evaluated. A style is therefore also hidden when any map target of its own, or of a style in its
/// parent chain, resolves hidden (the target looked up among the automatic and the common styles alike). A target is
/// judged as a plain style, by its own switches and parents: a consumer applies it once, so a map on a target is not
/// followed.
/// </para>
/// <para>
/// Retained state obeys fixed caps: <see cref="MaximumStyles"/> entries in all, a style definition and a map target
/// each being one, and <see cref="MaximumNameLength"/> characters per retained name.
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

    internal const string TableCellFamily = "table-cell";

    internal const string DrawingPageFamily = "drawing-page";

    private const sbyte Unspecified = 0;

    private const sbyte Visible = 1;

    private const sbyte Hidden = 2;

    private const string StandardParagraphStyle = "Standard";

    private readonly Dictionary<(string Family, string Name), StyleEntry> _common = [];

    private readonly Dictionary<(string Family, string Name), StyleEntry> _automatic = [];

    private readonly Dictionary<string, StyleEntry> _defaults = [];

    private readonly HashSet<string> _hiddenLayers = [];

    private int _count;

    private int _maps;

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
            else if (reader.NamespaceURI == OpenDocumentNames.Style && reader.LocalName == "default-style")
            {
                // A default style belongs in office:styles; one found among the automatic styles is still read, so a
                // consumer that honours it can never be shown text this reader would have projected.
                ReadDefaultStyle(xml);
            }
            else
            {
                xml.SkipElement();
            }
        }
    }

    /// <summary>
    /// Reads the layers of the <c>draw:layer-set</c> under the <c>office:master-styles</c> element the reader is on, then
    /// moves past its end. A layer that is not shown on screen (<c>draw:display</c> of <c>none</c> or <c>printer</c>)
    /// hides every shape placed on it.
    /// </summary>
    public void ReadMasterStyles(BoundedXmlReader xml)
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

            if (reader.NodeType == XmlNodeType.Element && reader.NamespaceURI == OpenDocumentNames.Draw && reader.LocalName == "layer-set")
            {
                ReadLayerSet(xml);
            }
            else if (reader.NodeType == XmlNodeType.Element)
            {
                xml.SkipElement();
            }
            else
            {
                xml.Read();
            }
        }
    }

    /// <summary>True when shapes on the layer called <paramref name="name"/> are not shown on screen.</summary>
    public bool IsLayerHidden(string? name) => name is not null && _hiddenLayers.Contains(name);

    /// <summary>
    /// True when the style <paramref name="name"/> of <paramref name="family"/> hides content for <paramref name="hiding"/>.
    /// A missing or unknown name falls back to the family's default style (and, for paragraphs, to the common
    /// <c>Standard</c> style, which Writer applies to a paragraph that names none).
    /// </summary>
    public bool IsHidden(Switch hiding, string family, string? name)
    {
        if (name is not null && Find(family, name) is { } style)
        {
            return IsStyleHidden(style, hiding);
        }

        if (_defaults.TryGetValue(family, out StyleEntry? defaultStyle) && defaultStyle.Display[(int)hiding] == Hidden)
        {
            return true;
        }

        return family == ParagraphFamily
            && hiding == Switch.Text
            && _common.GetValueOrDefault((ParagraphFamily, StandardParagraphStyle)) is { } standard
            && IsStyleHidden(standard, hiding);
    }

    /// <summary>True when the style itself, or (when the table holds conditional styles) any style it maps to, resolves hidden.</summary>
    private bool IsStyleHidden(StyleEntry style, Switch hiding) =>
        Resolve(style, hiding, 0) || (_maps > 0 && ResolveMapTargets(style, hiding, 0));

    private void ReadLayerSet(BoundedXmlReader xml)
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

            if (reader.NamespaceURI == OpenDocumentNames.Draw
                && reader.LocalName == "layer"
                && reader.GetAttribute("name", OpenDocumentNames.Draw) is { } name
                && reader.GetAttribute("display", OpenDocumentNames.Draw) is "none" or "printer")
            {
                Count();

                CheckLength(name);

                _hiddenLayers.Add(name);
            }

            xml.SkipElement();
        }
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
            // Every definition is kept: each is judged on its own and the style is hidden when any of them is.
            existing.AddDuplicate(entry);

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
    private StyleEntry ReadProperties(BoundedXmlReader xml, string family, string? parent)
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

                    case "map" when reader.GetAttribute("apply-style-name", OpenDocumentNames.Style) is { } target:
                        Count();

                        CheckLength(target);

                        _maps++;

                        entry.AddMap(target);

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

    /// <summary>
    /// True when any definition of the style (the head of its list of duplicates) resolves hidden for
    /// <paramref name="hiding"/>. <paramref name="depth"/> is the number of parent hops taken to reach it.
    /// </summary>
    private bool Resolve(StyleEntry style, Switch hiding, int depth)
    {
        int index = (int)hiding;

        if (style.Resolved[index] != Unspecified)
        {
            return style.Resolved[index] == Hidden;
        }

        bool hidden = false;

        for (StyleEntry? definition = style; definition is not null && !hidden; definition = definition.Next)
        {
            hidden = ResolveDefinition(definition, hiding, depth);
        }

        style.Resolved[index] = hidden ? Hidden : Visible;

        return hidden;
    }

    /// <summary>One definition: its own statement of the switch, else its parent's answer, else the family's default style.</summary>
    private bool ResolveDefinition(StyleEntry definition, Switch hiding, int depth)
    {
        sbyte stated = definition.Display[(int)hiding];

        if (stated != Unspecified)
        {
            return stated == Hidden;
        }

        if (definition.Parent is not null)
        {
            CheckDepth(depth);

            if (_common.GetValueOrDefault((definition.Family, definition.Parent)) is { } parent)
            {
                return Resolve(parent, hiding, depth + 1);
            }
        }

        return _defaults.TryGetValue(definition.Family, out StyleEntry? defaultStyle) && defaultStyle.Display[(int)hiding] == Hidden;
    }

    /// <summary>
    /// True when a map target of any definition of the style, or of any style in its parent chain, resolves hidden.
    /// A target is judged as a plain style (<see cref="Resolve"/>), so a map on a target is not followed.
    /// </summary>
    private bool ResolveMapTargets(StyleEntry style, Switch hiding, int depth)
    {
        int index = (int)hiding;

        if (style.MapsResolved[index] != Unspecified)
        {
            return style.MapsResolved[index] == Hidden;
        }

        bool hidden = false;

        for (StyleEntry? definition = style; definition is not null && !hidden; definition = definition.Next)
        {
            hidden = AnyMapTargetHidden(definition, hiding) || ParentMapTargetsHidden(definition, hiding, depth);
        }

        style.MapsResolved[index] = hidden ? Hidden : Visible;

        return hidden;
    }

    private bool AnyMapTargetHidden(StyleEntry definition, Switch hiding)
    {
        if (definition.Maps is null)
        {
            return false;
        }

        foreach (string target in definition.Maps)
        {
            if ((_automatic.GetValueOrDefault((definition.Family, target)) is { } automatic && Resolve(automatic, hiding, 0))
                || (_common.GetValueOrDefault((definition.Family, target)) is { } common && Resolve(common, hiding, 0)))
            {
                return true;
            }
        }

        return false;
    }

    private bool ParentMapTargetsHidden(StyleEntry definition, Switch hiding, int depth)
    {
        if (definition.Parent is null)
        {
            return false;
        }

        CheckDepth(depth);

        return _common.GetValueOrDefault((definition.Family, definition.Parent)) is { } parent
            && ResolveMapTargets(parent, hiding, depth + 1);
    }

    /// <summary>Refuses a parent chain that would need more than <see cref="MaximumParentHops"/> hops, which a cycle always does.</summary>
    private static void CheckDepth(int depth)
    {
        if (depth >= MaximumParentHops)
        {
            throw new DocumentPackageRejectedException(DocumentPackageRejection.StructureBounds);
        }
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

        public string? Parent { get; } = parent;

        public sbyte[] Display { get; } = new sbyte[4];

        /// <summary>The cached answer of <see cref="Resolve"/> for each switch; kept on the first definition of a name.</summary>
        public sbyte[] Resolved { get; } = new sbyte[4];

        /// <summary>The cached answer of <see cref="ResolveMapTargets"/> for each switch; kept on the first definition of a name.</summary>
        public sbyte[] MapsResolved { get; } = new sbyte[4];

        /// <summary>The names this definition's <c>style:map</c> children apply, or null when it has none.</summary>
        public List<string>? Maps { get; private set; }

        /// <summary>The next definition of the same name, or null.</summary>
        public StyleEntry? Next { get; private set; }

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

        public void AddMap(string target) => (Maps ??= []).Add(target);

        /// <summary>Adds another definition of the same name; the order of definitions does not matter.</summary>
        public void AddDuplicate(StyleEntry other)
        {
            other.Next = Next;

            Next = other;
        }

        /// <summary>Folds another default style of the same family into this one: "hidden" wins.</summary>
        public void Merge(StyleEntry other)
        {
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
