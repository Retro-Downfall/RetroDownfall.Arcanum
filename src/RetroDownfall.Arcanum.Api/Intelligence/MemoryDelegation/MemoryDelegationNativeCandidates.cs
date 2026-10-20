using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Resilience;

namespace RetroDownfall.Arcanum.Api.Intelligence.MemoryDelegation;

/// <summary>Native wrapper candidates retain ordinary health ordering and require native tool support.</summary>
internal static class MemoryDelegationNativeCandidates
{
    internal static IReadOnlyList<(ProviderSettings Provider, string CanonicalModelId)> Resolve(
        ArcanumSettings settings, string? model, IProviderHealthTracker? healthTracker) =>
        ProviderResolver.ResolveCandidates(settings, model, healthTracker)
            .Where(static candidate => SupportsTools(candidate.Provider, candidate.CanonicalModelId))
            .ToArray();

    internal static bool SupportsTools(ProviderSettings provider, string model) =>
        !ProviderResolver.TryResolveModelEntry(provider, model, out var entry) || entry is not { SupportsTools: false };

    internal static Error Unavailable() =>
        new(ErrorCodes.MemoryDelegation.Unavailable, "Native memory delegation is unavailable for this parent.");
}
