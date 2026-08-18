using Avalonia.Controls;
using Avalonia.Input;
using RetroDownfall.TheForge.Ux.ViewModels;
using RetroDownfall.TheForge.Ux.ViewModels.Docking;

namespace RetroDownfall.TheForge.Ux.Views.Docking;

/// <summary>
/// Hosts the Left/Workbench/Right columns and the Bottom row. The Grid definitions bind one-way to each
/// group's computed <c>EffectiveSize</c>, so a GridSplitter drag moves the Grid without telling the
/// layout ViewModel anything. These handlers commit the resolved definition size back to the group
/// after each drag, which is what makes a resized region survive the next show/hide/move notification
/// and reach the persisted layout state.
/// </summary>
public partial class DockHostView : UserControl
{

    public DockHostView()
    {

        InitializeComponent();

    }

    private void OnLeftSplitterDragCompleted(object? sender, VectorEventArgs e) =>
        CommitRegionSize(DockRegion.Left, ColumnsGrid.ColumnDefinitions[0].ActualWidth);

    private void OnRightSplitterDragCompleted(object? sender, VectorEventArgs e) =>
        CommitRegionSize(DockRegion.Right, ColumnsGrid.ColumnDefinitions[4].ActualWidth);

    private void OnBottomSplitterDragCompleted(object? sender, VectorEventArgs e) =>
        CommitRegionSize(DockRegion.Bottom, RootGrid.RowDefinitions[2].ActualHeight);

    private void CommitRegionSize(DockRegion region, double size)
    {

        if (DataContext is MainViewModel main)
        {

            main.DockLayout.ApplyRegionSize(region, size);

        }

    }

}
