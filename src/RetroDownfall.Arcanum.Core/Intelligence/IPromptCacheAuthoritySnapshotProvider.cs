using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>Read-only published metadata; grants no protected read or dispatch authority.</summary>
public interface IPromptCacheAuthoritySnapshotProvider
{
    PromptCacheAuthoritySnapshot Capture();
}

public sealed record PromptCacheAuthoritySnapshot(
    long RuntimeAuthorityGeneration,
    CovenantAuthoritySnapshot? Authority,
    CovenantAvailabilitySnapshot Availability);
