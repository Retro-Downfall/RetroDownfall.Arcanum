namespace RetroDownfall.Arcanum.Core.Intelligence.MemoryDelegation;

public static class MemoryDelegationLimits
{
    public const int PolicyVersion = 1;

    public const int MinTargets = 1;

    public const int MaxTargets = 16;

    public const int ManifestCoordinates = 128;

    public const int RenderedMemoryBytes = 32 * 1024;

    public const int EvidenceBytes = 64 * 1024;

    public const int ActiveGrantsPerParent = 1;

    public const int ActiveGrantsPerProcess = 32;

    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromMinutes(5);
}
