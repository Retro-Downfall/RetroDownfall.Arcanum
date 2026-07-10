namespace RetroDownfall.TheForge.Ux.ViewModels.Docking;

/// <summary>Default Forge shell layout matching the pre-docking fixed regions.</summary>
public static class DockLayoutDefaults
{

    public const int SchemaVersion = 1;

    public const double DefaultLeftWidth = 260;

    public const double DefaultRightWidth = 330;

    public const double DefaultBottomHeight = 190;

    public const double MinLeftWidth = 160;

    public const double MaxLeftWidth = 600;

    public const double MinRightWidth = 180;

    public const double MaxRightWidth = 700;

    public const double MinBottomHeight = 100;

    public const double MaxBottomHeight = 500;

    public static IReadOnlyList<(string ToolId, string Title, string? IconKey, DockRegion Region, int Order)> Tools { get; } =
    [
        (DockToolId.Atelier, "The Atelier", "IconCampaign", DockRegion.Left, 0),
        (DockToolId.Gatehouse, "The Gatehouse", "IconWard", DockRegion.Right, 0),
        (DockToolId.Treasury, "The Treasury", "IconModel", DockRegion.Right, 1),
        (DockToolId.Arsenal, "The Arsenal", "IconMcp", DockRegion.Right, 2),
        (DockToolId.WarTable, "The War Table", "IconApprentice", DockRegion.Right, 3),
        (DockToolId.Output, "Output", "IconSession", DockRegion.Bottom, 0),
        (DockToolId.Logs, "Logs", "IconCodex", DockRegion.Bottom, 1),
        (DockToolId.Hearth, "The Hearth", "IconSanctum", DockRegion.Bottom, 2),
    ];

    public static DockRegion DefaultRegionFor(string toolId)
    {

        foreach ((string id, string _, string? _, DockRegion region, int _) in Tools)
        {

            if (id == toolId && region != DockRegion.Hidden)
            {

                return region;

            }

        }

        return DockRegion.Right;

    }

}
