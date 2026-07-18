using RetroDownfall.TheForge.Ux.Services;

namespace RetroDownfall.TheForge.Tests;

internal sealed class NoopSetupWizardDialogService : ISetupWizardDialogService
{

    public int ShowCount { get; private set; }

    public Task ShowAsync(CancellationToken cancellationToken = default)
    {

        ShowCount++;

        return Task.CompletedTask;

    }

}
