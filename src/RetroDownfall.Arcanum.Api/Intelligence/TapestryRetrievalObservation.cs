using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>Closed immutable nonwire data; contains neither prompt content nor an effect grant.</summary>
internal sealed class TapestryRetrievalObservation(TapestryInvocationSelection selection,
    TapestryRetrievalIdentity identity, IEnumerable<TapestryScopeGeneration> generations) : ITapestryRetrievalObservation
{
    internal TapestryInvocationSelection Selection { get; } = selection;

    internal TapestryRetrievalIdentity Identity { get; } = identity;

    internal IReadOnlyList<TapestryScopeGeneration> Generations { get; } = Array.AsReadOnly(generations.Distinct().ToArray());
}

internal sealed record TapestryScopeGeneration(TapestryScope Scope, string GenerationId);
