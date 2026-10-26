using RetroDownfall.Arcanum.Core.Weave.Tapestry;

namespace RetroDownfall.Arcanum.Core.Weave;

/// <summary>Transient first-party identity captured with the resolved generator lease.</summary>
public sealed record EmbeddingProvenance(string Provider, string Model)
{
    public const string MetadataKey = "Arcanum.EmbeddingProvenance";

    public string Identity => TapestryHash.OfEmbeddingModel(Provider, Model);
}
