namespace RetroDownfall.TheForge.Ux.ViewModels.Atelier;

/// <summary>Root branch in The Atelier, such as Campaigns, Workspaces, Global Spells, or Sessions.</summary>
public sealed class AtelierRootNodeViewModel : AtelierNodeViewModel
{

    private readonly Func<CancellationToken, Task<IReadOnlyList<AtelierNodeViewModel>>> _loader;

    public AtelierRootNodeViewModel(
        string label,
        string icon,
        Func<CancellationToken, Task<IReadOnlyList<AtelierNodeViewModel>>> loader)
    {

        Label = label;

        Icon = icon;

        _loader = loader;

    }

    protected override Task<IReadOnlyList<AtelierNodeViewModel>> LoadChildrenAsync(CancellationToken cancellationToken) =>
        _loader(cancellationToken);

}
