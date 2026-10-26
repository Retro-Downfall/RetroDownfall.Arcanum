using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Weave.Tapestry;

/// <summary>Inputs from the invocation owner. Scopes are not a delegation grant or capability.</summary>
public sealed record TapestryRetrievalRequest(
    IReadOnlyList<TapestryScope> EligibleScopes,
    float[] QueryVector,
    TapestryRetrievalIdentity Identity,
    TapestryRetrievalMode Mode,
    int MaxNodes,
    int MaxBytes,
    int MaxTokens,
    float MinSimilarity,
    ProviderSettings Provider,
    string CanonicalModel);

/// <summary>Retrieves only explicitly supplied scopes; performs no embedding or provider calls.</summary>
public interface ITapestryRetriever
{
    Task<Result<IReadOnlyList<TapestryRetrievedNode>>> RetrieveAsync(TapestryRetrievalRequest request, CancellationToken cancellationToken);
}
