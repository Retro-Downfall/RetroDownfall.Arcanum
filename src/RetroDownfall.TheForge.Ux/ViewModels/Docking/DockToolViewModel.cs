using CommunityToolkit.Mvvm.ComponentModel;

namespace RetroDownfall.TheForge.Ux.ViewModels.Docking;

/// <summary>
/// One dockable tool window. Layout metadata lives here; <see cref="Content"/> is wired by
/// <c>MainViewModel</c> and must not be created by the layout subsystem.
/// </summary>
public sealed partial class DockToolViewModel : ObservableObject
{

    [ObservableProperty]
    private DockRegion _region;

    [ObservableProperty]
    private DockRegion _lastRegion;

    [ObservableProperty]
    private bool _isVisible = true;

    [ObservableProperty]
    private int _order;

    [ObservableProperty]
    private object? _content;

    public DockToolViewModel(string toolId, string title, string? iconKey, DockRegion region, int order)
    {

        ToolId = toolId;

        Title = title;

        IconKey = iconKey;

        _region = region;

        _lastRegion = region == DockRegion.Hidden ? DockRegion.Right : region;

        _order = order;

    }

    public string ToolId { get; }

    public string Title { get; }

    public string? IconKey { get; }

    /// <summary>Owning layout; set by <see cref="DockLayoutViewModel"/> for header commands.</summary>
    public DockLayoutViewModel? Owner { get; set; }

}
