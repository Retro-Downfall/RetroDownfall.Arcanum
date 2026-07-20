using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Core.Services;

namespace RetroDownfall.TheForge.Ux.Services;

/// <inheritdoc cref="IActiveCampaignService"/>
public sealed class ActiveCampaignService : IActiveCampaignService
{

    private readonly ITheForgeSettingsStore _settingsStore;

    private readonly object _gate = new();

    private CampaignDto? _activeCampaign;

    public ActiveCampaignService(
        ITheForgeSettingsStore settingsStore,
        IOptionsMonitor<TheForgeSettings> settings)
    {

        _settingsStore = settingsStore;

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

        if (previousId != campaign?.Id)
        {

            await _settingsStore
                .SavePatchAsync(s => s with { LastCampaignId = campaign?.Id }, cancellationToken)
                .ConfigureAwait(false);

        }

        ActiveCampaignChanged?.Invoke(this, EventArgs.Empty);

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

            ActiveCampaignChanged?.Invoke(this, EventArgs.Empty);

        }

    }

}
