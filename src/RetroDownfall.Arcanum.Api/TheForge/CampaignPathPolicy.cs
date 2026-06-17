using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Repositories;

namespace RetroDownfall.Arcanum.Api.TheForge;

internal static class CampaignPathPolicy
{

    public static CampaignDto ToDto(Campaign campaign) =>
        new(
            campaign.Id,
            campaign.Name,
            campaign.Path,
            campaign.Type,
            campaign.Description,
            CampaignRepository.DeserializeSettings(campaign.Settings),
            campaign.CreatedAt,
            campaign.UpdatedAt);

    public static CampaignSettings DefaultSettings() => CampaignSettings.CreateDefault();

}
