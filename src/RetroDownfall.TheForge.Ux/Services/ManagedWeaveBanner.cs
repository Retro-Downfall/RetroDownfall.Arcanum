using RetroDownfall.TheForge.Core.Models;

namespace RetroDownfall.TheForge.Ux.Services;

/// <summary>Shared copy for managed-mode Weave / Divination banners (typed meta, not health Detail).</summary>
public static class ManagedWeaveBanner
{

    public const string ModeManaged = "managed";

    public const string Message =
        "The Weave is running in managed SIMD fallback mode. Retrieval works, but large corpora may be slower or limited to the first 50,000 scored rows. sqlite-vec acceleration is not shipped in this beta.";

    public static bool ShouldShow(InstanceMetadataDto? meta) =>
        meta is { EmbeddingsEnabled: true }
        && string.Equals(meta.EmbeddingsVectorMode, ModeManaged, StringComparison.Ordinal);

}
