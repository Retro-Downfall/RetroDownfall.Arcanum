using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using RetroDownfall.TheForge.Ux.ViewModels.WorkspaceExplorer;

namespace RetroDownfall.TheForge.Ux.Views.WorkspaceExplorer;

public partial class WorkspaceExplorerView : UserControl
{

    public WorkspaceExplorerView()
    {

        InitializeComponent();

    }

    private async void OnEntriesDoubleTapped(object? sender, TappedEventArgs e)
    {

        if (DataContext is not WorkspaceExplorerViewModel vm)
        {

            return;

        }

        await vm.OpenDirectoryCommand.ExecuteAsync(null).ConfigureAwait(true);

    }

}
