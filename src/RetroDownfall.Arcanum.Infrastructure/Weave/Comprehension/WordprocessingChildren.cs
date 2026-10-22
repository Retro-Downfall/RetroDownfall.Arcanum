using System.Xml;

namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

/// <summary>
/// Iterates the child elements of the container the reader has just entered, with markup compatibility
/// (<c>mc:AlternateContent</c>) already resolved: the children of the selected branch are yielded as if they were the
/// container's own children, wherever the container is (a body, a paragraph, a run, a property container, a drawing, a
/// style).
/// </summary>
/// <remarks>
/// <para>
/// Markup compatibility is a preprocessing layer, so a consumer must never see an <c>mc:AlternateContent</c>. The branch is
/// selected as the specification describes: the first <c>mc:Choice</c> whose <c>Requires</c> prefixes all resolve to
/// namespaces this extractor understands (<see cref="WordprocessingXml.IsUnderstood"/>), otherwise the <c>mc:Fallback</c>;
/// never both, and never neither while a fallback exists. A prefix that is unbound, an empty or over-long list, or a
/// missing attribute means the choice cannot be taken.
/// </para>
/// <para>
/// The fallback is the last child. A choice after a fallback, or a second fallback, leaves it open which branch a reader
/// sees (the fallback may already have been walked), so the part is refused with <see cref="DocumentPackageRejection.FormatMismatch"/>.
/// </para>
/// <para>
/// This is a struct holding a lazily created stack; keep it in a local and call <see cref="MoveNext"/> on that local. As
/// with any forward-only walk, the caller must consume each yielded element (read it or skip it) before the next call.
/// </para>
/// </remarks>
internal struct WordprocessingChildren(BoundedXmlReader xml)
{
    /// <summary>An <c>mc:Choice</c> that requires more prefixes than this is treated as one that cannot be taken.</summary>
    internal const int MaximumRequiredPrefixes = 16;

    private List<AlternateContent>? _open;

    /// <summary>
    /// Moves to the next child element and returns true, leaving the reader on it. Returns false once the container's
    /// end tag has been consumed.
    /// </summary>
    public bool MoveNext()
    {
        XmlReader reader = xml.Reader;

        while (true)
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                {
                    AlternateContent? alternate = Innermost;

                    if (alternate is null || alternate.InBranch)
                    {
                        if (IsMarkupCompatibility(reader, "AlternateContent"))
                        {
                            bool empty = reader.IsEmptyElement;

                            xml.Read();

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
                    AlternateContent? alternate = Innermost;

                    xml.Read();

                    if (alternate is null)
                    {
                        return false;
                    }

                    if (alternate.InBranch)
                    {
                        alternate.InBranch = false;
                    }
                    else
                    {
                        _open!.RemoveAt(_open.Count - 1);
                    }

                    break;
                }

                case XmlNodeType.None:
                    return false;

                default:
                    xml.Read();

                    break;
            }
        }
    }

    private readonly AlternateContent? Innermost => _open is { Count: > 0 } open ? open[^1] : null;

    private static bool IsMarkupCompatibility(XmlReader reader, string localName) =>
        reader.LocalName == localName && reader.NamespaceURI == WordprocessingXml.MarkupCompatibilityNamespace;

    /// <summary>The reader is on a direct child of an open <c>mc:AlternateContent</c>: enter it if it is the branch to take, else skip it.</summary>
    private readonly void SelectBranch(AlternateContent alternate)
    {
        XmlReader reader = xml.Reader;

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

        xml.SkipElement();
    }

    private readonly void EnterBranch(AlternateContent alternate)
    {
        alternate.Selected = true;

        bool empty = xml.Reader.IsEmptyElement;

        xml.Read();

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

            if (namespaceUri is null || !WordprocessingXml.IsUnderstood(namespaceUri))
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
