namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

/// <summary>
/// The invisible layers of one page, as a fixed bit set over the layer indices <c>0</c> to <see cref="MaximumLayers"/> - 1.
/// The size does not depend on the document: a layer index beyond the range cannot be recorded and is refused.
/// </summary>
internal sealed class VisioLayerSet
{
    internal const int MaximumLayers = 8192;

    private readonly ulong[] _invisible = new ulong[MaximumLayers / 64];

    public void MarkInvisible(int index)
    {
        if ((uint)index >= MaximumLayers)
        {
            throw new DocumentPackageRejectedException(DocumentPackageRejection.StructureBounds);
        }

        _invisible[index >> 6] |= 1UL << (index & 63);
    }

    public bool IsInvisible(int index) =>
        (uint)index < MaximumLayers && (_invisible[index >> 6] & (1UL << (index & 63))) != 0;
}
