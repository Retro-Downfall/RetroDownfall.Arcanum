namespace RetroDownfall.TheForge.Ux.ViewModels.Docking;

using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;

/// <summary>Thin content wrapper so Output can share <see cref="FoundryFloorViewModel"/> without colliding with Logs templates.</summary>
public sealed class OutputToolContent(FoundryFloorViewModel foundryFloor)
{

    public FoundryFloorViewModel FoundryFloor { get; } = foundryFloor;

}

/// <summary>Thin content wrapper for the Logs tool tab.</summary>
public sealed class LogsToolContent(FoundryFloorViewModel foundryFloor)
{

    public FoundryFloorViewModel FoundryFloor { get; } = foundryFloor;

}
