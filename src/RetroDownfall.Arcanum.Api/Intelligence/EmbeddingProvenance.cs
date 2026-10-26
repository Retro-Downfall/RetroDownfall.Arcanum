using RetroDownfall.Arcanum.Core.Weave.Tapestry;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>Transient first-party identity captured with the resolved generator lease.</summary>
internal sealed record EmbeddingProvenance(string Provider, string Model)
{
    internal const string MetadataKey = "Arcanum.EmbeddingProvenance";

    internal string Identity => TapestryHash.OfEmbeddingModel(Provider, Model);
}
