using System.Runtime.InteropServices;
using System.Xml;

namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

/// <summary>
/// A forward-only, hardened <see cref="XmlReader"/> over one untrusted package part. DOCTYPE declarations are
/// prohibited (or, for the one reviewed ODF manifest case, ignored), nothing is ever resolved, element depth and
/// the total node count are bounded, and every failure is a closed <see cref="DocumentPackageRejection"/>.
/// </summary>
/// <remarks>
/// <para>
/// Callers read through <see cref="Read"/> and <see cref="SkipElement"/>, never through <see cref="Reader"/>'s own
/// <c>Read</c>/<c>Skip</c>, so the depth guard, the cancellation check and the <see cref="XmlException"/> translation
/// cannot be bypassed. <see cref="Reader"/> is exposed for property access only
/// (<c>NodeType</c>, <c>LocalName</c>, <c>NamespaceURI</c>, <c>Value</c>, <c>IsEmptyElement</c>, <c>Depth</c>,
/// <c>GetAttribute</c>).
/// </para>
/// <para>
/// With <c>DtdProcessing.Prohibit</c> any DOCTYPE fails the part. With <c>DtdProcessing.Ignore</c> the declaration is
/// skipped without being processed: no entity can be declared, so any entity reference that is not one of the five
/// predefined ones fails as an undeclared entity, and the resolver is still null.
/// </para>
/// </remarks>
internal sealed class BoundedXmlReader : IDisposable
{
    internal const int MaximumDepth = 256;

    internal const int CancellationInterval = 1024;

    private const int MaximumCharactersFromEntities = 1024;

    private readonly CancellationToken _cancellationToken;

    private long _nodes;

    private BoundedXmlReader(XmlReader reader, CancellationToken cancellationToken)
    {
        Reader = reader;

        _cancellationToken = cancellationToken;
    }

    /// <summary>The underlying reader, for property access only. Advance with <see cref="Read"/> and <see cref="SkipElement"/>.</summary>
    public XmlReader Reader { get; }

    /// <summary>Opens a hardened reader over <paramref name="part"/>; the bytes are read in place, not copied when array-backed.</summary>
    public static BoundedXmlReader Create(ReadOnlyMemory<byte> part, CancellationToken cancellationToken, bool ignoreDocumentType = false)
    {
        XmlReaderSettings settings = new()
        {
            DtdProcessing = ignoreDocumentType ? DtdProcessing.Ignore : DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = MaximumCharactersFromEntities,
            MaxCharactersInDocument = BoundedZipPackage.MaximumEntryBytes,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = false,
            CloseInput = true,
        };

        MemoryStream stream = OpenStream(part);

        try
        {
            return new BoundedXmlReader(XmlReader.Create(stream, settings), cancellationToken);
        }
        catch (XmlException)
        {
            stream.Dispose();

            throw new DocumentPackageRejectedException(DocumentPackageRejection.MalformedXml);
        }
    }

    /// <summary>Advances to the next node. Returns false at the end of the document.</summary>
    public bool Read()
    {
        if (++_nodes % CancellationInterval == 0)
        {
            _cancellationToken.ThrowIfCancellationRequested();
        }

        bool more;

        try
        {
            more = Reader.Read();

            if (more && Reader.HasValue)
            {
                // XmlReader completes some text lazily, on the first Value access. Touching it here keeps every
                // XmlException inside this method, so a caller reading Reader.Value can never see a raw one.
                _ = Reader.Value;
            }
        }
        catch (XmlException)
        {
            throw new DocumentPackageRejectedException(DocumentPackageRejection.MalformedXml);
        }

        if (more && Reader.Depth > MaximumDepth)
        {
            throw new DocumentPackageRejectedException(DocumentPackageRejection.StructureBounds);
        }

        return more;
    }

    /// <summary>
    /// Consumes the whole subtree of the element the reader is positioned on (just that node for an empty element),
    /// leaving the reader on the node after it. The depth, cancellation and well-formedness rules still apply inside.
    /// </summary>
    /// <exception cref="InvalidOperationException">The reader is not positioned on an element start (a caller bug).</exception>
    public void SkipElement()
    {
        if (Reader.NodeType != XmlNodeType.Element)
        {
            throw new InvalidOperationException("SkipElement requires the reader to be positioned on an element.");
        }

        if (Reader.IsEmptyElement)
        {
            Read();

            return;
        }

        int depth = Reader.Depth;

        while (Read())
        {
            if (Reader.NodeType == XmlNodeType.EndElement && Reader.Depth == depth)
            {
                Read();

                return;
            }
        }
    }

    public void Dispose() => Reader.Dispose();

    private static MemoryStream OpenStream(ReadOnlyMemory<byte> part)
    {
        if (MemoryMarshal.TryGetArray(part, out ArraySegment<byte> segment) && segment.Array is not null)
        {
            return new MemoryStream(segment.Array, segment.Offset, segment.Count, writable: false);
        }

        byte[] copy = part.ToArray();

        return new MemoryStream(copy, 0, copy.Length, writable: false);
    }
}
