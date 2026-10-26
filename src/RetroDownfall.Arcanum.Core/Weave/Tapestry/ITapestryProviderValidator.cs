using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Weave.Tapestry;

/// <summary>First-party source/currentness check at the actual provider boundary; carries no authority.</summary>
public interface ITapestryProviderValidator
{
    Task<Result> ValidateAsync(string provider, string model, bool embedding, CancellationToken cancellationToken);
}
