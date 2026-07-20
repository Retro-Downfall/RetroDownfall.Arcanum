using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RetroDownfall.TheForge.Ux.ViewModels.Docking;

/// <summary>A Left/Right/Bottom tab group of dock tools.</summary>
public sealed partial class DockGroupViewModel : ObservableObject
{

    [ObservableProperty]
    private DockToolViewModel? _selectedTool;

    [ObservableProperty]
    private double _size;

    public DockGroupViewModel(DockRegion region, double size)
    {

        if (region is DockRegion.Hidden)
        {

            throw new ArgumentOutOfRangeException(nameof(region), "Hidden is not a dock group.");

        }

        Region = region;

        _size = size;

        Tools.CollectionChanged += OnToolsChanged;

    }

    public DockRegion Region { get; }

    public ObservableCollection<DockToolViewModel> Tools { get; } = [];

    public bool IsCollapsed => Tools.Count == 0;

    public double EffectiveSize => IsCollapsed ? 0 : Math.Max(Size, RegionMinSize);

    /// <summary>Min width/height for this region when visible; 0 when collapsed so empty regions take no space.</summary>
    public double EffectiveMinSize => IsCollapsed ? 0 : RegionMinSize;

    private double RegionMinSize => Region switch
    {
        DockRegion.Left => DockLayoutDefaults.MinLeftWidth,
        DockRegion.Right => DockLayoutDefaults.MinRightWidth,
        DockRegion.Bottom => DockLayoutDefaults.MinBottomHeight,
        _ => 0,
    };

    public void SelectNeighborOrDefault(DockToolViewModel? removed)
    {

        if (Tools.Count == 0)
        {

            SelectedTool = null;

            return;

        }

        if (removed is not null && !ReferenceEquals(SelectedTool, removed) && SelectedTool is not null && Tools.Contains(SelectedTool))
        {

            return;

        }

        SelectedTool = Tools[0];

    }

    public void NotifyLayoutChanged()
    {

        OnPropertyChanged(nameof(IsCollapsed));

        OnPropertyChanged(nameof(EffectiveSize));

        OnPropertyChanged(nameof(EffectiveMinSize));

    }

    private void OnToolsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {

        OnPropertyChanged(nameof(IsCollapsed));

        OnPropertyChanged(nameof(EffectiveSize));

        OnPropertyChanged(nameof(EffectiveMinSize));

        if (SelectedTool is not null && !Tools.Contains(SelectedTool))
        {

            SelectedTool = Tools.Count > 0 ? Tools[0] : null;

        }
        else if (SelectedTool is null && Tools.Count > 0)
        {

            SelectedTool = Tools[0];

        }

    }

    partial void OnSizeChanged(double value) => OnPropertyChanged(nameof(EffectiveSize));

}
