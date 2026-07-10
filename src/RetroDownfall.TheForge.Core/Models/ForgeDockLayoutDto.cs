namespace RetroDownfall.TheForge.Core.Models;

/// <summary>
/// Versioned dock layout persisted inside <see cref="ForgeSettings.LayoutState"/>.
/// Pure data — no Avalonia or UX dependencies.
/// </summary>
public sealed record ForgeDockLayoutDto(
    int SchemaVersion,
    IReadOnlyList<ForgeDockToolLayoutDto> Tools,
    string? ActiveLeftToolId,
    string? ActiveRightToolId,
    string? ActiveBottomToolId,
    double LeftWidth,
    double RightWidth,
    double BottomHeight);

/// <summary>Per-tool dock placement persisted inside <see cref="ForgeDockLayoutDto"/>.</summary>
public sealed record ForgeDockToolLayoutDto(
    string ToolId,
    string Region,
    string LastRegion,
    bool IsVisible,
    int Order);
