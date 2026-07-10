namespace RetroDownfall.TheForge.Ux.ViewModels.Atelier;

/// <summary>Intermediate branch inside a campaign, such as Spells, Prompts, or Sessions.</summary>
public sealed class AtelierCategoryNodeViewModel : AtelierNodeViewModel
{

    private readonly IReadOnlyList<AtelierNodeViewModel> _initialChildren;

    public AtelierCategoryNodeViewModel(string label, string icon, IEnumerable<AtelierNodeViewModel> children)
    {

        Label = label;

        Icon = icon;

        _initialChildren = children.ToArray();

        foreach (AtelierNodeViewModel child in _initialChildren)
        {

            Children.Add(child);

        }

        MarkChildrenLoaded();

    }

    protected override Task<IReadOnlyList<AtelierNodeViewModel>> LoadChildrenAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_initialChildren);

}
