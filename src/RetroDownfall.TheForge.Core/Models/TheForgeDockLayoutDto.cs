namespace RetroDownfall.TheForge.Core.Models;

/// <summary>
/// Versioned dock layout persisted inside <see cref="TheForgeSettings.LayoutState"/>.
/// Pure data — no Avalonia or UX dependencies.
/// </summary>
public sealed record TheForgeDockLayoutDto(
    int SchemaVersion,
    IReadOnlyList<TheForgeDockToolLayoutDto> Tools,
    string? ActiveLeftToolId,
    string? ActiveRightToolId,
    string? ActiveBottomToolId,
    double LeftWidth,
    double RightWidth,
    double BottomHeight);

/// <summary>Per-tool dock placement persisted inside <see cref="TheForgeDockLayoutDto"/>.</summary>
public sealed record TheForgeDockToolLayoutDto(
    string ToolId,
    string Region,
    string LastRegion,
    bool IsVisible,
    int Order);
