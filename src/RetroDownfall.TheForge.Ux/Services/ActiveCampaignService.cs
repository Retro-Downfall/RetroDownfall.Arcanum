using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Core.Services;
using RetroDownfall.TheForge.Ux.Services.Whispers;

namespace RetroDownfall.TheForge.Ux.Services;

/// <inheritdoc cref="IActiveCampaignService"/>
public sealed class ActiveCampaignService : IActiveCampaignService
{

    private readonly ITheForgeSettingsStore _settingsStore;

    private readonly IUiThreadDispatcher _uiThread;

    private readonly object _gate = new();

    private CampaignDto? _activeCampaign;

    public ActiveCampaignService(
        ITheForgeSettingsStore settingsStore,
        IOptionsMonitor<TheForgeSettings> settings,
        IUiThreadDispatcher uiThread)
    {

        _settingsStore = settingsStore;

        _uiThread = uiThread;

        Guid? lastId = settings.CurrentValue.LastCampaignId;

        if (lastId is { } id)
        {

            // Placeholder until Atelier loads the real DTO; Anvil shows name when available.
            _activeCampaign = new CampaignDto(
                id,
                $"Campaign {id:D}",
                string.Empty,
                Arcanum.Core.Workspaces.WorkspaceType.Campaign,
                null,
                CampaignSettings.CreateDefault(),
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch);

        }

    }

    public CampaignDto? ActiveCampaign
    {
        get
        {
            lock (_gate)
            {
                return _activeCampaign;
            }
        }
    }

    public event EventHandler? ActiveCampaignChanged;

    public async Task SetActiveCampaignAsync(CampaignDto? campaign, CancellationToken cancellationToken = default)
    {

        Guid? previousId;

        lock (_gate)
        {

            previousId = _activeCampaign?.Id;

            _activeCampaign = campaign;

        }

        try
        {

            if (previousId != campaign?.Id)
            {

                await _settingsStore
                    .SavePatchAsync(s => s with { LastCampaignId = campaign?.Id }, cancellationToken)
                    .ConfigureAwait(false);

            }

        }
        finally
        {

            // The new campaign is already published from ActiveCampaign, so the notification has to
            // go out even when persisting LastCampaignId failed. Skipping it leaves a split brain:
            // the service answers with the new campaign while every bound surface renders the old
            // one. The write failure still propagates to the caller, which surfaces it.
            RaiseActiveCampaignChanged();

        }

    }

    public void HydrateIfMatching(CampaignDto campaign)
    {

        bool raise;

        lock (_gate)
        {

            if (_activeCampaign is null || _activeCampaign.Id != campaign.Id)
            {

                return;

            }

            raise = !string.Equals(_activeCampaign.Name, campaign.Name, StringComparison.Ordinal)
                || !string.Equals(_activeCampaign.Path, campaign.Path, StringComparison.Ordinal);

            _activeCampaign = campaign;

        }

        if (raise)
        {

            RaiseActiveCampaignChanged();

        }

    }

    /// <summary>
    /// Every subscriber is a view model whose handler mutates bound state, so the notification must
    /// land on the UI thread. Both raise sites can run on a worker: <see cref="SetActiveCampaignAsync"/>
    /// continues on whatever thread completed the settings write, and <see cref="HydrateIfMatching"/>
    /// is called from background hydration. <c>ConfigureAwait(true)</c> is not enough — a worker with
    /// no synchronization context simply resumes on the pool.
    /// </summary>
    private void RaiseActiveCampaignChanged()
    {

        if (_uiThread.CheckAccess())
        {

            ActiveCampaignChanged?.Invoke(this, EventArgs.Empty);

            return;

        }

        _uiThread.Post(() => ActiveCampaignChanged?.Invoke(this, EventArgs.Empty));

    }

}
