using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.Repositories;

namespace RetroDownfall.Arcanum.Api.TheForge;

internal static class PingRequestResolver
{

    public static async Task<Result<PingRequest>> ResolveCampaignAsync(
        PingRequest request,
        ICampaignRepository campaignRepository,
        CancellationToken cancellationToken)
    {
        if (request.CampaignId is not Guid campaignId)
        {
            return Result<PingRequest>.Success(request);
        }

        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory))
        {
            return Result<PingRequest>.Success(request);
        }

        Campaign? campaign = await campaignRepository
            .GetByIdAsync(campaignId, cancellationToken)
            .ConfigureAwait(false);

        if (campaign is null)
        {
            return Result<PingRequest>.Failure(
                new Error("Campaign.NotFound", "No campaign exists with that identifier."));
        }

        return Result<PingRequest>.Success(request with { WorkingDirectory = campaign.Path });
    }

}
