using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.LongRest;

/// <summary>One process-owned, content-free configuration view; never execution authority.</summary>
public interface ILongRestConfigurationProvider
{
    LongRestSweepConfiguration Capture();

    bool IsCurrent(LongRestConfigurationStamp stamp);
}

public sealed record LongRestSweepConfiguration(LongRestConfigurationStamp Stamp, bool Enabled,
    bool CovenantEnabled, Result<LongRestRecipeIdentity> Recipe);
