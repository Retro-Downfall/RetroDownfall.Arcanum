using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.TheForge.Ux.Services;

/// <summary>
/// Shared IDE active-campaign state for menus, The Anvil, New Spell/Prompt, and Campaign Open.
/// Persists <c>LastCampaignId</c> without clearing unrelated <c>the-forge.json</c> fields.
/// </summary>
public interface IActiveCampaignService
{

    CampaignDto? ActiveCampaign { get; }

    event EventHandler? ActiveCampaignChanged;

    Task SetActiveCampaignAsync(CampaignDto? campaign, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces a LastCampaignId placeholder with the real DTO after Atelier refresh when ids match.
    /// </summary>
    void HydrateIfMatching(CampaignDto campaign);

}
