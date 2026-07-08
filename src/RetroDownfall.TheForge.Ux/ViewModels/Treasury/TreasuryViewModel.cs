namespace RetroDownfall.TheForge.Ux.ViewModels.Treasury;

/// <summary>Phase 3 placeholder for The Treasury; Phase 9 adds budget and spend aggregation.</summary>
public sealed class TreasuryViewModel : ViewModelBase
{

    public TreasuryViewModel()
    {

        Title = "The Treasury";

    }

    public string EmptyState => "Budget and spend tracking arrives in Phase 9.";

}
