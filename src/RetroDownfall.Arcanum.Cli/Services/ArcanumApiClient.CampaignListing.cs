using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Cli.Services;

public sealed partial class ArcanumApiClient
{
    /// <summary>The page size the whole-list campaign read asks the host for, which is the host's own ceiling for it.</summary>
    private const int CampaignListPageRows = 100;

    /// <summary>
    /// Every registered campaign, following the host's continuation to its end.
    /// </summary>
    /// <remarks>
    /// The one whole-list read of campaigns, for the callers that need all of them to find one (the current
    /// directory's campaign, a saved context's). A cursor the host repeats is the
    /// <c>Api.PaginationNoProgress</c> fault rather than an accumulator that grows without bound.
    /// </remarks>
    /// <param name="cancellationToken">Stops the read between and during pages.</param>
    public async Task<Result<CampaignDto[]>> GetAllCampaignsAsync(CancellationToken cancellationToken = default)
    {
        Result<HostListing<CampaignDto>> campaigns = await HostPageWalker
            .ReadAsync<CampaignDto, int>(
                "campaign list",
                singlePage: false,
                async (offset, token) => HostPageWalker.ByOffset(
                    await GetCampaignsPageAsync(null, CampaignListPageRows, offset, token).ConfigureAwait(false),
                    offset),
                cancellationToken,
                firstPageCursor: 0)
            .ConfigureAwait(false);

        return campaigns.IsFailure
            ? Result<CampaignDto[]>.Failure(campaigns.Error)
            : Result<CampaignDto[]>.Success(campaigns.Value.Items);
    }
}
