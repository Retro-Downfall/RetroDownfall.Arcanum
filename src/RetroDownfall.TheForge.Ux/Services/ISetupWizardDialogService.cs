using RetroDownfall.TheForge.Ux.ViewModels.Setup;

namespace RetroDownfall.TheForge.Ux.Services;

/// <summary>Shows The Forge setup wizard as a modal dialog.</summary>
public interface ISetupWizardDialogService
{

    Task ShowAsync(CancellationToken cancellationToken = default);

}
