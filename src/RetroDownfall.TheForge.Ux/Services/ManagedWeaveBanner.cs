using RetroDownfall.TheForge.Core.Models;

namespace RetroDownfall.TheForge.Ux.Services;

/// <summary>Shared copy for managed-mode Weave / Divination banners (typed meta, not health Detail).</summary>
public static class ManagedWeaveBanner
{

    public const string ModeManaged = "managed";

    public const string Message =
        "The Weave uses complete streamed managed SIMD search over durable embedding BLOBs. Every eligible row is scored with bounded top-K memory and no total row budget. Large corpora may take longer to search.";

    public static bool ShouldShow(InstanceMetadataDto? meta) =>
        meta is { EmbeddingsEnabled: true }
        && string.Equals(meta.EmbeddingsVectorMode, ModeManaged, StringComparison.Ordinal);

}
