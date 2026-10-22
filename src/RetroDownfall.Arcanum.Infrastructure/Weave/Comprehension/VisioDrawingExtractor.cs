using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

/// <summary>
/// Extracts the text a reader sees in a Visio drawing package (<c>.vsdx</c> and <c>.vsdm</c>, MS-VSDX): each page
/// as <c>Page: &lt;name&gt;</c> followed by the text of its shapes, one line per shape.
/// </summary>
/// <remarks>
/// <para>
/// The package is walked only through relationships (root, document, pages, masters, then each page and master part);
/// nothing is found by scanning. Shape data, user cells, hyperlinks, foreign data, comments, windows, document
/// properties, macros and every external target are never read. Hidden content (D8) is excluded: <c>HideText</c>,
/// shapes on an invisible layer, locally deleted shapes and guides, and pages hidden from the Visio user interface.
/// Text a shape inherits from its master is projected once per instance, where the instance appears; master parts are
/// never projected on their own.
/// </para>
/// <para>
/// Documented simplifications: background pages are projected like any other page; any invisible layer a shape lists
/// (or a layer-membership formula with no cached value, on a page that has an invisible layer) hides the shape and all
/// of its sub-shapes; a master shape's own layer membership is not applied to its instances; a shape with neither a
/// <c>Master</c> nor a <c>MasterShape</c> attribute inherits nothing, even inside an instantiated group.
/// </para>
/// <para>
/// Reading is forward-only. A top-level shape and its sub-shapes are buffered until the shape ends, so a hiding cell
/// that arrives after the text it hides is still honoured.
/// </para>
/// </remarks>
internal sealed class VisioDrawingExtractor : ISessionAttachmentDocumentExtractor
{
    internal const int MaximumPages = 4096;

    internal const int MaximumMasters = 4096;

    internal const int MaximumMasterTextCharacters = 262_144;

    private readonly int _maximumPages;

    private readonly int _maximumMasters;

    private readonly int _maximumMasterCharacters;

    public VisioDrawingExtractor()
        : this(MaximumPages, MaximumMasters, MaximumMasterTextCharacters)
    {
    }

    /// <summary>Test hook: the same extractor with smaller structural limits.</summary>
    internal VisioDrawingExtractor(
        int maximumPages = MaximumPages,
        int maximumMasters = MaximumMasters,
        int maximumMasterCharacters = MaximumMasterTextCharacters)
    {
        _maximumPages = maximumPages;

        _maximumMasters = maximumMasters;

        _maximumMasterCharacters = maximumMasterCharacters;
    }

    public ValueTask ExtractAsync(
        ReadOnlyMemory<byte> bytes,
        ISessionAttachmentDocumentTextSink output,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        new VisioDrawingExtraction(output, cancellationToken, _maximumPages, _maximumMasters, _maximumMasterCharacters).Run(bytes);

        return ValueTask.CompletedTask;
    }
}
