namespace RetroDownfall.TheForge.Ux.ViewModels.Atelier;

/// <summary>Intermediate branch inside a campaign, such as Spells, Prompts, or Sessions.</summary>
public sealed class AtelierCategoryNodeViewModel : AtelierNodeViewModel
{

    public AtelierCategoryNodeViewModel(string label, string icon, IEnumerable<AtelierNodeViewModel> children)
    {

        Label = label;

        Icon = icon;

        foreach (AtelierNodeViewModel child in children)
        {

            Children.Add(child);

        }

    }

    protected override Task<IReadOnlyList<AtelierNodeViewModel>> LoadChildrenAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AtelierNodeViewModel>>(Children.ToArray());

}
