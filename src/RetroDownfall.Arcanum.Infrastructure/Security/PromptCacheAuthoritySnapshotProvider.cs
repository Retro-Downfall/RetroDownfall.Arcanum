using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

/// <summary>Content-free projection of one atomic publication; never opens canonical storage.</summary>
internal sealed class PromptCacheAuthoritySnapshotProvider(ICovenantRuntimeGenerationProvider runtime)
    : IPromptCacheAuthoritySnapshotProvider
{
    public PromptCacheAuthoritySnapshot Capture()
    {
        CovenantRuntimeGenerationState current = runtime.Current;

        return new(current.RuntimeAuthorityGeneration, current.ActiveAuthority, current.Availability);
    }
}
