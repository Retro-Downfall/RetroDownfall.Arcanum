using System.Xml;

namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

/// <summary>
/// Iterates the child elements of the container the reader has just entered, with markup compatibility
/// (<c>mc:AlternateContent</c>) already resolved: the children of the selected branch are yielded as if they were the
/// container's own children, wherever the container is (a workbook, <c>sheets</c>, <c>bookViews</c>, a worksheet,
/// <c>cols</c>, <c>mergeCells</c>, <c>sheetData</c>, a row, a cell, a rich text item, the shared string table).
/// </summary>
/// <remarks>
/// <para>
/// Markup compatibility is a preprocessing layer, so a consumer must never see an <c>mc:AlternateContent</c>. A hidden
/// column, a merged range, a hidden window, a hidden row or a sheet state declared inside one counts exactly as it does
/// outside, or the cells it hides would be projected. The branch is selected as the specification describes: the first
/// <c>mc:Choice</c> whose <c>Requires</c> prefixes all resolve to namespaces this extractor understands
/// (<see cref="IsUnderstood"/>), otherwise the <c>mc:Fallback</c>; never both, and never neither while a fallback exists.
/// A prefix that is unbound, an empty or over-long list, or a missing attribute means the choice cannot be taken.
/// </para>
/// <para>
/// The fallback is the last child. A choice after a fallback, or a second fallback, leaves it open which branch a reader
/// sees (the fallback may already have been read), so the part is refused with <see cref="DocumentPackageRejection.FormatMismatch"/>.
/// </para>
/// <para>
/// This is a struct holding a lazily created stack; keep it in a local and call <see cref="MoveNext"/> on that local. As
/// with any forward-only walk, the caller must consume each yielded element (read it or skip it) before the next call;
/// descendants it leaves behind are passed over.
/// </para>
/// </remarks>
internal struct SpreadsheetChildren
{
    /// <summary>An <c>mc:Choice</c> that requires more prefixes than this is treated as one that cannot be taken.</summary>
    internal const int MaximumRequiredPrefixes = 16;

    internal const string MarkupCompatibilityNamespace = "http://schemas.openxmlformats.org/markup-compatibility/2006";

    /// <summary>
    /// The namespaces this extractor understands, which is what decides an <c>mc:Choice</c> (a choice is taken only when every
    /// namespace it requires is understood; otherwise the fallback is). Understanding a namespace means that what a choice
    /// written against it holds is either read here (SpreadsheetML itself, transitional or strict) or is extension markup
    /// that carries no cell text and is skipped without losing anything the fallback would have shown: the Excel 2007 to
    /// 2013 extension namespaces (<c>x12ac</c>, <c>x14</c>, <c>x14ac</c>, <c>x15</c>, <c>x15ac</c>). Revision namespaces (<c>xr</c>)
    /// are not here: they only ever appear in <c>mc:Ignorable</c>, never in a <c>Requires</c>.
    /// </summary>
    private static readonly HashSet<string> NamespacesUnderstood = new(StringComparer.Ordinal)
    {
        SpreadsheetXml.TransitionalMain,

        SpreadsheetXml.StrictMain,

        "http://schemas.microsoft.com/office/spreadsheetml/2009/9/main",

        "http://schemas.microsoft.com/office/spreadsheetml/2009/9/ac",

        "http://schemas.microsoft.com/office/spreadsheetml/2010/11/main",

        "http://schemas.microsoft.com/office/spreadsheetml/2010/11/ac",

        "http://schemas.microsoft.com/office/spreadsheetml/2011/1/ac",
    };

    private readonly BoundedXmlReader? _xml;

    private readonly int _depth;

    private List<AlternateContent>? _open;

    private SpreadsheetChildren(BoundedXmlReader xml, int depth)
    {
        _xml = xml;

        _depth = depth;
    }

    /// <summary>True when this extractor understands the namespace, so an <c>mc:Choice</c> that requires it can be taken.</summary>
    internal static bool IsUnderstood(string namespaceUri) => NamespacesUnderstood.Contains(namespaceUri);

    /// <summary>
    /// With the reader on an element start, steps past its start tag. Returns true when the element has content to iterate
    /// with <see cref="MoveNext"/>; false for an empty element, which is consumed.
    /// </summary>
    public static bool Enter(BoundedXmlReader xml, out SpreadsheetChildren children)
    {
        int depth = xml.Reader.Depth;

        bool hasChildren = !xml.Reader.IsEmptyElement;

        xml.Read();

        children = hasChildren ? new SpreadsheetChildren(xml, depth) : default;

        return hasChildren;
    }

    /// <summary>
    /// Moves to the next child element and returns true, leaving the reader on it. Returns false once the container's end
    /// tag has been consumed.
    /// </summary>
    public bool MoveNext()
    {
        if (_xml is null)
        {
            return false;
        }

        XmlReader reader = _xml.Reader;

        while (true)
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                {
                    if (reader.Depth > SlotDepth)
                    {
                        // A descendant of an element the caller left unread.
                        _xml.Read();

                        break;
                    }

                    AlternateContent? alternate = Innermost;

                    if (alternate is null || alternate.InBranch)
                    {
                        if (IsMarkupCompatibility(reader, "AlternateContent"))
                        {
                            bool empty = reader.IsEmptyElement;

                            _xml.Read();

                            if (!empty)
                            {
                                (_open ??= []).Add(new AlternateContent());
                            }

                            break;
                        }

                        return true;
                    }

                    SelectBranch(alternate);

                    break;
                }

                case XmlNodeType.EndElement:
                {
                    int depth = reader.Depth;

                    if (depth == _depth)
                    {
                        _xml.Read();

                        return false;
                    }

                    if (_open is { Count: > 0 } open)
                    {
                        AlternateContent alternate = open[^1];

                        if (alternate.InBranch && depth == _depth + (2 * open.Count))
                        {
                            alternate.InBranch = false;
                        }
                        else if (!alternate.InBranch && depth == _depth + (2 * open.Count) - 1)
                        {
                            open.RemoveAt(open.Count - 1);
                        }
                    }

                    _xml.Read();

                    break;
                }

                case XmlNodeType.None:
                    return false;

                default:
                    _xml.Read();

                    break;
            }
        }
    }

    /// <summary>
    /// The depth at which the next relevant element sits: a child of the container, or of the selected branch of the
    /// innermost open <c>mc:AlternateContent</c>, or a <c>mc:Choice</c> or <c>mc:Fallback</c> of it while none is entered.
    /// </summary>
    private readonly int SlotDepth =>
        _open is { Count: > 0 } open
            ? (open[^1].InBranch ? _depth + 1 + (2 * open.Count) : _depth + (2 * open.Count))
            : _depth + 1;

    private readonly AlternateContent? Innermost => _open is { Count: > 0 } open ? open[^1] : null;

    private static bool IsMarkupCompatibility(XmlReader reader, string localName) =>
        reader.LocalName == localName && reader.NamespaceURI == MarkupCompatibilityNamespace;

    /// <summary>The reader is on a direct child of an open <c>mc:AlternateContent</c>: enter it if it is the branch to take, else skip it.</summary>
    private readonly void SelectBranch(AlternateContent alternate)
    {
        XmlReader reader = _xml!.Reader;

        if (IsMarkupCompatibility(reader, "Choice"))
        {
            if (alternate.FallbackSeen)
            {
                throw new DocumentPackageRejectedException(DocumentPackageRejection.FormatMismatch);
            }

            if (!alternate.Selected && RequiresOnlyUnderstoodNamespaces(reader))
            {
                EnterBranch(alternate);

                return;
            }
        }
        else if (IsMarkupCompatibility(reader, "Fallback"))
        {
            if (alternate.FallbackSeen)
            {
                throw new DocumentPackageRejectedException(DocumentPackageRejection.FormatMismatch);
            }

            alternate.FallbackSeen = true;

            if (!alternate.Selected)
            {
                EnterBranch(alternate);

                return;
            }
        }

        _xml.SkipElement();
    }

    private readonly void EnterBranch(AlternateContent alternate)
    {
        alternate.Selected = true;

        bool empty = _xml!.Reader.IsEmptyElement;

        _xml.Read();

        alternate.InBranch = !empty;
    }

    /// <summary>
    /// Evaluates <c>mc:Choice/@Requires</c>: a whitespace-separated list of prefixes, each resolved through the namespace
    /// declarations in scope. A missing, empty or over-long list, an unbound prefix, or any namespace this extractor does
    /// not understand means the choice cannot be taken.
    /// </summary>
    private static bool RequiresOnlyUnderstoodNamespaces(XmlReader reader)
    {
        ReadOnlySpan<char> rest = reader.GetAttribute("Requires");

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

            string? namespaceUri = reader.LookupNamespace(prefix.ToString());

            if (namespaceUri is null || !IsUnderstood(namespaceUri))
            {
                return false;
            }
        }
    }

    private sealed class AlternateContent
    {
        /// <summary>True once a branch (a choice, or the fallback) has been entered.</summary>
        public bool Selected { get; set; }

        /// <summary>True while the reader is inside the entered branch.</summary>
        public bool InBranch { get; set; }

        public bool FallbackSeen { get; set; }
    }
}
