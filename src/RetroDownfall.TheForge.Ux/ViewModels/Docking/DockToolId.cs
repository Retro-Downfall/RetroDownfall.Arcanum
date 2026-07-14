namespace RetroDownfall.TheForge.Ux.ViewModels.Docking;

/// <summary>Stable string ids for dockable tool windows (persisted in layout state).</summary>
public static class DockToolId
{

    public const string Atelier = "atelier";

    public const string Gatehouse = "gatehouse";

    public const string Treasury = "treasury";

    public const string Arsenal = "arsenal";

    public const string WarTable = "warTable";

    public const string Output = "output";

    public const string Logs = "logs";

    public const string Hearth = "hearth";

    public const string Lore = "lore";

    public const string Archive = "archive";

    public const string Divination = "divination";

    public const string WorkspaceExplorer = "workspaceExplorer";

    public static IReadOnlyList<string> All { get; } =
    [
        Atelier,
        Gatehouse,
        Treasury,
        Arsenal,
        WarTable,
        Output,
        Logs,
        Hearth,
        Lore,
        Archive,
        Divination,
        WorkspaceExplorer,
    ];

}
