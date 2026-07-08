namespace RetroDownfall.TheForge.Ux.ViewModels.Gatehouse;

/// <summary>Phase 3 placeholder for The Gatehouse; Phase 8 adds live ward polling and approve/deny actions.</summary>
public sealed class GatehouseViewModel : ViewModelBase
{

    public GatehouseViewModel()
    {

        Title = "The Gatehouse";

    }

    public string EmptyState => "No active wards — the Forge is quiet.";

}
