using CommunityToolkit.Mvvm.Input;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.ViewModels;
using RetroDownfall.TheForge.Ux.ViewModels.Atelier;

namespace RetroDownfall.TheForge.Tests;

internal sealed class FakeActiveCampaignService : IActiveCampaignService
{

    public CampaignDto? ActiveCampaign { get; private set; }

    public event EventHandler? ActiveCampaignChanged;

    public List<CampaignDto?> SetCalls { get; } = [];

    /// <summary>
    /// When set, the campaign write fails after yielding — mirroring a settings-store IO failure.
    /// </summary>
    public Exception? SetFailure { get; set; }

    public async Task SetActiveCampaignAsync(CampaignDto? campaign, CancellationToken cancellationToken = default)
    {

        SetCalls.Add(campaign);

        if (SetFailure is not null)
        {

            await Task.Yield();

            throw SetFailure;

        }

        ActiveCampaign = campaign;

        ActiveCampaignChanged?.Invoke(this, EventArgs.Empty);

    }

    public void HydrateIfMatching(CampaignDto campaign)
    {

        if (ActiveCampaign?.Id == campaign.Id)
        {

            ActiveCampaign = campaign;

            ActiveCampaignChanged?.Invoke(this, EventArgs.Empty);

        }

    }

}

internal sealed class NullCampaignCommandCoordinator : ICampaignCommandCoordinator
{

    public NullCampaignCommandCoordinator()
    {

        NewCampaignCommand = new AsyncRelayCommand(() => Task.CompletedTask);

        OpenCampaignCommand = new AsyncRelayCommand(() => Task.CompletedTask);

        EditCampaignCommand = new AsyncRelayCommand(() => Task.CompletedTask);

        UnregisterCampaignCommand = new AsyncRelayCommand(() => Task.CompletedTask);

        NewSpellCommand = new AsyncRelayCommand(() => Task.CompletedTask);

        NewPromptCommand = new AsyncRelayCommand(() => Task.CompletedTask);

    }

    public IAsyncRelayCommand NewCampaignCommand { get; }

    public IAsyncRelayCommand OpenCampaignCommand { get; }

    public IAsyncRelayCommand EditCampaignCommand { get; }

    public IAsyncRelayCommand UnregisterCampaignCommand { get; }

    public IAsyncRelayCommand NewSpellCommand { get; }

    public IAsyncRelayCommand NewPromptCommand { get; }

    public bool CanEditOrUnregisterCampaign => false;

    public bool CanCreateCampaignScopedArtifact => false;

    public Func<Guid, CancellationToken, Task>? FocusCampaignInAtelierAsync { get; set; }

    public event EventHandler? CanEditOrUnregisterChanged
    {
        add { }
        remove { }
    }

    public Task NewCampaignAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task OpenCampaignAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task EditActiveCampaignAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task UnregisterActiveCampaignAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task NewSpellForActiveCampaignAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task NewPromptForActiveCampaignAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

}

internal static class CampaignDialogServiceExtensions
{

    // Marker — dialog fakes updated in place to match ICampaignDialogService.

}
