namespace RetroDownfall.TheForge.Ux.ViewModels.Docking;

/// <summary>
/// Dock regions for tool windows. Workbench stays a fixed document host outside this enum;
/// tools may not dock into the document well in this pass.
/// </summary>
public enum DockRegion
{

    Left,

    Right,

    Bottom,

    Hidden,

}
