using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.TheForge.Ux.Services;

namespace RetroDownfall.TheForge.Ux.ViewModels.Atelier;

/// <summary>Leaf node for a registered workspace. Open focuses the Workspace Explorer dock tool.</summary>
public sealed partial class WorkspaceNodeViewModel : AtelierNodeViewModel
{

    private readonly INavigationService _navigation;

    public WorkspaceNodeViewModel(WorkspaceInfo workspace, INavigationService navigation)
    {

        _navigation = navigation;

        Workspace = workspace;

        Label = workspace.Name;

        Icon = "IconWorkspace";

    }

    public WorkspaceInfo Workspace { get; }

    public override bool HasChildren => false;

    public override ICommand? PrimaryCommand => OpenCommand;

    [RelayCommand]
    private async Task OpenAsync(CancellationToken cancellationToken) =>
        await _navigation.OpenWorkspaceAsync(Workspace.Id, cancellationToken).ConfigureAwait(true);

}
