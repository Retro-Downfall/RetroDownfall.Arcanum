using RetroDownfall.Arcanum.Core.Workspaces;

namespace RetroDownfall.TheForge.Ux.ViewModels.Atelier;

/// <summary>Leaf node for a registered workspace.</summary>
public sealed class WorkspaceNodeViewModel : AtelierNodeViewModel
{

    public WorkspaceNodeViewModel(WorkspaceInfo workspace)
    {

        Workspace = workspace;

        Label = workspace.Name;

        Icon = "IconWorkspace";

    }

    public WorkspaceInfo Workspace { get; }

    public override bool HasChildren => false;

}
