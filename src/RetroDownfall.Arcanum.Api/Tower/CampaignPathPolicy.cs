using System.Diagnostics.CodeAnalysis;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Repositories;

namespace RetroDownfall.Arcanum.Api.Tower;

[ExcludeFromCodeCoverage] // Reason: thin Campaign DTO mapping; CampaignRepository and endpoint tests cover wire shape.
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
